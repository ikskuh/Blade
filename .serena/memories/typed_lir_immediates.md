2026-04-02: Removed `TypeSymbol.NormalizeValue` and reshaped `LirImmediateOperand` to carry a `BladeValue`.

Changes:
- Deleted `TypeSymbol.NormalizeValue` from `Blade/Semantics/TypeSymbol.cs`.
- `BladeValue` in `Blade/Semantics/BladeValue.cs` now validates legality directly with `type.IsLegalRuntimeObject(value)` and stores the exact payload without hidden normalization.
- `LirImmediateOperand` in `Blade/IR/Lir/LirModel.cs` now uses a primary constructor over `BladeValue` instead of `(object?, TypeSymbol)`.
- `LirLowerer` now creates typed `RuntimeBladeValue` / `ComptimeBladeValue` immediates for non-null MIR constants. MIR `const null` placeholders lower to `LirConstOperation` with no operands instead of an illegal null immediate.
- `AsmLowerer` now handles zero-operand `const` as an explicit zero/placeholder constant path, and regular immediates come from `BladeValue` payloads.
- `MirConstantPropagation` and `ComptimeTypeFacts` were updated to stop using deleted `NormalizeValue` and to rely on legality checks plus explicit semantic conversion helpers.
- Tests updated in `RuntimeTypeAndValueTests`, `OptimizerTests`, `WriterAndSymbolTests`, and `MirOptimizerTests`.

Validation:
- Focused tests over runtime/value, optimizer, writer/symbol, array-literal pipeline, and full regression harness passed.
- `just accept-changes` passed cleanly.