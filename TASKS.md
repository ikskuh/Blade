# Implementation tasks for the Blade compiler

## CS-1: `u8x4` SIMD type

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`.

- Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`).
- `IsAssignable`: `[4]u8` ↔ `u8x4` (implicit coercion both ways).
- Integer literal → `u8x4` only through array literal `[a,b,c,d]`.
- Future: swizzle operations (deferred, not in reference.blade).
- Tests: `var v: u8x4 = [1,2,3,4];`, coerce from/to `[4]u8`.

## Bug Fix Backlog

## BUG-2: Lower struct literals end-to-end

Regular struct literals bind successfully but still fail later with `E0401_UnsupportedLowering`.

- Implement MIR/asm lowering for regular struct literals so they compile through final assembly.
- Convert the existing struct-literal demonstrator from `EXPECT: xfail` to `EXPECT: pass`.
- Keep regression coverage focused on removing the `structlit` unsupported-lowering path.

## BUG-4: Fix recursive calling convention

`rec fn` codegen still does not reliably use the recursive calling convention path.

- Make recursive callees lower through the recursive tier instead of falling back to plain `CALL`.
- Implement the required recursive return/spill behavior around `CALLB` and PTRB-backed stack usage.
- Convert the recursive-function demonstrator away from its current expected failure once the path is live.

## BUG-5: Preserve the halt-loop sentinel through asm optimization

The halt loop requires `REP #1, #0` followed by `NOP`, but generic NOP elision can remove the sentinel.

- Make the halt/trap sequence non-elidable by the asm optimizer.
- Preserve the exact `REP #1, #0` + `NOP` shape in optimized output.
- Add an optimizer regression that asserts the sentinel survives optimization.

## BUG-7: Resolve peer typing for enum literals in comparisons

Enum comparisons should infer the enum type for contextual literals like `.Off` from the opposite operand.

- Teach comparison binding to resolve bare enum literals from the peer operand in `==` and `!=`.
- Add passing coverage for both equality and inequality cases.
- Keep the scope to enum-literal comparison typing rather than broader enum feature work.

## BUG-8: Allow address-of indexed array elements

`&ptr[1]` is currently rejected with `E0223` even though the indexed element should be addressable.

- Extend address-of binding to accept indexed array-element lvalues instead of only bare names.
- Lower indexed-element addresses through the existing pointer/address pipeline.
- Add focused binder and IR coverage for taking the address of an array element.

## BUG-9: Remove stray store when passing `&array` to pointer parameters

Passing `&greeting` into a pointer parameter currently has a reported codegen path that emits an unexpected store.

- Add a demonstrator for the `length = count_string(&greeting);` shape.
- Fix lowering so taking the address of an array for a call does not synthesize a stray `WRLONG` to the array base.
- Validate the fix at final-asm level so the bad store is explicitly absent.

## BUG-10: Preserve loop-carried count updates in the pointer-walk sample

The string-walk sample reports that `count += 1` disappears from final codegen.

- Add a reproducer for the pointer-walk/counting loop.
- Fix the lowering or optimization path that drops the loop-carried increment.
- Validate the final assembly contains the increment behavior for the live reproducer.

## BUG-11: Respect the `SETQ`/`SETQ2` + PTRx silicon hazard

The compiler must not emit `ALTx`/`AUG*` instructions between `SETQ`/`SETQ2` and PTRx bulk-transfer instructions.

- Add a regression that exercises bulk PTRx transfer codegen.
- Ensure legalization/scheduling preserves adjacency between `SETQ`/`SETQ2` and the corresponding `RDLONG`/`WRLONG`/`WMLONG` PTRx instruction.
- Keep the acceptance criteria at final emitted assembly shape, not just intermediate IR.

## BUG-12: Respect the `AUGS` + immediate `ALTx` silicon hazard

The compiler must not let an `AUGS` intended for one instruction leak into an intervening immediate `ALTx`.

- Add a regression around large-immediate codegen with an intervening `ALTx` instruction.
- Ensure legalization does not emit an immediate `ALTx` that consumes or preserves the wrong `AUGS`.
- Validate the final assembly ordering/operands so the hazard cannot occur.
