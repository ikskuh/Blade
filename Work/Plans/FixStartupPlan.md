## Plan: Runtime-Controlled Memory Init

Move automatic memory initialization off the physical launcher entry (`_start`) and onto the logical application entry reached through `builtin.task_main()`. Keep runtime-owned initialization explicit through a new `builtin.init_memory()` hook, and emit a warning late in IR construction only when the runtime launcher declares globals that still require generated init stores but never calls that hook.

**Steps**
1. Model the two entry functions explicitly in bound state. Preserve the logical user `task main` body separately from the physical launcher body so later stages can distinguish “application init target” from “boot launcher body” without relying on the retargeted task entry metadata. This is centered in `Binder.ComposeBoundProgram(...)` and `BoundProgram`.
2. Add runtime-builtin surface for `builtin.init_memory()` alongside the existing runtime-only `builtin.task_main()` export. Reuse `Binder.GetOrCreateBuiltinModule()` for symbol creation, and keep normal user code unable to resolve either runtime-only builtin outside launcher binding mode. This step depends on 1 only if the new bound metadata is used to wire warning spans or init ownership; otherwise it can be implemented in parallel.
3. Split initializer ownership into two explicit sets for the entry image: application-owned globals and runtime-owned globals. Recommended scope: application-owned globals are all entry-image globals except those declared by the runtime launcher module; runtime-owned globals are the globals declared by the runtime launcher module. Use module membership, not `DeclaringLayout`, because `MergeRootEntryTask(...)` retargets launcher members onto the root task layout. This depends on 1.
4. Refactor MIR lowering so generated init stores are reusable and no longer hardwired to the launcher entrypoint. Extract the current store-emission logic from `FunctionLoweringContext.LowerEntryPointBody(...)` into a helper that accepts a selected global set. Then: lower the physical launcher entry body without automatic init; lower the logical application entry body with the application-global init set; lower calls to `builtin.init_memory()` by emitting the same helper over the runtime-global init set. This depends on 2 and 3.
5. Add a late validation/warning step after layout solving and storage-definition classification, before MIR lowering. Use the same “needs generated init” predicate as MIR uses today so fixed-address/static-preinitialized globals do not warn. If the runtime launcher module has at least one runtime-owned global that still requires generated init stores and the launcher body never refers to `builtin.init_memory()`, emit a warning on the launcher task/body span. This depends on 2 and 3 and should be implemented near `IrPipeline.Build(...)` so it has layout and storage-definition knowledge.
6. Update the default launcher/runtime expectations and focused tests. Keep the default synthesized runtime in `CompilerDriver` unchanged except that application memory now initializes when `builtin.task_main()` executes. Update hardware-runtime and regression fixtures so runtime globals that genuinely need init call `builtin.init_memory()` before using them. Add one positive runtime fixture proving runtime init can run before `builtin.task_main()`, and one warning fixture proving omission warns only when required. This depends on 4 and 5.
7. Validate with narrow checks first, then the behavior slice. Run the focused unit tests that cover binder/runtime launcher behavior and regression harness runtime coverage, then run `just regressions` if the targeted checks pass. Do not run `just accept-changes` unless explicitly requested.

**Relevant files**
- `/home/felix/projects/nerdgruppe/blade/Blade/Semantics/Bound/BoundProgram.cs` — add/clarify properties for logical program-main body versus physical launcher body.
- `/home/felix/projects/nerdgruppe/blade/Blade/Semantics/Binder.cs` — `Bind(...)`, `ComposeBoundProgram(...)`, `MergeRootEntryTask(...)`, `GetOrCreateBuiltinModule()`, and `BindCallExpression(...)` are the key reuse points.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/Mir/MirLowerer.cs` — current init injection lives in `LowerEntryPointBody(...)`; this needs to become reusable and callable from the logical main-body path and `builtin.init_memory()` expansion.
- `/home/felix/projects/nerdgruppe/blade/Blade/IR/IrPipeline.cs` — best insertion point for the warning because layout/storage-definition knowledge is available here before MIR lowering starts.
- `/home/felix/projects/nerdgruppe/blade/Blade/CompilerDriver.cs` — confirm the default runtime launcher still just calls `builtin.task_main()`.
- `/home/felix/projects/nerdgruppe/blade/Blade/Diagnostics/Messages.def` — add the new warning definition and regenerate diagnostics.
- `/home/felix/projects/nerdgruppe/blade/Blade.HwTestRunner/Runtime.blade` — runtime launcher likely needs an explicit `builtin.init_memory()` call if it keeps non-static runtime globals.
- `/home/felix/projects/nerdgruppe/blade/RegressionTests/Runtime/runtime_launcher.blade` — good regression anchor for runtime-launcher-specific semantics.
- `/home/felix/projects/nerdgruppe/blade/Blade.Tests/RegressionHarnessTests.cs` — existing runtime launcher tests and hardware-runtime fixture helpers are the best narrow test anchors.
- `/home/felix/projects/nerdgruppe/blade/Blade.Tests/BinderTests.cs` — useful only for small binder-surface assertions if the behavior cannot be expressed cleanly through regressions.

**Verification**
1. Add/adjust a focused regression for a runtime launcher with its own non-static global and explicit `builtin.init_memory()` before `builtin.task_main()`, then confirm emitted asm/order matches expectation.
2. Add/adjust a focused warning regression where the runtime launcher has a non-static runtime global, omits `builtin.init_memory()`, and still compiles with the new warning.
3. Run targeted NUnit coverage around `RegressionHarnessTests` and any binder/diagnostic tests touched for the new warning and builtin surface.
4. Run `just regressions` after the narrow checks pass.
5. If coverage drops on the touched paths, use a regression or focused test to hit both “warn” and “no warn” branches, plus both runtime/app init emission paths.

**Decisions**
- Application automatic init moves to the logical user `task main` body reached through `builtin.task_main()`, not the physical launcher `_start` body.
- `builtin.init_memory()` is runtime-launcher-only and exists so the runtime can initialize its own globals before transferring control.
- The warning should be based on globals that still require generated runtime stores after storage-definition/layout analysis, not merely “has an initializer,” to avoid false positives for statically preinitialized storage.
- Included scope: runtime-owned explicit init and warning cover globals declared by the runtime launcher module. Excluded from this change unless encountered as a concrete blocker: broader provenance tracking for shared/imported helper modules used by both runtime and user code.

**Further Considerations**
1. If implementation friction appears around identifying the logical user-main body after `MergeRootEntryTask(...)`, prefer adding explicit `BoundProgram` properties over inferring from retargeted `TaskSymbol.EntryFunction`; the current `EntryPointFunction` naming already conflates launcher and logical entry.
2. If the warning cannot reuse MIR’s exact init predicate without awkward duplication, factor the predicate into a small shared helper used by both the warning pass and MIR lowering instead of maintaining two similar rules.
3. Prefer regression fixtures over binder-only unit tests for the warning and init-order behavior, because the observable requirement is end-to-end codegen order, not just symbol binding.