# XFAIL campaign progress

User goal: work through current regression-suite XFAIL fixtures one at a time, fixing each issue before moving to the next. Use hardware execution when applicable.

Resolved item 1: `Demonstrators/Types/pass_pointers_mir.blade`
- Language rule restored by user: standalone `ptr.*;` / `many[1];` are invalid and should stay invalid.
- Fixture uses explicit discards.
- Implemented backend lowering for reg-backed `load.deref` and `load.index` in `AsmLowerer` using `ALTS` plus an altered-operand placeholder.
- Replaced the old raw numeric placeholder model with `AsmAltPlaceholderOperand`.
- `AsmTextWriter`/ASMIR now shows `<altered>` to express intent clearly, while final assembly emission still materializes assembler-safe `0` / `#0` placeholders for FlexSpin.
- Prevented altered-placeholder `MOV`s from being treated as ordinary tracked copies.
- Strengthened `IrPipelineTests.VolatileRegPointerReadExpressions_EmitIndirectCogRegisterLoads` so it asserts `ALTS` is immediately followed by `MOV ... <altered>`.

Resolved item 2: `Demonstrators/Optimizations/mir-copy-prop-enabled.blade`
- The old xfail was a fixture-design problem, not a compiler bug.
- Replaced the source pattern with a unary-plus example that reliably lowers to `MirCopyInstruction`.
- Updated both enabled/disabled fixtures around that pattern.
- Important note: copy-prop rewrites downstream uses, but dead-copy removal is a separate optimization pass. The defining MIR/LIR copy may still be present in the `copy-prop-enabled` fixtures.

Resolved item 3: `Demonstrators/Types/pass_string_to_array.blade`
- The xfail was caused by missing reg-backed `store.index` lowering during array initialization.
- Implemented `store.index.reg` lowering in `AsmLowerer` using `ALTD` plus the altered-operand placeholder.
- Added IR coverage via `RegArrayLiteralInitialization_EmitsIndirectCogRegisterStores`, now strengthened to assert `ALTD` is immediately followed by `MOV <altered>, ...`.

Resolved item 4: `Demonstrators/HwTest/hw_bool_logic.blade`
- Root cause was not MIR lowering. `-fno-mir-opt=*` still miscompiled, which isolated the real bug to ASM phi transport / allocation.
- MIR and LIR were correct; the first bad stage was ASMIR/final assembly on hardware.
- Added `AsmInstructionNode.IsPhiMove` and marked block-parameter transport MOVs emitted by `AsmLowerer.EmitPhiMoves`.
- Fixed `LivenessAnalyzer` so phi-move source registers interfere with the other values already live across the same phi bundle. Without this, distinct incoming values on the same edge could be assigned the same physical register.
- Left a conservative allocator guard in place to skip MOV coalescing on phi moves (`RegisterAllocator.BuildCoalesceMap` ignores `IsPhiMove`).
- Updated `Demonstrators/HwTest/hw_bool_logic.blade` from `EXPECT: xfail-hw` to `EXPECT: pass-hw`.
- Added a focused non-hardware regression test: `RegisterAllocatorTests.LivenessAnalyzer_TreatsPhiMoveSourcesAsInterfering`.
- Also updated stale inline-asm expectation `IrPipelineTests.InlineAsm_TypedMode_SupportsColonTerminatedLabels` so it no longer requires the pre-fusion conditional jump form.

Resolved item 5: `Demonstrators/Optimizations/asmir-global_reg-operator-no-copy.blade`
- The user explicitly rejected changing the source from `shared = shared + 1` to `shared += 1`; the optimizer had to be fixed instead.
- Added `AsmTopLevelRegisterAddFusion`, which folds `MOV tmp, g_shared; ADD tmp, #n; MOV g_shared, tmp` into `ADD g_shared, #n` for elidable top-level reg storage.
- Updated `Demonstrators/Optimizations/asmir-global_reg-operator-no-copy.blade` from `EXPECT: xfail` to `EXPECT: pass`.
- Also updated `Demonstrators/Modules/pass_imported_global_persists.blade` because imported-module global register increments now optimize to direct `ADD g_seed, #1` as well.

Current remaining xfails after resolving `asmir-global_reg-operator-no-copy.blade`:
- `Demonstrators/HwTest/hw_multi_return.blade`
- `Demonstrators/HwTest/hw_casts_and_bitcasts.blade`
- `Demonstrators/HwTest/hw_recursive_fn.blade`
- `Demonstrators/HwTest/hw_hub_storage.blade`
- `Demonstrators/HwTest/hw_rep_for.blade`

Validation from the latest turn:
- `dotnet test Blade.Tests/Blade.Tests.csproj --filter "FullyQualifiedName~IrPipelineTests|FullyQualifiedName~OptimizerTests"` -> passed
- `execute_regression_fixture(Demonstrators/Types/pass_pointers_mir.blade)` -> pass
- `execute_regression_fixture(Demonstrators/Optimizations/asmir-global_reg-operator-no-copy.blade)` -> pass
- `execute_regression_tests(with_hardware: true)` -> `fixture_count=335`, `pass_count=330`, `xfail_count=4`, `fail_count=0`, `hw_failed_count=1`, only failing hardware path `Demonstrators/HwTest/hw_rep_for.blade`
- `just coverage` -> line coverage `95.4% (18915/19821)`, branch coverage `89.7% (7755/8648)`
- `just accept-changes` -> passed

Notes:
- Do not reintroduce any parser/binder expression-statement acceptance changes. The user explicitly rejected that and restored those files themselves.
- `pass_pointers_mir.blade` should keep explicit discards, not top-level `ptr.*;` / `many[1];`.
- `TODO.md` has user changes and should be left alone.
- `Docs/multicore.blade` is untracked and should be left alone unless asked.
- No Zig commands were used for these items.