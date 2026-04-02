2026-04-02: Continued the typed-value refactor past LIR into binder/comptime/MIR constant handling.

Completed in this slice:
- `BoundLiteralExpression` now carries `BladeValue` and related tests were updated.
- `MirConstantInstruction` now carries `BladeValue?` and `LirImmediateOperand` carries `BladeValue`.
- Removed the late per-stage `CreateBladeValue(...)` reconstruction helpers from binder, comptime evaluation, MIR lowering, and MIR constant propagation.
- Centralized semantic coercion in `ComptimeTypeFacts.TryCreateBladeValue(object?, TypeSymbol, out BladeValue)`.
- `ComptimeTypeFacts` now accepts already-legal direct payloads for both runtime and comptime types before attempting numeric normalization.
- `NormalizeFoldedValue` / `NormalizeLiteral` now preserve exact typed values when the source type already matches the target type, and otherwise use `TryConvertToLong` for numeric coercion instead of only `int/uint/long` exact payloads.
- `CreateFoldedLiteralExpression` now preserves exact typed values and keeps `undefined` as an undefined literal instead of trying to fabricate an illegal runtime value.
- `MirConstantPropagation` boolean/int extraction now handles the full small-integer payload set (`sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`).

Verification:
- Representative compiler runs passed for `Demonstrators/Types/pass_unions.bound.blade`, `Demonstrators/Types/pass_string_to_array.blade`, and `Demonstrators/HwTest/hw_multi_return.blade`.
- `dotnet test` focused slices for comptime/binder/MIR optimizer tests passed.
- `just accept-changes` passed green, including hardware-backed test runs.

Remaining architectural note:
- `ComptimeTypeFacts` still exists as the central semantic conversion surface; the broader plan to eliminate it in favor of a more explicit runtime/comptime value conversion model is not yet complete, but the late compatibility shims inside binder/MIR/LIR/ASM were removed in this slice.