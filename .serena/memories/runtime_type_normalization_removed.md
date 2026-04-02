2026-04-02: Removed RuntimeTypeSymbol.TryNormalizeRuntimeObject end-to-end.

Rationale from user: runtime normalization should not silently repair illegal payloads, because that hides compiler bugs and shifts failures later in the pipeline.

What changed:
- Deleted `RuntimeTypeSymbol.TryNormalizeRuntimeObject` and all overrides from `Blade/Semantics/TypeSymbol.cs`.
- Runtime types now only expose legality via `IsLegalRuntimeObject`; `NormalizeValue` no longer depends on a recoverable runtime-conversion API.
- `AsmLowerer.LowerConst` now asserts that any runtime constant payload is already legal for the result type instead of attempting repair.
- `MirConstantPropagation` now normalizes fold results to the instruction result type at the optimizer boundary using `ComptimeTypeFacts.TryNormalizeValue` when needed, and otherwise leaves conversions unfused rather than asserting that every runtime conversion can be materialized as an immediate constant.
- `ComptimeTypeFacts.TryNormalizeValue` now owns the remaining semantic conversion logic for comptime conversions and truncation paths (integer wrapping, pointer conversion, bool conversion) without relying on runtime-type repair APIs.
- Updated tests in `Blade.Tests/RuntimeTypeAndValueTests.cs` and `Blade.Tests/MirOptimizerTests.cs` to reflect the stricter runtime payload model and typed MIR fold results.

Validation:
- Focused tests for runtime/value behavior, MIR optimizer constant folding, and the full regression harness passed.
- `just accept-changes` passed cleanly after the refactor.