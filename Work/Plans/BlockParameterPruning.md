## Plan: Block Parameter Pruning

Implement canonical block-parameter pruning in MIR, repeat the same cleanup in LIR after LIR-local rewrites, and keep ASMIR as a consumer-only stage that benefits from the reduced parameter sets instead of adding a separate ASM optimization pass. The goal is to remove unused merge/environment parameters and unused flag-valued SSA transport early enough that ASM lowering never emits dead phi-moves, while still preserving correct flag semantics for surviving uses, CFG threading, and argument/parameter alignment invariants.

**Steps**
1. Phase 1: MIR discovery-to-implementation anchor. Add a new MIR optimization pass under `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/` that computes which block parameters are actually used by each reachable block, builds a per-block keep-mask, and rewrites both the target block parameter list and every incoming terminator argument list to the same filtered index set. Reuse the helper patterns in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirOptimizationHelpers.cs` for successor enumeration, predecessor counting, parameter mapping, and value rewriting.
2. Phase 1 details: the MIR pass should run in the normal iterative optimization loop after MIR DCE and CFG simplification have already removed dead instructions and trivial control-flow. Recommended attribute: a new MIR optimization with priority just below `dce` so it executes before post-iteration `flag-propagation`, allowing flag provenance to reattach to the reduced parameter set after dead non-flag and dead flag-valued SSA parameters have both been removed.
3. Phase 1 algorithm: for each reachable MIR block, determine which parameters are live by scanning instruction uses plus terminator uses in that block. Build a list of kept parameter indices. Rewrite the block with only those `MirBlockParameter`s. Then rewrite every predecessor terminator targeting that block so the corresponding argument lists drop the same indices. Preserve blocks with zero kept parameters by emitting empty parameter lists and empty argument lists, not by inventing compatibility shims.
4. Phase 1 edge handling: keep the block structure intact; do not delete blocks in this pass. Preserve entry-block parameters and any function-ABI-sensitive parameters unless the owning abstraction already guarantees they are ordinary SSA merge parameters. Verify that `arguments.Count == target.Parameters.Count` still holds on every incoming edge after rewriting.
5. Phase 2: add an analogous LIR optimization pass under `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/Optimizations/`. The LIR pass should not assume MIR pruning was sufficient, because LIR copy propagation, CFG simplification, and DCE can make previously-live block parameters dead again. Use the same keep-mask + predecessor-argument rewrite shape, adapted to `LirBlockParameter`, `LirGotoTerminator`, and `LirBranchTerminator`.
6. Phase 2 placement: run the LIR pass in the normal iterative LIR optimizer after `dce` so it can clean up parameters exposed by LIR-local optimizations, and so later iterations can simplify any newly-exposed trivial moves or control-flow.
7. Phase 3: ASMIR integration. Do not add a standalone ASMIR block-parameter pruning pass, because ASMIR has no block-parameter abstraction; the relevant artifact is phi-move emission in `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs`. Instead, treat ASMIR work as integration validation: confirm `EmitPhiMoves` and conditioned phi-move emission consume the already-pruned LIR parameter lists without behavioral changes, and keep the existing invariant that argument count matches target parameter count.
8. Phase 3 follow-up: if inspection shows residual dead phi-moves can still be created after lowering for reasons unrelated to block parameters, treat that as a separate ASM optimization task. It should not be mixed into the block-parameter pruning implementation.
9. Testing phase: add focused optimizer unit tests that construct MIR and LIR functions with unused merge parameters, verify the new passes remove only the dead indices, and verify predecessor argument lists shrink in lockstep. Add regression or demonstrator coverage for an `if`/merge case like the multi-return bool example, where extra environment values currently flow through blocks and later show up as dead ASM phi-moves.
10. Verification phase: validate the new passes with targeted optimizer tests first, then `dotnet build --no-restore`, then a narrow regression fixture compile and dump check that compares pre/post ASMIR shape for redundant phi-move traffic, and finally `just regressions` when the slice is stable.

**Relevant files**
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirLowerer.cs` — source of merge/environment block parameters via `LowerIfExpression` and `CreateEnvironmentParameters`; use this as the semantic origin of the problem.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirOptimizationHelpers.cs` — reuse `EnumerateSuccessors`, `ComputeReachableBlocks`, `CreateParameterMap`, and `RewriteValues` when building the MIR pruning pass.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirDeadCodeElimination.cs` — reference liveness/reachability style and place the new MIR pass immediately after this stage.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/Optimizations/MirFlagPropagation.cs` — ensure pruning runs before this post-iteration pass so flag provenance is propagated over the final parameter graph, not the bloated one.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirLowerer.cs` — confirms LIR inherits MIR block parameters 1:1 and therefore needs either inherited or local pruning.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/Optimizations/LirDeadCodeElimination.cs` — current LIR DCE removes dead instructions but preserves block parameters unchanged; primary reference for the LIR pruning pass.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Lir/LirOptimizationHelpers.cs` — reuse reachability and successor traversal helpers for the LIR pass.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/AsmLowerer.cs` — `EmitPhiMoves` and `EmitPhiMovesConditioned` are the ASMIR integration points that should observe fewer arguments after MIR/LIR pruning.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Asm/LivenessAnalysis.cs` — verify remaining phi-moves still participate in interference exactly as before; no algorithm change should be necessary if pruning happens before ASM lowering.
- `/home/felix/projects/nerdgruppe/blade/Blade.Tests/OptimizerTests.cs` — likely home for targeted MIR/LIR optimizer regression tests.
- `/home/felix/projects/nerdgruppe/blade/RegressionTests/HwTest/hw_multi_return-3val.blade` — useful regression anchor for validating that bool environment threading no longer inflates downstream phi traffic.

**Verification**
1. Add MIR-focused unit tests that start from explicit MIR blocks and assert unused block parameters and aligned predecessor arguments are pruned while used parameters remain.
2. Add LIR-focused unit tests that prove LIR-only dead parameters are removed after LIR optimizations, even when MIR would have left them temporarily live.
3. Compile and dump `/home/felix/projects/nerdgruppe/blade/RegressionTests/HwTest/hw_multi_return-3val.blade` before and after the change; confirm the later merge blocks no longer force redundant phi-move materialization in ASMIR.
4. Run `dotnet build --no-restore`.
5. Run a narrow `dotnet test` filter for the new optimizer tests.
6. Run `just regressions` once the targeted checks pass.
7. Run `just accept-changes` before considering the task complete.

**Decisions**
- Include pruning in both MIR and LIR. MIR is the canonical fix; LIR is a second cleanup stage because LIR-local optimizations can create new dead parameters.
- Exclude a standalone ASMIR pruning pass. For this task, ASMIR changes are limited to integration validation and possibly extra assertions or tests around phi-move emission.
- Keep the implementation structural and deletion-first. No compatibility layers, no dummy placeholder parameters, and no widening into general dead-move cleanup beyond what block-parameter pruning directly enables.
- Preserve flag semantics for surviving uses, but do prune dead flag-valued SSA transport. Flag provenance should only continue through surviving parameters and should then be re-derived by existing flag propagation.

**Further Considerations**
1. Decide whether the MIR and LIR passes should share a small internal helper for keep-mask computation and argument filtering, or whether duplication is clearer given the strongly-typed IR models. Recommendation: duplicate the algorithm shape but keep helpers stage-local to avoid weakly-typed abstraction.
2. Decide whether to prune entry/exit block parameters in the first implementation. Recommendation: start by pruning only ordinary merge/loop parameters and leave ABI-shaped entry/exit parameters unchanged unless a concrete dead-parameter case proves they are safe and worthwhile.
3. Decide whether to add a dump-format assertion test for ASMIR phi-move count reduction. Recommendation: yes, but keep it narrow and anchored on one demonstrator so the test protects the intended downstream effect without overfitting exact register names.
