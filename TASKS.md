# Implementation tasks for the Blade compiler

## CS-1: `u8x4` SIMD type

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`.

- Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`).
- `IsAssignable`: `[4]u8` ↔ `u8x4` (implicit coercion both ways).
- Integer literal → `u8x4` only through array literal `[a,b,c,d]`.
- Future: swizzle operations (deferred, not in reference.blade).
- Tests: `var v: u8x4 = [1,2,3,4];`, coerce from/to `[4]u8`.

## CS-2: eradicate invalid syntax

Operators  PostIncrement and PostDecrement do not exist in Blade.

## ASM-3: Add `%N` temporary register support in inline asm

Users currently must declare explicit local variables to use scratch registers in inline asm blocks. `%0`, `%1`, etc. should act as hidden locals — unnamed temporaries allocated by the compiler for the duration of the asm block. This is especially useful in `asm fn` functions where no surrounding variable scope is available.

Syntax: `%0`–`%N` as operands inside an `asm` or `asm volatile` block. Each distinct index allocates one fresh virtual register. They do not correspond to any Blade variable and are not live outside the block.

Implementation sketch:

- Lexer/validator: recognise `%<decimal>` as a new operand token kind inside asm bodies.
- Binder: collect the set of used indices per asm block, add a synthetic `{%0}` … `{%N}` binding for each with Write-only access on first write, ReadWrite thereafter.
- Typed lowering: map each `%N` operand to the corresponding synthetic binding's virtual register operand — no name mangling needed beyond the existing binding mechanism.
- Register allocator: treat synthetic bindings as ordinary short-lived locals; they must not escape the asm region.
- Diagnostics: E0307 for `%N` used in a context where typed lowering cannot represent it (should be unreachable given the validator, but keep the invariant explicit).
- Demonstrators: positive case using `%0` as a scratch register inside a non-trivial asm block; negative case (if any grammar restriction is introduced).

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

### BUG-3: Fix code generation for `irq* fn()` in combination with `yield`

Right now, the compiler lowers `yield` to the correct `RESI1`, but it does not yet restore the original IRQ handler when the IRQ leaves through a `RETI1`.

Acceptance Criteria:

- `Demonstrators/Language/pass_irq_lowering_basic.blade` passes
- `Demonstrators/Language/pass_irq_lowering_yield.blade` passes
- A `MOV IJMP*, #<current_function_label>` is present for `irq* fn()` IFF `yield` is present.

## Backend Lowering Backlog

### LOW-3: Implement reg-storage indexed access (`load.index.reg`, `store.index.reg`)

`LowerLoadIndex` and `LowerStoreIndex` handle `Hub` and `Lut` storage classes but fall through to E0401 for `Reg`. Register-file arrays with runtime indices need `ALTS`/`ALTD`-based indirection on the P2.

- Implement `ALTD` + source instruction for `load.index.reg`.
- Implement `ALTS` + destination instruction for `store.index.reg`.
- Decide whether constant-index cases can be lowered to direct register access without `ALTx`.
- Unblock xfail fixtures: `pass_array_literals.bound.blade`, `pass_array_literals_bool.bound.blade`, `pass_array_literals_mir.blade`, `pass_for_array_iteration.blade`.

### LOW-4: Implement reg-storage pointer deref (`load.deref.reg`, `store.deref.reg`)

`LowerLoadDeref` and `LowerStoreDeref` handle `Hub` and `Lut` but fall through to E0401 for `Reg`. Pointer-to-register-file indirection needs `ALTS`/`ALTD`.

- Implement `ALTD`/`ALTS` sequences for register-file pointer derefs.
- Unblock xfail fixture: `pass_pointers_mir.blade`.

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

## REVIEW-C7: Preserve source identity on diagnostics from imported modules

Diagnostics currently only carry a `TextSpan`, while rendering resolves the span against the root compilation source file.

- Attach originating source identity to diagnostics or diagnostic locations.
- Render imported-module diagnostics against their actual source file instead of the root file.
- Add coverage for imported-module parse/bind errors with file/line reporting.

## REVIEW-C8: Report import failures at the `import` site

`Binder.LoadAndBindModule` still reports some import failures with `new TextSpan(0, 0)` instead of the caller span.

- Thread the import-site span through `LoadAndBindModule`.
- Use that span for file-not-found and circular-import diagnostics.
- Add regression coverage that checks the reported import location.

## REVIEW-C9: Do not populate `_importedModules` before alias declaration succeeds

`BindImports` writes `_importedModules[alias] = imported` before checking whether the module alias can be declared in the global scope.

- Only commit the imported-module table entry after `TryDeclare` succeeds.
- Keep `_importedModules` and the symbol table consistent after alias collisions.
- Add a regression or unit test for duplicate import aliases.

## REVIEW-C11: Make `DiagnosticBag.HasErrors` severity-aware

`DiagnosticBag.HasErrors` still treats any diagnostic as an error by checking only `_diagnostics.Count > 0`.

- Derive diagnostic severity from the code model instead of bag length.
- Return `true` only when at least one error diagnostic is present.
- Add tests that mix warning/info diagnostics with and without errors.

## REVIEW-C16: Unify flag annotation parsing

Return-item flags accept `SyntaxFacts.IsIdentifierLike(...)`, while asm output bindings still require a strict identifier token.

- Extract one shared parser helper for flag names.
- Apply it to return specs and asm output bindings so equivalent grammar positions accept the same token set.
- Add positive and negative parser coverage around flag annotations.

## REVIEW-C17: Narrow `SyntaxFacts.IsIdentifierLike` to explicit contextual cases

`IsIdentifierLike` currently accepts identifiers plus every keyword in the keyword table.

- Replace the broad keyword-table check with explicit allowlists or smaller context-specific predicates.
- Audit the existing call sites so each one admits only the intended keyword subset.
- Add parser coverage for accepted and rejected contextual names.

## REVIEW-X2: Split `AsmLowerer` and remove placeholder lowering paths

`Blade/IR/Asm/AsmLowerer.cs` remains very large and still contains placeholder comment-emission paths such as `TODO: CALLD`.

- Partition `AsmLowerer` by lowering concern so invariants become easier to reason about.
- Replace placeholder comment emission for unsupported operations with explicit diagnostics or assertions.
- Add demonstrators/tests that lock in the intended behavior.

## REVIEW-X3: Break up binder responsibilities

`Blade/Semantics/Binder.cs` still concentrates imports, declarations, statements, expressions, conversions, comptime, and diagnostics in one class.

- Extract focused binder helpers or sub-binders for imports, declarations, statements, expressions, and conversion/type-policy logic.
- Preserve existing diagnostics and behavior during the split.
- Keep the decomposition incremental and covered by regressions.

## REVIEW-X4: Refactor compilation option parsing boilerplate

`CompilationOptionsCommandLine.TryParse` still repeats the same parse/error/continue gate for each option family.

- Introduce a staged dispatcher or result object model so option parsing is easier to audit.
- Centralize error propagation instead of repeating the same control flow after each parse attempt.
- Preserve current CLI surface and diagnostics.

## REVIEW-X5: Split parser responsibilities into smaller units

`Blade/Syntax/Parser.cs` still centralizes large branch-heavy declaration, statement, expression, and recovery logic.

- Extract focused parsing helpers or partial parsers for declarations, statements, expressions, and types.
- Keep error recovery behavior stable while reducing branching concentration.
- Add or retain regression coverage around the extracted grammar surfaces.

## REVIEW-X6: Reduce `DiagnosticBag` boilerplate with descriptor-driven reporting

`DiagnosticBag` still exposes a large repetitive surface of near-identical reporting helpers.

- Centralize diagnostic descriptors and format templates.
- Keep typed wrappers only where they add real readability or type safety.
- Preserve existing diagnostic codes and message text.

## REVIEW-X8: Remove dead `hasEscapeErrors` state from `Lexer.ReadString`

`Lexer.ReadString` still tracks `hasEscapeErrors` only to discard it immediately afterward.

- Remove the dead local state or wire it into real control flow if behavior is actually missing.
- Keep escape-sequence diagnostics unchanged.
- Add coverage if needed to lock in the intended string-literal behavior.

## REVIEW-X9: Extract shared raw-asm body capture logic

`Parser.ParseAsmFunctionDeclaration` and `Parser.ParseAsmBlockStatement` still duplicate the same brace-depth raw-body capture loop.

- Extract one shared helper for raw-asm body capture.
- Reuse it in both asm parsing paths.
- Keep the captured body text and recovery behavior identical.
