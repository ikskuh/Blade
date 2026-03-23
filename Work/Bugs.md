# Current known compiler bugs

## Release fallback can silently force extra call result into FlagC

- Date: 2026-03-22
- Symptom: `LirLowerer.GetExtraResultPlacement` returns `ReturnPlacement.FlagC` after `Debug.Fail` when an extra result lookup fails.
- Location: `Blade/IR/Lir/LirLowerer.cs`.
- Impact: in release builds this can silently produce incorrect ABI lowering for extra return values.
