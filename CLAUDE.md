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

## Task Completion

Your task is completed when:

- You have fulfilled the original prompt
- `just accept-changes` passes
- The code coverage for your new code is 100% line coverage
- You have performed a review of your own changes through `git diff`

## Coding Style

- Prefer clarity over brevity. No `var` where the type isn't obvious from the RHS.
- Result types / error returns for expected failures. Exceptions only for bugs.
- Every runtime error path handled, never swallowed.
  - Violation of invariants is not a runtime error, it's a bug.
    Bugs get hidden if "handled". Assert invariants instead of handling invariant violation.
- Unreachable switch cases `throw new UnreachableException()` instead of returning wrong values
- `Assert.Invariant(cond)` for invariants guaranteed by earlier stages — never write unreachable fallback code.
- `Assert.Unreachable()` for statements that should never be reachable.
- `Assert.UnreachableValue<T>()` for expressions that should never be reachable. `<T>` is the expected result. Useful for unreachable branches in switches.
- Argument validation via `Blade/Requires.cs` (`Requires.NotNull`, `.NonNegative`, `.Positive`, `.InRange`).
- `Blade/.editorconfig` is the analyzer rule source of truth.
- In-source suppressions (`#pragma`, attributes) only for single occurrences with a clear explanation.
- Prefer primary constructors for data classes
- Don't use trinary operators for control flow. Use local variables or switch pattern matching instead.
- We prefer "one file per responsibility" over split functionality. Some files or classes like `Binder` or `AsmLowerer` have a really large surface, but still only have a single task. These tasks are complex and it's better to keep code that belongs together in a single file.

## Type Safety Rules

- Prefer strict and specialized typing throughout the compiler pipeline. Preserve semantic identity and provenance through dedicated types and object references instead of flattening data into text or numbers.
- `string` is only legal at true text boundaries:
  - source parsing and unresolved name lookup
  - diagnostics and dump writers
  - final assembly emission
- `int` is only legal for arithmetic, sizes, widths, offsets, counts, indices, and validated physical register addresses. It must not be used as an identifier or as a stand-in for a distinct domain type.
- Do not introduce `string`-based or `int`-based "typing". If a value has a domain meaning, introduce a dedicated type, enum, interface, or stage object instead.
- Do not compare names or ids when object identity, provenance, or a dedicated enum can be used instead.
- Do not use bare `object` or `object?` types, unless there's a really good reason for it.
  - If you need a "union type", use a custom class with invariants (example: `BladeValue`) instead of a broadband type like `object`.
- Frontend semantic symbols, MIR/LIR control-flow identities, and ASM-visible symbols are different domains and must not be collapsed into a shared weak representation.
- Compatibility shims that accept raw `string` or `int` in typed compiler-internal APIs are forbidden unless the API is explicitly a parse/emission boundary.

## Refactoring Policy

- Leave a place better than you found it.
- Never introduce compatibility layers. Refactor fully, never just enough to make it work.
- Prefer deletion-first refactoring for type-safety work.
- When a stringly-typed or int-typed compatibility path is wrong, delete it first and then fix all call sites to use the correct typed path.
- Do not preserve obsolete constructors, overloads, helper properties, or test-only shims just to avoid fallout.
- Make tests fit the compiler, not the compiler fit outdated tests. If a test only validates an obsolete abstraction boundary, rewrite or delete the test instead of reintroducing the old behavior.
- Prefer carrying references to prior-stage objects over recreating identity from names. MIR should point to bound/semantic objects, LIR should point to MIR objects, and ASM should point to LIR or dedicated ASM symbol objects as appropriate.

## Definition Of Done

Before reporting a task as completed:

- `just accept-changes` must return without any findings.
- Your changes must have 100% code coverage.
- All language-related changes must have a positive and negative demonstrator file inside `Demonstrators/`.

## Syntax & Grammar Changes

- **`Docs/reference.blade` is the syntax authority.** Do not invent syntax not in the reference.
- Mirror syntax changes into `VSCode/blade-lang/syntaxes/blade.tmLanguage.json`.
- Parser/lexer changes require corresponding test updates in `Blade.Tests/`.

## Diagnostics

Every diagnostic needs: numeric code (`E`/`W`/`I` prefix), human-readable name, format string.

## Development Assistance

### C#

You can use the Serena MCP server if available for quick navigation and editing on the code base.

### Blade Compiler

You can use the Blade MCP server if available to get quicker results from the compiler, as you don't
need to roundtrip through shell commands and shell output processing. It gives you quick filtering of
the desired outputs.

Prefer the Blade MCP server over manual shell invocation whenever it exposes the operation you need.
In particular:

- use MCP `build_compiler` instead of manual `dotnet build` when you want structured diagnostics
- use MCP `compile_file` instead of invoking `Blade/bin/Debug/net10.0/blade` manually
- use MCP regression tools instead of `just regressions` / `dotnet run --project Blade.Regressions -- ...`
- use MCP instruction-query tools instead of manually scraping the workbook, CSV, or generated metadata

Manual shell commands are the fallback only when the MCP server does not expose the required capability.

If you do so, always create a new demonstrator for that.

The Blade MCP server always uses the latest compiler in `Blade/bin/Debug/net10.0/blade`, so you can
rebuild the compiler and immediately test out your changes.

## Finding Bugs

When something unexpected happens or a compiler bug emerges, immediately log it into `Work/Bugs.md`. Otherwise the bug will be lost forever.

When code appears unreachable, immediately log it into `Work/Unreachable.md`.

## Testing

Always prefer a new code sample in `Demonstrators/` over a dedicated unit test in `Blade.Tests`.
A unit test asserts code, a Demonstrator asserts compiler compliance.

**100% branch coverage required.** Run `just coverage`; query uncovered lines:
`python Scripts/coverage_range.py <class_pattern> <start_line> <end_line>`.

Prefer writing a demonstrator file over a unit test to reach coverage.

The regression harness `Blade.Regressions` is fully integrated into the test suite
in `Blade.Tests/RegressionHarnessTests.cs` (`FullRegressionSuite_Passes`) and allows
collecting coverage information from the regression suite as well.

Code paths that are not provably reachable through the compiler frontend are dead code and shall be
replaced with assertions.

### Regression Harness (`Blade.Regressions`)

Main behavioral gate. Discovers fixtures under `Examples/`, `Demonstrators/`, `RegressionTests/`.
Matches diagnostics and codegen against header expectations; validates assembly via FlexSpin
and semantics through a hardware test runner.

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

| Command                      | Description                                                                                          |
| ---------------------------- | ---------------------------------------------------------------------------------------------------- |
| `just all`                   | Full local verification: build, test, regressions, compile-all-samples                               |
| `just accept-changes`        | Quality gate for new code. Must exit successfully without any findings to consider a task completed. |
| `just build`                 | `dotnet build`                                                                                       |
| `just test`                  | `dotnet test`                                                                                        |
| `just coverage`              | Collect coverage → `coverage/coverage.cobertura.xml`                                                 |
| `just coverage-regressions`  | Collect coverage of just the regression harness → `coverage/coverage.cobertura.xml`                  |
| `just regressions`           | Run full regression harness                                                                          |
| `just compile-all-samples`   | Compile all `.blade` samples, write `*.dump.txt`                                                     |
| `just compile-sample <path>` | Run `blade --dump-all` for one sample                                                                |

## Generated Instruction Metadata

Queries go through `Blade/P2InstructionMetadata.g.cs`. Regenerate: `source .venv/bin/activate && python Scripts/extract.py`.
