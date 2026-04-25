# Image And Layout Refactor Plan

## Summary
Implement `Image` as the backend unit produced from each reachable `task`. Parser and binder stay single-pass. After binding, add image planning that discovers tasks reachable from `task main`, solves stable layout addresses once, then runs ASMIR and register allocation per image.

First implementation includes `cog task`, direct-start `hub task`, `cog var`, `lut var`, `hub var`, and real `spawn` / `spawnpair`. `lut task` code generation remains unsupported for now, but LUT data in layouts is in scope.

## Key Changes

- Add image planning between binding and IR:
  - `ImagePlan`: entry image, all reachable task images, shared layout solution, image labels, task-to-image lookup.
  - `ImageDescriptor`: task symbol, execution mode, task entry function, reachable function closure, referenced layout/storage set.
  - Reachability starts at `task main`, follows normal calls transitively, and records `spawn` / `spawnpair` task edges as separate images.
  - Unreachable tasks remain bound but are not emitted.

- Make task entry functions match task storage:
  - `cog task` entry body is a `cog fn`.
  - `hub task` entry body is a `hub fn`.
  - `lut task` entry body is a `lut fn`, but image emission reports unsupported in this pass.
  - Enforce this explicitly in symbols/planning instead of treating task storage as only metadata.

- Replace global placement with layout-aware placement:
  - `LayoutSolver` flattens parent layouts and assigns stable addresses for `cog`, `lut`, and `hub` layout members.
  - Respect size, alignment, `@(position)`, extern/fixed aliases, and storage class.
  - `cog` and `lut` layout addresses are image-relative but stable across every image using that layout.
  - `hub` layout and top-level hub addresses are program-global.
  - Reject overlap, impossible alignment, out-of-range addresses, and unsatisfied constraints.

- Treat cog code and cog data as one address space:
  - A cog image has 496 longs total for executable cog code, `cog fn` bodies, `cog var`, temporary registers, parameters, and return slots.
  - Code always starts at cog address `0x000`.
  - Allocate fixed cog variables first, then allocate unconstrained cog variables from the back downward, around `0x1EF`/`0x1F0`, leaving code and ABI temporaries to grow from the front.
  - After ASM lowering/register allocation, validate that the highest code/temp slot does not collide with the lowest backward-allocated variable slot.
  - `cog fn` functions included in an image consume front-growing cog code space just like the task body.

- Keep LUT data, defer LUT code:
  - Layout `lut var` members are solved and emitted.
  - LUT data uses the existing `org $200`/LUT data model where possible, but placement must become solver-driven rather than sorted-only emission.
  - Any reachable `lut fn` or `lut task` code path gets a clear unsupported diagnostic until LUT code images are designed.

- Refactor IR and emission around images:
  - Introduce `IrProgramBuildResult` containing per-image MIR/LIR/ASM artifacts.
  - MIR/LIR may be rebuilt per image or shared only where storage identity remains image-safe.
  - Run ASM lowering, optimization, register allocation, and legalization per image.
  - Emit the entry image first at hub file address `0x0000`.
  - Emit non-entry images as hub-resident image blocks with stable labels for `COGINIT`.

- Implement spawn lowering:
  - Stop reporting unsupported lowering in the binder.
  - Add MIR/LIR spawn operations carrying target `TaskSymbol`, startup argument, operator kind, and requested result count.
  - Lower `spawn` to `SETQ <startup-value-or-0>` plus `COGINIT` using the target image start label.
  - Zero/one-result forms trap on failure; two-result form returns cog id plus success.
  - Lower `spawnpair` as two launches of the same image, returning the lower cog id and success only if both launches succeed.

## Public Interfaces And Diagnostics

- Add typed objects:
  - `ImageDescriptor` / `ImageSymbol` for image identity.
  - `ImageStartSymbol` for assembly label references.
  - `LayoutSlot` for solved layout member addresses.
  - `ImageStorageSlot` for image-relative storage use.
- Replace or extend `StoragePlace` so a cog/lut storage reference can carry solved slot identity without using emitted names as semantic identity.
- Add diagnostics for layout overlap, invalid fixed address, impossible alignment, image overflow, code/data collision, unsupported reachable LUT code, and unsupported `lut task` emission.

## Test Plan

- Add regression fixtures for:
  - one `cog task main` emits one entry image at file start,
  - spawned `cog task` emits a second image and uses `COGINIT`,
  - spawned `hub task` emits a direct-start hub image,
  - transitive spawn reachability emits required images only,
  - `cog var` fixed and unconstrained addresses remain stable across images,
  - cog code/data collision fails,
  - `lut var` inside layout is accepted and emitted,
  - `lut var` fixed/align conflicts fail,
  - reachable `lut task` or `lut fn` code fails with the temporary unsupported diagnostic.
- Flip existing `RegressionTests/TaskLayoutRefactor/pass_spawn_*` and `pass_spawnpair_forms.blade` from `xfail` when spawn codegen passes.
- Update dumps to show image boundaries, image mode, start label/address, included functions, and solved storage slots.
- Verification target remains `just accept-changes`; no Zig command is required for this plan.

## Assumptions

- First implementation supports `cog task` and direct-start `hub task`.
- LUT data is fully in scope; LUT code is explicitly out of scope.
- Layout solving happens once per program and is the source of truth for every image.
- Cog image allocation is two-sided: code from the front, layout cog variables from the back, with a final collision check.
- Per-image ASMIR/register allocation is required because each image has its own physical cog/LUT contents.
