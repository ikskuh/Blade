# CLAUDE.md — Blade Compiler Project

## What is Blade?

Blade compiles to PASM2 (Propeller 2 assembly) targeting COG execution mode.
Every construct maps to 1–3 PASM2 instructions. Language spec: `Docs/reference.blade`.

## Host Tools

- **`dotnet`** — .NET SDK (build + test).
- **`flexspin`** (v6.9.10+) — validates emitted PASM2. Use `-2 -c -q` for Prop2 DAT-only assembly; exit code 0 = success.

Emitted PASM must be wrapped in a DAT block (`DAT` / `org 0`) for FlexSpin to accept it.

## Architecture

C#, NUnit, handrolled recursive descent parser.

Pipeline: Lexer → Parser (AST) → Semantic analysis (typed AST, CC tiering) → SSA lowering (backwards reg alloc) → Codegen (PASM2 text).

Each stage independently testable.

## Coding Style

- Prefer clarity over brevity. No `var` where the type isn't obvious from the RHS.
- Result types / error returns for expected failures. Exceptions only for bugs.
- Every runtime error path handled, never swallowed.
- `Debug.Assert` for invariants guaranteed by earlier stages — never write unreachable fallback code.
- Argument validation via `Blade/Requires.cs` (`Requires.NotNull`, `.NonNegative`, `.Positive`, `.InRange`).
- `Blade/.editorconfig` is the analyzer rule source of truth.
- In-source suppressions (`#pragma`, attributes) only for single occurrences with a clear explanation.

## Syntax & Grammar Changes

- **`Docs/reference.blade` is the syntax authority.** Do not invent syntax not in the reference.
- Mirror syntax changes into `VSCode/blade-lang/syntaxes/blade.tmLanguage.json`.
- Parser/lexer changes require corresponding test updates in `Blade.Tests/`.

## Diagnostics

Every diagnostic needs: numeric code (`E`/`W`/`I` prefix), human-readable name, format string.

## Finding Bugs

When something unexpected happens or a compiler bug emerges, immediately log it into `Work/Bugs.md`. Otherwise the bug will be lost forever.

When code appears unreachable, immediately log it into `Work/Unreachable.md`.

## Testing

Always prefer a new code sample in `Demonstrators/` over a dedicated unit test in `Blade.Tests`.
A unit test asserts code, a Demonstrator asserts compiler compliance.

**100% branch coverage required.** Run `just coverage`; query uncovered lines:
`python Scripts/coverage_range.py <class_pattern> <start_line> <end_line>`.

Prefer writing a demonstrator file over a unit test to reach coverage.

Code paths that are not provably reachable through the compiler frontend are dead code and shall be
replaced with assertions.

### Regression Harness (`Blade.Regressions`)

Main behavioral gate. Discovers fixtures under `Examples/`, `Demonstrators/`, `RegressionTests/`.
Matches diagnostics and codegen against header expectations; validates assembly via FlexSpin.

- `Examples/` — pristine, no headers, zero diagnostics only.
- `Demonstrators/` — may have expectation headers, part of regression corpus.
- `RegressionTests/` — focused bug/diagnostic/assembly fixtures.
- `EXPECT: fail` — malformed or semantically invalid programs.
- `EXPECT: xfail` — valid programs that fail due to known compiler gaps.

Header format: `Docs/TestHarness.md`.

### Other Tests

- `Blade.Tests/Accept` and `Blade.Tests/Reject` — focused NUnit parser/binder diagnostic coverage.
- Unit tests in `Blade.Tests/` — use `Blade.Tests/TempDirectory` for filesystem access.
- **Never use ad-hoc temp files.** Create demonstrator/regression files and use `just compile-sample <path>` or `just regressions`.

### Build Hygiene

Builds must be **warning-free**. New warnings are regressions.

## Justfile Commands

| Command | Description |
|---|---|
| `just all` | Full local verification: build, test, regressions, compile-all-samples |
| `just build` | `dotnet build` |
| `just test` | `dotnet test` |
| `just coverage` | Collect coverage → `coverage/coverage.cobertura.xml` |
| `just regressions` | Run full regression harness |
| `just compile-all-samples` | Compile all `.blade` samples, write `*.dump.txt` |
| `just compile-sample <path>` | Run `blade --dump-all` for one sample |

## Generated Instruction Metadata

Queries go through `Blade/P2InstructionMetadata.g.cs`. Regenerate: `source .venv/bin/activate && python Scripts/extract.py`.
