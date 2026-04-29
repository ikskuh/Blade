# Blade-Native Runtime Launcher via `builtin.task_main()`

## Summary
Replace external SPIN2 runtime composition with a **Blade-native launcher model** built entirely in the compiler pipeline.

The compiler will no longer support SPIN2 runtime definitions. `--runtime` will accept only a **Blade source file** that defines the boot launcher task for the entry image. The user program still defines `task main` as the required logical entry task. The runtime instead defines `cog task _start`, which is the physical boot task entered at reset.

To connect `_start` to the user program, the builtin module will expose `builtin.task_main()`. In normal user binding mode, calling `builtin.task_main()` is an error. In runtime-launcher binding mode, it resolves to the compiled body function for the user’s `task main`.

This removes name-based hacks, avoids any dependency on FlexSpin for runtime interpretation, and keeps the runtime design native to Blade semantics.

## New Design

### 1. Split logical entry from physical boot
Introduce two explicit compiler concepts:

- **Logical program entry**: the user-defined `task main`
- **Physical boot launcher**: the runtime-defined `cog task _start`

Semantics:
- `task main` remains mandatory in the root user program.
- `_start` is the task that owns the real entry image and hardware startup.
- `_start` is launcher/runtime infrastructure, not the user program entry task.
- The compiler either synthesizes `_start` or loads it from the Blade file passed via `--runtime`.

### 2. `--runtime` becomes a Blade launcher override
`--runtime=<path>` accepts only `.blade` files.

That file is compiled as a dedicated launcher module and must define:
- exactly one `cog task _start`
- optionally helper functions, constants, globals, layouts, and additional tasks

The intended runtime shape becomes:

```blade
cog task _start {
    // runtime setup
    builtin.task_main();
    // runtime teardown / reporting / halt
}
```

Rules:
- the runtime file must not define `task main`
- the runtime file may define additional tasks, but they are not special
- only `_start` is used as the launcher override for the entry image
- any additional runtime-defined tasks participate as normal tasks and images if reachable
- non-entry user tasks remain unchanged

When `--runtime` is absent, the compiler synthesizes a default launcher equivalent to:

```blade
cog task _start {
    builtin.task_main();
}
```

### 3. `builtin.task_main()` is the launcher hook
Expose `builtin.task_main()` through the builtin module as a reserved compiler-integrated callable.

Binding behavior:
- in normal user program binding, any use of `builtin.task_main()` is an error
- in runtime launcher binding mode, `builtin.task_main()` binds to a dedicated compiler symbol representing “invoke the logical entry task body”
- runtime code may call it exactly where control should transfer into the user program
- no special unresolved function names are needed

Lowering behavior:
- the call is rewritten to the actual compiled entry body function for the user’s `task main`
- this is a direct compiler-known call edge, not dynamic dispatch

### 4. Runtime composition becomes semantic, not textual
Delete the runtime template model entirely.

The runtime file is not pasted into final assembly. Instead:
- it is loaded as a special launcher module during compilation
- `_start` becomes the real root task for image planning
- the user `task main` remains the logical program entry body
- `builtin.task_main()` creates the call edge from launcher to program body
- the entire entry image is compiled as one normal Blade-owned image

Additional runtime-defined tasks are handled by normal planning rules:
- if reachable by call/spawn semantics, they are included like any other tasks
- they do not alter the special meaning of `_start`

### 5. Keep the fixed hardware ABI
Keep the hardcoded ABI.

The compiler owns a fixed launcher ABI in the entry image:
- `rt_param0..rt_param7`
- `rt_result`

Model:
- these are reserved fixed COG ABI slots in the entry image
- runtime Blade code and hardware fixtures refer to them as `extern cog var`
- binder resolves them against compiler-defined launcher ABI bindings when runtime mode is active
- the hardware runner continues to patch fixed binary offsets derived from this compiler contract

The ABI layout is compiler-defined, not inferred from runtime source.

### 6. Entry image planning rooted at `_start`
Image planning and lowering must change root ownership:
- entry-image reachability starts from `_start`
- the user `task main` body is included because `_start` calls `builtin.task_main()`
- all entry-image code/resource planning is based on this unified graph
- runtime-defined extra tasks are planned normally if reachable
- spawned/helper tasks keep current image behavior

## Public API / Compiler Model Changes
- Remove `RuntimeTemplate` and all template-composition APIs.
- Replace `CompilationOptions.RuntimeTemplate` and related pipeline fields with a Blade launcher source/module concept.
- `--runtime` accepts only Blade runtime launcher files.
- Add builtin surface for `builtin.task_main()`.
- Add a compiler-only binding mode flag/context for “runtime launcher binding”.
- Reserve `_start` as the launcher task name.
- Keep `task main` as the required logical user program entry task.

## Frontend / IR / Backend Changes

### Frontend
- Binder still requires root `task main` in the user program.
- Add a dedicated launcher-module load/bind path for `--runtime`.
- Validate launcher module invariants:
  - exactly one `cog task _start`
  - no `task main`
  - `builtin.task_main()` allowed only in launcher bind mode
- Additional runtime tasks are allowed and bind as normal task declarations.

### MIR / LIR / ASM
- `_start` becomes the true entry task for image planning and lowering.
- `builtin.task_main()` lowers as a compiler-resolved call edge to the logical entry body for user `task main`.
- Default launcher synthesis happens before MIR lowering so custom and built-in launchers share one pipeline.
- Additional runtime tasks lower exactly like normal tasks.

### Layout / resource planning
- Entry-image ABI slots are reserved before allocation.
- Runtime launcher globals/helpers are planned like normal Blade code/storage.
- Runtime-defined extra tasks get normal image/layout treatment if reachable.

### Final assembly
- Final assembly writer emits only compiler-produced IR/ASM output.
- Remove runtime text composition and runtime DAT/CON insertion.
- `_start` is simply the first emitted task body in the entry image.

## Test Plan
- Loader/binder tests:
  - `--runtime=<blade file>` accepted
  - `--runtime=<spin2 file>` rejected
  - launcher file must contain exactly one `cog task _start`
  - launcher file may not define `task main`
  - launcher file may define additional tasks
  - `builtin.task_main()` is rejected in normal user code
  - `builtin.task_main()` is accepted in runtime launcher bind mode
- Lowering tests:
  - `_start` becomes the real entry image root
  - `builtin.task_main()` lowers to the user `task main` body function
  - default synthesized launcher behaves identically to a minimal custom launcher
  - extra runtime-defined tasks are emitted normally when reachable
- ABI tests:
  - `extern cog var rt_param0..7` / `rt_result` bind to fixed ABI slots
  - hardware runner still works with fixed patch offsets
- Regression tests:
  - migrate `Blade.HwTestRunner/Runtime.spin2` usage to `Runtime.blade`
  - add positive regression for custom Blade launcher runtime
  - add positive regression for runtime file with extra tasks
  - add negative regression for SPIN2 runtime rejection
  - rework runtime-template tests into launcher-runtime tests
  - rerun hardware smoke/runtime fixtures

## Assumptions
- `task main` remains the required logical user entry task.
- `_start` is the reserved physical boot launcher task name.
- `builtin.task_main()` is only valid inside runtime launcher compilation context.
- Runtime support is Blade-only; SPIN2 runtime support is fully removed.
- The fixed runtime ABI remains compiler-owned and hardcoded.
- Extra tasks in the runtime file are allowed, but only `_start` has launcher semantics.
