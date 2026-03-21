# Blade project overview

Blade is a compiler for the Blade language that emits PASM2 (Propeller 2 assembly) for COG execution mode. The language reference is `Docs/reference.blade`.

## Tech stack
- C# on .NET 10 (`Blade/blade.csproj` targets `net10.0`)
- NUnit test suite in `Blade.Tests`
- Regression harness in `Blade.Regressions`
- Justfile-based developer workflow
- FlexSpin is used externally to validate emitted PASM2 assembly

## Architecture
Compiler pipeline:
1. Lexer
2. Parser producing AST
3. Semantic analysis / typed bound tree
4. SSA / IR lowering
5. PASM2 code generation

The compiler is described in `CLAUDE.md` as a hand-rolled recursive descent parser. Each stage is intended to be testable on its own.

## High-level codebase structure
- `Blade/`: main compiler implementation
  - `Syntax/`: lexer, parser, syntax facts, syntax node model
  - `Semantics/`: binder, type system, bound tree, comptime, inline assembly validation
  - `IR/`: MIR/LIR/ASM lowering, optimization, legalizing, register allocation, final assembly writing
  - `Diagnostics/`: diagnostic codes and reporting
  - `Source/`: source text and span handling
  - `Program.cs`: CLI entrypoint
- `Blade.Tests/`: NUnit tests plus accept/reject fixtures
- `Blade.Regressions/`: regression harness executable
- `Examples/`, `Demonstrators/`, `RegressionTests/`: sample and regression corpora used for compiler validation
- `Docs/`: language spec, harness docs, optimization notes, hardware references
- `VSCode/`: Blade language extension grammar
- `Scripts/`: coverage and metadata scripts

## Important project rules
- `Docs/reference.blade` is the syntax authority
- Syntax changes must be mirrored into `VSCode/blade-lang/syntaxes/blade.tmLanguage.json`
- Unexpected compiler bugs should be logged in `Work/Bugs.md`
- Unreachable code should be logged in `Work/Unreachable.md`
- Prefer demonstrators/regression fixtures over ad-hoc temporary files and often over unit tests for compiler behavior work