# Implementation tasks for the Blade compiler

## CS-1: Add semantic/runtime support for `u8x4` SIMD type

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`, and the lexer/parser already recognize `u8x4`.
The remaining work is in the type system and coercion rules.

- Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`).
- `IsAssignable`: `[4]u8` ↔ `u8x4` (implicit coercion both ways).
- Integer literal → `u8x4` only through array literal `[a,b,c,d]`.
- Future: swizzle operations (deferred, not in reference.blade).
- Tests: `var v: u8x4 = [1,2,3,4];`, coerce from/to `[4]u8`.

## Bug Fix Backlog

## BUG-1: Respect the `SETQ`/`SETQ2` + PTRx silicon hazard

The compiler must not emit `ALTx`/`AUG*` instructions between `SETQ`/`SETQ2` and PTRx bulk-transfer instructions.

- Add a regression that exercises bulk PTRx transfer codegen.
- Ensure legalization/scheduling preserves adjacency between `SETQ`/`SETQ2` and the corresponding `RDLONG`/`WRLONG`/`WMLONG` PTRx instruction.
- Keep the acceptance criteria at final emitted assembly shape, not just intermediate IR.

## BUG-2: Respect the `AUGS` + immediate `ALTx` silicon hazard

The compiler must not let an `AUGS` intended for one instruction leak into an intervening immediate `ALTx`.

- Add a regression around large-immediate codegen with an intervening `ALTx` instruction.
- Ensure legalization does not emit an immediate `ALTx` that consumes or preserves the wrong `AUGS`.
- Validate the final assembly ordering/operands so the hazard cannot occur.

## Backend Lowering Backlog

### LOW-6: Implement non-aligned bitfield insert

`LowerAlignedBitfieldFallback` (line 1216) emits E0401 for bit widths/offsets that don't align to nibble (4), byte (8), or word (16) boundaries. P2 has `SETNIB`/`SETBYTE`/`SETWORD` for aligned cases only.

- Implement a shift+mask sequence for arbitrary bitfield inserts: `MOVBYTS`/`BITL`/`SHL`/`AND`/`OR` or equivalent.
- Cover the fallback path with a demonstrator that uses a non-aligned bitfield.

### LOW-7: Implement C#-style declaration attributes (`[Used]`, `[LinkName]`)

Design note in `CallGraphAnalyzer.cs` (line 9). Not a lowering gap, but a planned language feature.

- `[Used]`: marks a function/variable as reachable, preventing dead-code elimination even without direct callers.
- `[LinkName("_start")]`: sets the emitted assembly label name for linker/external interop.
- Parse attributes in the syntax layer, store in bound tree, propagate through MIR/LIR to call-graph analysis and codegen.

## Code Review Backlog

## REVIEW-C5: Guard non-IO output path failures in compiler output writers

`StdioOutputWriter` and `JsonOutputWriter` only normalize `IOException` and `UnauthorizedAccessException` when writing `--output` or `--dump-dir` targets.

- Handle other expected path-format failures such as `ArgumentException` and `NotSupportedException`, or pre-validate the paths before opening them.
- Keep output-path problems as regular user-facing CLI errors instead of process-terminating exceptions.
- Cover both text and JSON output paths in tests.

## REVIEW-C16: Apply the shared flag-name parser to asm output bindings

Return-item flags already accept `SyntaxFacts.IsIdentifierLike(...)`; asm output bindings still require a strict identifier token.

- Extract one shared parser helper for flag names.
- Apply it to asm output bindings so both flag positions accept the same token set.
- Add positive and negative parser coverage around flag annotations.

## REVIEW-C17: Narrow `SyntaxFacts.IsIdentifierLike` to explicit contextual cases

`IsIdentifierLike` currently accepts identifiers plus every keyword in the keyword table.

- Replace the broad keyword-table check with explicit allowlists or smaller context-specific predicates.
- Audit the existing call sites so each one admits only the intended keyword subset.
- Add parser coverage for accepted and rejected contextual names.

## REVIEW-X4: Refactor compilation option parsing boilerplate

`CompilationOptionsCommandLine.TryParse` still repeats the same parse/error/continue gate for each option family.

- Introduce a staged dispatcher or result object model so option parsing is easier to audit.
- Centralize error propagation instead of repeating the same control flow after each parse attempt.
- Preserve current CLI surface and diagnostics.

## REVIEW-X6: Reduce `DiagnosticBag` boilerplate with descriptor-driven reporting

`DiagnosticBag` still exposes a large repetitive surface of near-identical reporting helpers.

- Centralize diagnostic descriptors and format templates.
- Keep typed wrappers only where they add real readability or type safety.
- Preserve existing diagnostic codes and message text.
