# CLAUDE.md — Blade Compiler Project

## What is Blade?

Blade is a tiny systems programming language that compiles to PASM2 (Propeller 2 assembly).
It targets COG execution mode on the Parallax Propeller 2 microcontroller. Think "C for the
PDP-11" — a high-level assembler with Zig/Rust-inspired syntax where every construct maps to
1–3 PASM2 instructions. See `Docs/reference.blade` for the full language spec.

## Host Environment

The following tools are available on the host:

- **`dotnet`** — .NET SDK for building and testing the compiler.
- **`flexspin`** — Parallax FlexSpin compiler (v6.9.10+). Used to validate
  emitted PASM2 output.

### Validating PASM Output with FlexSpin

FlexSpin can assemble and check PASM2 output. Use `-2` for Prop2 mode and `-c`
to compile only DAT sections (pure PASM, no Spin wrapper):

```sh
# Assemble a PASM2 file, check for errors
flexspin -2 -c -q output.spin2

# Produce a listing for inspection
flexspin -2 -c -l -q output.spin2

# Full binary (if needed for integration tests)
flexspin -2 -q -o output.bin output.spin2
```

The emitted PASM must be wrapped in a minimal Spin2/PASM2 DAT block for FlexSpin
to accept it. The codegen should emit something like:

```spin2
DAT
    org 0
    ' --- Blade compiler output ---
    ' (PASM2 instructions here)
```

Use `-q` (quiet) to suppress the banner in CI. FlexSpin's exit code is 0 on success,
nonzero on assembly errors — suitable for test assertions.

## Language & Tooling

- **Language**: C#
- **Test framework**: NUnit
- **Output format**: PASM2 assembly (text, wrapped for FlexSpin)
- **Parser style**: Handrolled recursive descent — no parser generators

## Architecture

The compiler pipeline is roughly:

1. **Lexer** → token stream
2. **Parser** → AST (recursive descent, no backtracking)
3. **Semantic analysis** → typed AST, call-graph analysis, CC tiering
4. **SSA lowering** → SSA IR with backwards register allocation
5. **Codegen** → PASM2 text output

Build incrementally. Each stage should be independently testable.

## Coding Style

- **Explicit style.** Prefer clarity over brevity.
- **Minimal exceptions.** Use result types / error returns for expected failures.
  Exceptions are for programmer errors (bugs), not user input errors.
- **Proper error handling.** Every error path must be handled, not swallowed.
- **Assert invariants, don't handle programmer errors.** When a condition must
  hold because earlier pipeline stages guarantee it, use `Debug.Assert` to
  document the invariant — never write fallback code that silently handles
  "impossible" states. Defensive programming means asserting that invariants
  hold, not writing unreachable recovery code that hides intent and makes the
  code harder to reason about.
- **No `var` where the type isn't obvious.** Prefer explicit types on declarations
  when the RHS doesn't make the type self-evident.
- **Validate externally visible inputs with `Blade/Requires.cs`.** When analyzers
  require argument validation, use the shared helpers such as `Requires.NotNull`,
  `Requires.NonNegative`, `Requires.Positive`, and `Requires.InRange` instead of
  ad hoc guard code.
- **`Blade/.editorconfig` is the analyzer source of truth.** Keep the enabled and
  disabled rule set documented there together with the reasoning for each choice.
- **In-source suppressions are exceptional.** A `#pragma`, attribute suppression,
  or similar local mute is allowed only for a single occurrence with a strong
  explanation of why the rule does not apply, and that reasoning must be clear to
  the programmer who will review or maintain the code later.

## Syntax & Grammar Changes

- **`Docs/reference.blade` is the syntax authority.** Every change to the parser,
  lexer, or AST must be justified by a construct defined in `Docs/reference.blade`.
  Do not invent syntax that is not in the reference.
- **Mirror syntax changes into the shipped docs and editor grammar.** Any token,
  keyword, operator, or syntactic form added to or changed in the compiler frontend
  must also be reflected in `VSCode/blade-lang/syntaxes/blade.tmLanguage.json` and
  documented in `Docs/Blade.md` so that editor highlighting and user-facing language
  documentation stay in sync with the implementation.
- **Update tests.** Parser and lexer changes require corresponding test updates in
  `Blade.Tests/`.

## Diagnostics

Every diagnostic (error, warning, info) must have:

1. A **numeric code** (e.g., `E1001`, `W2003`)
2. A **human-readable name** (e.g., `StackDepthExceeded`)
3. A **format string** with placeholders for context

Prefix conventions:
- `E` = error (compilation fails)
- `W` = warning (compilation continues)
- `I` = info / note

**Use T4 text templates** (`.tt` files) to project diagnostic codes from a dense
definition table. This allows easy refactoring, renumbering, and ensures the code,
name, and format string stay in sync. Example dense definition:

```
E1001 | StackDepthExceeded  | Call depth {0} exceeds hardware stack limit of 8. Consider 'rec fn'.
E1002 | RegisterOverflow    | Code + data require {0} registers, exceeding the {1} available.
W2001 | UnusedVariable      | Variable '{0}' is declared but never used.
```

The T4 template generates the actual C# diagnostic classes/enums from this table.

## Testing

### Coverage Requirement

All new code must have **100% branch coverage** through testing. Run `just coverage`
to verify. The report covers the `Blade` assembly (the regression suite contributes
to coverage but is not itself measured) and writes results to
`coverage/coverage.cobertura.xml`.

To query uncovered lines in a specific class and line range:

```sh
python Scripts/coverage_range.py <class_pattern> <start_line> <end_line>
# Example: python Scripts/coverage_range.py Binder 652 740
```

### Regression Harness

The main behavioral gate is the external regression harness in `Blade.Regressions`.
It discovers fixtures under `Examples/`, `Demonstrators/`, and `RegressionTests/`,
matches diagnostics and generated code against header expectations, and validates
hand-written assembly through FlexSpin.

- `Examples/` must stay pristine: no expectation headers, zero diagnostics only.
- `Demonstrators/` may contain expectation headers and are part of the regression corpus.
- `RegressionTests/` holds focused bug, diagnostic, and assembly fixtures.
- Use `EXPECT: fail` only for malformed or semantically invalid programs where failure is the correct result.
- Use `EXPECT: xfail` for valid programs that should compile but currently fail because of a known compiler bug or missing implementation, such as an unimplemented lowering.

The header format and directive semantics are documented in `Docs/TestHarness.md`.

### Smoke Testing New Features

**Never use ad-hoc temp files for smoke testing.** When testing a new compiler
feature, create a demonstrator or regression test file first and use
`just compile-sample <path>` or `just regressions` to test it. Demonstrator
files are reusable, checked into the repo, and serve as permanent regression
fixtures.

### Accept/Reject Test Files

`Blade.Tests/Accept` and `Blade.Tests/Reject` still exist for focused NUnit
coverage of parser/binder diagnostics. They use the older first-line diagnostic-code
convention and are not the primary compiler-quality gate.

### Unit Tests

Standard NUnit tests in `Blade.Tests/` for individual compiler components:
lexer token sequences, parser AST shapes, semantic checks, codegen output.

Run with:
```sh
just test
```

When a unit test needs filesystem access, use `Blade.Tests/TempDirectory` instead of hand-rolled `Path.GetTempPath()` / manual cleanup logic.

### Build Hygiene

- **Builds must be warning-free.** `dotnet build`, `just build`, and the full local
  verification flow are expected to complete with zero warnings. New warnings are
  treated as regressions and should be fixed rather than tolerated.

### Justfile Commands

The repo has a `justfile` with a few convenience commands:

- `just all` runs the usual local verification sequence: `build`, `test`,
  `regressions`, and `compile-all-samples`.
- `just build` runs `dotnet build`.
- `just test` runs `dotnet test`.
- `just coverage` collects code coverage (including `Blade.Regressions`),
  writes a stable `coverage/coverage.cobertura.xml`, and prints a summary.
- `just regressions` runs the external regression harness over the full fixture corpus.
- `just compile-all-samples` builds Blade and compiles the checked-in sample and
  demonstrator `.blade` programs, writing `*.dump.txt` files next to each sample.
- `just compile-sample <path>` runs `Blade/bin/Debug/net10.0/blade --dump-all`
  for one sample path and writes the dump beside the source file.

## Generated Instruction Metadata

- All instruction-related metadata queries must go through `Blade/P2InstructionMetadata.g.cs`.
- Regenerate with:
  ```sh
  source .venv/bin/activate
  python Scripts/extract.py
  ```
- `Scripts/extract.py` records the generation timestamp and command line in the generated file
  header. If the workbook changes, rerun the generator and review the resulting diff.

