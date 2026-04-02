2026-04-02 completion update for the typed values and module-level storage refactor.

Status: the refactor is now completed through the acceptance gate for this turn. `just accept-changes` passed cleanly after the final harness/analyzer fixes.

Key completed areas:
- Type hierarchy split into `RuntimeTypeSymbol` and `ComptimeTypeSymbol` in `Blade/Semantics/TypeSymbol.cs`.
- `TypeFacts` removed from production code.
- `BladeValue`, `RuntimeBladeValue`, `ComptimeBladeValue`, and singleton `BladeValue.Void` / `BladeValue.Undefined` implemented in `Blade/Semantics/BladeValue.cs`.
- `TypeSymbol.NormalizeValue(object value)` now owns validation/normalization; runtime normalization delegates through `TryNormalizeRuntimeObject` and asserts invariants.
- `VoidTypeSymbol` and `UndefinedLiteralTypeSymbol` are singleton types.
- `IntegerTypeSymbol.IsLegalRuntimeObject` uses range checks instead of exact CLR payload-type matching; normalization preserves narrow CLR payload shapes.
- Old boolean helper properties on `TypeSymbol` such as `IsVoid`, `IsUnknown`, etc. were removed in favor of concrete type-pattern checks.
- `StoragePlace.StaticInitializer` removed.
- MIR/LIR/ASM modules now carry module-level storage/data definitions instead of embedding storage emission into function node streams.
- `AsmModule(IReadOnlyList<AsmFunction> functions)` deleted; callers construct full modules explicitly.
- `AsmSectionNode`, `AsmDataNode`, `AsmPlaceOperand`, and `AsmDirectiveNode` were removed from production code.
- `AsmTextWriter` and `FinalAssemblyWriter` now use module-level `AsmDataBlock`s and emit data sections separately from functions.
- Shared constants and spill/storage definitions are emitted as module-level data blocks rather than function body nodes.
- Full acceptance gate passed after fixing the remaining IR coverage harness crash.

Final blocker that was resolved:
- `Demonstrators/Language/pass_array_literal_inference.bound.blade` was crashing only in regression-harness IR coverage, not in the compiler itself.
- Root cause: `Blade.Regressions/IrCoverage.cs` reflected over every public property and invoked runtime-layout getters such as `ArrayTypeSymbol.SizeBytes`/`AlignmentBytes` on binder-only array types like `[3]<int-literal>`, which threw through reflection.
- Fix: IR coverage now traverses only properties whose return types are not skipped scalar/meta types, preventing non-graph layout getters from being invoked.
- Added a targeted regression-harness test `FullRegressionSuite_WithIrGuard_PassesArrayLiteralInferenceFixture` in `Blade.Tests/RegressionHarnessTests.cs` to lock this case in.

Acceptance outcome:
- `just accept-changes` passed.
- Static analysis clean.
- Debug and release test suites passed, including hardware-backed tests.

If future work continues in this area, the next cleanup target is conceptual rather than blocking: revisit binder-stage array literal inference carrying `[N]<int-literal>` if the semantic model should eventually forbid non-runtime element types inside runtime array symbols. The current harness crash is fixed without reintroducing compatibility code.