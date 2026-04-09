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

## BUG-3: Fix `rep for(...)` lowering and indexed variant codegen

Reproducer: `Demonstrators/HwTest/hw_rep_for.blade` (`// EXPECT: xfail-hw`).

- Compile the repro with `just compile-sample Demonstrators/HwTest/hw_rep_for.blade` and inspect the `*.dump.txt` for MIR/LIR/ASM.
- Confirm which boundaries the current `REP` spans (what the assembler repeats) and where the end label lands.
- Fix `Blade/IR/Mir/MirLowerer.cs` rep-for lowering so the REP setup is immediately before the repeated body, not before loop-invariant setup.
- Ensure the REP end-label pseudo-op is emitted after the repeated body, not at the top of the block.
- Remove the explicit loop-back `JMP` pattern for rep-for, since `REP` provides repetition and a `JMP` after the REP region can create an outer infinite loop.
- Add an explicit zero-guard: if iterations is 0, skip the repeated region entirely to avoid `REP #0` infinite repeat semantics.
- Thread the rep-for index binding correctly: initialize `i` to `start` and make it visible as the bound loop variable in the body.
- Implement index increment by 1 at the end of each iteration for the indexed form.
- Validate that the non-indexed form does not accidentally synthesize or clobber an index variable.
- Update `Blade/IR/Asm/AsmLowerer.cs` for `repfor.setup`/`repfor.iter` so it uses both operands (start/end) and emits correct REP + index maintenance code.
- Add a non-hardware regression fixture that asserts the final PASM has one `REP` region, no `JMP` back-edge after it, and a correct zero-guard.
- Acceptance: `Demonstrators/HwTest/hw_rep_for.blade` becomes `pass-hw` and matches all expected runs.

## BUG-4: Emit truncation for explicit narrowing casts like `x as u8`

Reproducer: `Demonstrators/HwTest/hw_casts_and_bitcasts.blade` (`// EXPECT: xfail-hw`).

- Compile the repro and confirm whether `MirConvertInstruction` is present for the `as u8` cast.
- Inspect `Blade/IR/Asm/AsmLowerer.cs` `LowerConvert` output for u32->u8 and u32->u16: it must clear upper bits via `ZEROX` (or equivalent).
- If the convert is optimized away, fix the MIR/LIR optimization passes so narrowing converts are never deleted when the value later widens.
- Ensure the conversion result type is preserved through MIR and LIR (avoid losing the u8 width and treating it as u32).
- Add an IR-focused demonstrator that checks for a `convert<u8>` node in MIR and a `ZEROX ..., #7` in final assembly.
- Confirm behavior for both constants and runtime values (e.g. `0x1FF as u8` and `rt_param0 as u8`).
- Confirm behavior for chained casts: `u32 -> u8 -> u32` must round-trip as `(x & 0xFF)` not identity.
- Ensure signed narrowing (`as i8`) uses sign extension rules only when widening, not when truncating.
- Ensure `as bool` (if supported) is not accidentally routed through the integer truncation path.
- Add coverage for both `as u8` and `as u16` in one fixture to prevent partial fixes.
- Update the repro header from `xfail-hw` only when the hardware runner validates the expected runs.
- Acceptance: `hw_casts_and_bitcasts.blade` reports correct low-byte truncation on all runs.

## BUG-5: Lower signed comparisons correctly (`bitcast(i32, x) < 0`)

Reproducer: `Demonstrators/HwTest/hw_casts_and_bitcasts.blade` (`// EXPECT: xfail-hw`).

- Confirm the current lowering path: `i32 < 0` ends up as `CMP reg, #0 WC` (unsigned) in PASM.
- Thread operand signedness through IR: comparisons currently lose operand type because the result is `bool`.
- Extend `MirBinaryInstruction` and/or `LirBinaryOperation` so ordering comparisons carry the operand type (or an `IsSignedComparison` bit).
- Update `Blade/IR/Asm/AsmLowerer.cs` `LowerBinary` to choose signed compare lowering when the operand type is signed.
- For the specific `< 0` pattern, implement a fast sign-bit test lowering (`TESTB x, #31 WC` or an equivalent sequence) instead of full signed compare.
- Ensure `>= 0` and `<= -1` style forms also lower correctly (no inverted polarity bugs).
- Ensure non-zero RHS signed compares either use `CMPS` (if available/desired) or a correct arithmetic transform, not unsigned `CMP`.
- Add a targeted regression fixture that locks the exact emitted instruction sequence for `signed < 0`.
- Validate that `MirFlagPropagation` and branch lowering preserve the intended flag polarity for signed compares.
- Validate the result for both edge cases: `0x80000000` (negative) and `0x00000000` (non-negative).
- Update the repro header from `xfail-hw` only when the hardware runner validates the expected runs.
- Acceptance: `bitcast(i32, x) < 0` produces correct results for all runs in `hw_casts_and_bitcasts.blade`.

## BUG-6: Fix multi-return bool capture from C/Z and bool->u32 if-expression lowering

Reproducer: `Demonstrators/HwTest/hw_multi_return.blade` (`// EXPECT: xfail-hw`).

- Compile the repro and inspect whether `LirCallExtractFlagOperation` is emitted for every bool extra result.
- Confirm return placement policy for multiple bools: which bool maps to `C` vs `Z`, and ensure it matches binder return-slot ordering.
- Ensure `call.extractC`/`call.extractZ` are emitted immediately after the corresponding call and cannot be moved past later flag-writing instructions.
- If necessary, model flag reads in LIR or mark flag-extract ops as order-sensitive so optimizations cannot reorder or eliminate them.
- Validate discard assignments (`_, _ = ...`) still perform the call but do not corrupt previously captured bool values.
- Fix `if (lt2) 1 else 0` lowering so then/else bodies actually assign the result value, not jump through empty blocks.
- Ensure bool conditions used by if-expressions are materialized into registers when needed (do not rely on stale C/Z flags).
- Add a non-hardware regression fixture that asserts the final PASM contains both flag-extract instructions for the 3-return call.
- Add a regression fixture that asserts the then/else blocks contain the constant assignment ops for `lt2_u` and `eq3_u`.
- Confirm the final packed result matches the expected bit layout for all runs.
- Update the repro header from `xfail-hw` only when the hardware runner validates the expected runs.
- Acceptance: `Demonstrators/HwTest/hw_multi_return.blade` becomes `pass-hw` and matches all expected runs.

## BUG-7: Fix recursive function calling convention so `rec fn` results survive spills

Reproducer: `Demonstrators/HwTest/hw_recursive_fn.blade` (`// EXPECT: xfail-hw`).

- Compile the repro and inspect the PASM around the recursive call and subsequent multiply.
- Confirm whether the recursive tier is allocating arg0/ret0 to `PB` and whether `PUSHB`/`POPB` sequences restore into `PB`.
- Reserve `PB` from value allocation for recursive-tier functions if `PB` is semantically tied to the PTRB hub stack / CALLB mechanism.
- Update `Blade/IR/Asm/AsmLowerer.cs` recursive calling convention preference to favor `PA` (or a dedicated non-PB register) for arg0/ret0.
- Audit `Blade/IR/Asm/RegisterAllocator.cs` recursive-call spill insertion so it never pops a spill into the active return register before the return value is consumed.
- Ensure expression evaluation order for `n * factorial(n - 1)` preserves `n` across the call (save `n` before clobbering the argument/return location).
- Add a regression fixture that asserts the recursive result register/place is not overwritten by a POP immediately after CALLB.
- Add a regression fixture that checks factorial output for small values (0, 1, 4, 6) at the harness level.
- Validate both base-case and recursive-case control flow for `rec fn` are unaffected by the calling convention change.
- Ensure the fix does not regress general/leaf calling conventions (focus the change to recursive tier only).
- Update the repro header from `xfail-hw` only when the hardware runner validates the expected runs.
- Acceptance: `Demonstrators/HwTest/hw_recursive_fn.blade` becomes `pass-hw` and matches all expected runs.

## BUG-8: Parse `range` as a type keyword (avoid binder crash on `var r: range = ...`)

Reproducer: `var r: range = 0..10;` (currently crashes in `Binder.BindNamedType` invariant).

- Add `TokenKind.RangeKeyword` in `Blade/Syntax/TokenKind.cs` under type keywords.
- Add `"range" => TokenKind.RangeKeyword` in `Blade/Syntax/SyntaxFacts.cs` keyword table and include it in `GetText`.
- Update `Blade/Syntax/Parser.cs` `ParseType` to treat `RangeKeyword` as a `PrimitiveTypeSyntax`.
- Add a parser-level unit test that `range` in type position produces `PrimitiveTypeSyntax` (not `NamedTypeSyntax`).
- Add a binder/regression demonstrator that exercises `var r: range = 0..10;` and asserts the compiler reports a diagnostic instead of crashing.
- Ensure binder still rejects standalone range expressions outside loops (but via diagnostics, not asserts/exceptions).
- Confirm `BuiltinTypes.TryGet("range", ...)` remains consistent with the lexer/parser treatment.
- Ensure qualified types `module.range` remain parsed as named/qualified types, not primitive keywords.
- Ensure `range` used as identifier where permitted still behaves as a keyword (or document the restriction).
- Run the full parser/binder regression harness to ensure no keyword-table regressions.
- Add coverage so the old crash path is provably unreachable (assert remains valid).
- Acceptance: no crash; `range` as a type keyword is parsed correctly and diagnostics are emitted as needed.

## BUG-9: Do not crash on variable declarations with a missing/empty identifier

Reproducer: malformed `var` statement where `VariableDeclarationSyntax.Name.Text` is empty (binder currently throws).

- Create a focused reject demonstrator that omits the identifier in a local var declaration and asserts diagnostics.
- In `Blade/Semantics/Binder.cs`, guard `CreateVariableSymbol` against `string.IsNullOrWhiteSpace(declaration.Name.Text)`.
- When the name is missing, emit `E0105_ExpectedIdentifier` (or a more specific binder diagnostic if desired) at the declaration span.
- Synthesize a unique non-empty placeholder name for error recovery to avoid `Requires.NotNullOrWhitespace` failures.
- Ensure placeholder names do not collide within the same scope (use a monotonic counter on the binder instance).
- Ensure global-storage variable declarations follow the same non-crashing path as locals.
- Ensure follow-on phases treat the symbol as an error-carrying placeholder and avoid cascading null-ref paths.
- Add a regression that mixes one malformed and one valid declaration in the same scope to validate recovery.
- Confirm the compiler does not throw during binding, MIR lowering, or dumping for the malformed input.
- Ensure diagnostic output points at the missing identifier site, not at unrelated later tokens.
- Run `just regressions` to ensure the recovery change doesn’t hide real diagnostics elsewhere.
- Acceptance: malformed variable declarations produce diagnostics and the compiler never throws.

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
