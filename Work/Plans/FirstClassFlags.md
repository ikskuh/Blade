## Plan: First-Class Flag Values

Reshape the MIR → LIR → ASM pipeline so flags become a real value space alongside registers, not metadata attached to register-valued booleans. The recommended design uses generic flag-space values for all bool-like and `bit` results, introduces the same value-space split in MIR that already exists in LIR, deletes the side channels (`MirFunction.FlagValues`, branch `ConditionFlag`) and the backend heuristic (`FlagOnlyRegisters`), and adds one dedicated legalization stage that inserts explicit flag/register conversions only where the hardware or calling convention requires them.

**Steps**
1. Phase 1: Reframe MIR around value spaces instead of untyped ids.
2. Replace `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/VirtualMirValue.cs` with the same shape already introduced in LIR: an abstract `VirtualMirValue` base plus `VirtualMirRegister` and `VirtualMirFlag` subtypes, with a small discriminator enum for value space.
3. Update `/home/felix/projects/nerdgruppe/blade/Blade/GlobalUsings.cs` so the compiler stops treating MIR values as a register-like `MirValueId` handle. The end state should use explicit `VirtualMirValue` / `VirtualMirRegister` / `VirtualMirFlag` names in the IR layer rather than preserving the old “id only” abstraction that hides storage-space meaning.
4. Rework `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirModel.cs` so block parameters, instruction results, uses, and terminators are expressed in terms of `VirtualMirValue`, with register-only or flag-only positions typed accordingly where the model can state that directly. Remove `MirFunction.FlagValues` entirely; a value being a flag must be encoded by its type, not by a side dictionary.
5. Split the current overloaded `MirFlag` concept in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirModel.cs` into two concerns: value-space identity stays on `VirtualMirFlag`, while concrete hardware predicate/lane concepts remain as operation or backend-level condition metadata only where needed. Do not let `C/Z/NC/NZ` remain the way MIR tells whether a value is a flag.
6. Phase 2: Make MIR lowering produce flag-space values by default for logical 1-bit results.
7. In `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirLowerer.cs`, change all bool-like and `bit` producers to create `VirtualMirFlag` results by default. That includes comparisons, logical boolean operations, flag-return call results, and any other frontend lowering sites currently creating a generic value and then registering it in `_flagValues`.
8. Introduce explicit MIR conversion operations for the real storage-space boundaries instead of implicit materialization. At minimum the MIR layer needs one register-to-flag conversion and one flag-to-register conversion so arithmetic, memory, aggregate, and calling-convention boundaries can demand the space they actually need.
9. Rewrite MIR branch terminators so the condition is a first-class flag value, not a register plus optional `ConditionFlag`. The branch should mean “branch on this logical flag value being true,” leaving concrete `C/Z` selection to later legalization and lowering.
10. Delete `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirFlagPropagation.cs` after the MIR model change lands. Its job is a workaround for the side-channel architecture. If any useful canonicalization remains, replace it with a smaller pass that rewrites explicit flag/register conversions, not one that reconstructs flag identity from metadata.
11. Phase 3: Rebase MIR utilities and optimizations onto typed values.
12. Update `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirOptimizationHelpers.cs`, `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirInliner.cs`, `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirTextWriter.cs`, and the MIR optimization passes under `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/` so they track `VirtualMirValue` directly and preserve its storage space through rewrites, parameter maps, alias maps, liveness, and dumps. This step depends on steps 2 through 10.
13. Replace any copying of `function.FlagValues` in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirConstantPropagation.cs`, `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirCopyPropagation.cs`, `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirControlFlowSimplification.cs`, and `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirDeadCodeElimination.cs` with normal typed-value preservation. After the reshape, there must be no parallel bookkeeping for “flagness”.
14. Phase 4: Finish the LIR split instead of leaving `VirtualLirFlag` dormant.
15. Generalize `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirModel.cs` so block parameters, instruction destinations, and value operands use `VirtualLirValue` rather than `LirVirtualRegister` only. Introduce explicit flag-valued operands or a shared `LirValueOperand` abstraction; either way, the end state must let the type system distinguish register operands from flag operands without consulting side metadata.
16. Rework `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirLowerer.cs` to map `VirtualMirRegister -> VirtualLirRegister` and `VirtualMirFlag -> VirtualLirFlag` directly. Delete the current “everything becomes a register” map and remove `LirCallExtractFlagOperation` as a representation crutch. Call and spawn extra results that are returned in flags must produce flag-valued LIR results directly.
17. Make branch conditions in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirModel.cs` first-class flag values and delete `ConditionFlag` from the LIR terminator model. After this change, a branch does not carry a shadow predicate descriptor beside its condition value.
18. Add explicit LIR conversion operations for flag/register space changes, mirroring the MIR conversion operations. These operations become the only legal place where a flag value is spilled to a register or a register boolean is reconstituted into flag space.
19. Phase 5: Add a dedicated flag legalization stage before ASM lowering.
20. Introduce a new pass between `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirLowerer.cs` and `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs` that legalizes virtual flag values against the actual Propeller 2 hardware constraints. This pass owns: assigning live flag values to concrete hardware flag lanes when possible, inserting explicit flag-to-register spills when a value cannot remain in hardware flag space, and inserting register-to-flag rematerialization when a later consumer demands a flag again.
21. Keep MIR and LIR generic as requested: a `VirtualMirFlag` / `VirtualLirFlag` is a logical 1-bit flag-space value, not a pre-assigned `C` or `Z`. Concrete lane choice belongs in the legalization/backend boundary, not in the high-level IR.
22. Make the legalization stage the owner of calling-convention adaptation for flag returns and flag parameters. This replaces the current extract-and-retest pattern by inserting conversions only when a caller/callee boundary truly crosses storage spaces.
23. Phase 6: Simplify ASM lowering around typed inputs and delete the heuristic.
24. In `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs`, delete `FlagOnlyRegisters`, `ComputeFlagOnlyRegisters(...)`, and all `isFlagOnly` checks. They are symptoms of the old architecture and should disappear entirely once LIR arrives in a legalized form.
25. Rework `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs` so branches, compares, returns, and call-result handling dispatch on typed LIR values and explicit conversion operations, not on a register operand plus side metadata. `LowerBranch(...)`, `LowerReturn(...)`, and the current `LowerCallExtractFlag(...)` area are the main control points.
26. Keep `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/RegisterAssociator.cs` register-only. Do not grow it into another heuristic bridge for flags. If the legalization pass is correct, ASM lowering should only hand real virtual registers to the register associator and should consume flag values through dedicated backend lowering logic before register allocation becomes relevant.
27. Review `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/RegisterAllocator.cs` only after the legalization design is in place. The intended outcome is that register allocation stays register-centric because flag allocation was already resolved one stage earlier, not because flags were collapsed back into registers.
28. Phase 7: Lock the reshape down with regression anchors and coverage.
29. Add demonstrators and regression fixtures that force the new architecture to prove itself end-to-end: compare-to-branch without materialization, multi-return flag values across calls, `bit` arithmetic that must spill to registers and return to flag space, flag values crossing block parameters, and storage/load boundaries where bool-like values move between memory and flags.
30. Use `/home/felix/projects/nerdgruppe/blade/RegressionTests/HwTest/hw_multi_return-3val.blade` as the primary hardware regression anchor for the motivating bug. Add one positive fixture where two logical results survive as flags across a call boundary, and one negative/contrast fixture where an explicit spill is required and expected.
31. Add or refresh narrow unit coverage only where the regression harness cannot assert the invariant directly, especially for MIR/LIR text writers, inliner/optimizer value rewrites, and the new legalization pass.
32. Verification order: first targeted compiler tests for MIR/LIR model validity and legalization behavior; then the new demonstrators/regressions; then `just regressions`; then `just coverage` to ensure the new flag-space paths are fully covered; finally `just accept-changes` and a `git diff` self-review.

**Relevant files**
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/VirtualMirValue.cs` — introduce the MIR value-space split parallel to LIR.
- `/home/felix/projects/nerdgruppe/blade/Blade/GlobalUsings.cs` — remove or narrow the old `MirValueId` abstraction so value-space meaning is explicit.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirModel.cs` — replace flag side metadata with typed values in blocks, instructions, and terminators.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirLowerer.cs` — make bool-like and `bit` lowering produce flag-space values and explicit conversions.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirOptimizationHelpers.cs` — preserve typed values through MIR rewrites.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirInliner.cs` — clone and remap typed MIR values without auxiliary flag maps.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirTextWriter.cs` — dump typed flag/register values explicitly so regressions can assert the new shape.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirFlagPropagation.cs` — delete or replace after typed flag values make the propagation side-channel obsolete.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/` — update constant propagation, copy propagation, CFG simplification, and DCE to operate on typed MIR values directly.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/VirtualLirValue.cs` — existing template for value-space split; wire it through rather than treating flags as dormant.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirModel.cs` — generalize parameters, destinations, operands, and branches from register-only to typed value space.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirLowerer.cs` — preserve flag/register space from MIR and replace extract-flag lowering with real flag-valued results.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs` — delete `FlagOnlyRegisters` and lower typed/legalized values directly.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/RegisterAssociator.cs` — remain register-only; no new heuristic flag bridge.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/RegisterAllocator.cs` — validate assumptions after legalization rather than absorbing flag logic.
- `/home/felix/projects/nerdgruppe/blade/TODO.md` — already contains the nearby architectural direction to stop treating MIR values as pure handles.
- `/home/felix/projects/nerdgruppe/blade/RegressionTests/HwTest/hw_multi_return-3val.blade` — motivating regression for call/return flag-space correctness.

**Verification**
1. Add focused MIR/LIR model tests that fail if a bool-like or `bit` result is lowered as a register without an explicit conversion.
2. Add legalization-focused tests that prove the new stage inserts spills and reloads only at true storage-space boundaries.
3. Add regression fixtures for compare-to-branch, call-return flags, `bit` arithmetic spill/reload, and block-parameter flag threading.
4. Run the targeted regression anchor `/home/felix/projects/nerdgruppe/blade/RegressionTests/HwTest/hw_multi_return-3val.blade` after each phase that changes calling-convention or branch handling.
5. Run `just regressions` once the focused slice passes.
6. Run `just coverage` and verify the new flag-space code reaches 100% line coverage.
7. Run `just accept-changes` and review `git diff` before closing the task.

**Decisions**
- A flag value in MIR and LIR is a generic logical 1-bit value living in flag space, not a pre-bound `C` or `Z` hardware lane.
- All bool-like values and the `bit` type use flag space by default; explicit conversion operations mark the places where values cross between flag and register spaces.
- `MirFunction.FlagValues`, branch `ConditionFlag`, `LirCallExtractFlagOperation` as a representation crutch, and `FlagOnlyRegisters` are deleted rather than preserved behind compatibility shims.
- The backend gets one dedicated legalization stage to handle concrete hardware flag lanes and calling-convention adaptation. That stage replaces local heuristics.
- Included scope: end-to-end value-space reshape through MIR, LIR, legalization, ASM lowering, and verification.
- Excluded scope: unrelated frontend syntax changes, new source-language flag syntax, and backend superoptimization beyond what naturally falls out of the new representation.

**Further Considerations**
1. Rename the current `MirFlag` enum during implementation so it no longer conflates “flag-space value” with “specific hardware predicate”; keeping the old name after the reshape will invite bugs.
2. If MIR block parameters are too broad to type precisely in one pass, land a temporary internal helper layer that validates value-space compatibility, but do not keep side dictionaries or register-only aliases in the final design.
3. If the legalization pass reveals that some `bit` operations are better kept register-valued for now, make that an explicit operation-selection rule in MIR lowering rather than reintroducing implicit backend materialization.