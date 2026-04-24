# Multicore / Task / Layout Redesign Addendum

This section documents only the language parts that changed relative to the
earlier single-cog model.

The central shift is:

- Rename `cog` keyword into `cog` keyword.
- We remove the ability to invoke modules as functions
  - Imported modules with top-level code emit a warning, but the code is never executed
  - Initialization of modules has to be done through an explicit initializer function
- Two new syntax constructs are introduced:
  - `task` is an executable unit of code.
    - A `task` is compiled into an `image` (not a syntax construct) that contains the code, data and shared addresses for executing the `task`.
  - `layout` is a sharable abstract describtion of memory layouts
- Blade programs now require an explicit entry point `task main`.
  - A warning is emitted if it's not `cog task main`.
- Global declarations of `cog var` and `lut var` are not allowed anymore.
  - Only declarable inside a layout.
- `hub var` may still exist at top-level, or inside a layout.

The compiler remains free to optimize storage and code generation heavily,
but the semantic guarantees around layout membership, visibility, and task
spawning are explicit.

## Spawnable units: tasks

A `task` is the user-written declaration of runnable code.
Every task compiles to exactly one image.

The task memory space determines where execution starts semantically:

- `cog task` starts in cog/register execution.
- `lut task` starts in LUT execution.
- `hub task` starts in hub execution.

Important implementation note:

- `cog task` requires no startup shim for cog variables, because the
  hardware-startup path already seeds cog memory through COGINIT loading.
- `lut task` requires a compiler-generated startup shim which initializes LUT
  storage before jumping to the task entrypoint.
- `hub task` may start directly in hub execution.

Those startup shims are lowering details, not source-level constructs.

cog task worker_task() {
    // Reaching the end of a task means that the current cog stops itself.
    //
    // There is no normal caller to return to, so falling off the end of a task
    // body is equivalent to an implicit self-COGSTOP.
}

lut task lut_worker() {
    // This task executes from LUT memory.
    //
    // The compiler must ensure that any LUT variables with non-`undefined`
    // initializers are copied into LUT before the first user-visible
    // instruction executes.
}

hub task hub_worker() {
    // This task executes from Hub memory.
}

//////////////////////////////////////////////////////////////////////////////
// Task parameters
//////////////////////////////////////////////////////////////////////////////

// A task may take zero or one startup parameter.
// The parameter may be any type whose runtime representation fits into 32 bits.
//
// The value is initially passed through PTRA, but semantically it behaves as a
// regular local variable initialized from that startup value.
//
// Because it is a local, it is *not* visible to helper functions declared
// within the task unless it is passed explicitly.

cog task parameterized_task(step: u32) {
    fn helper(x: u32) -> u32 {
        return x + 1;
    }

    var next: u32 = helper(step);

    // `step` is visible here because we are in the task body itself.
    _ = next;
}

//////////////////////////////////////////////////////////////////////////////
// Layouts
//////////////////////////////////////////////////////////////////////////////

// `layout` is the mechanism by which cog/lut/hub storage is defined and
// composed.
//
// Layouts are *not* fully manual placement scripts. Instead, they are a set of
// constraints presented to the compiler.
//
// The compiler must either:
// - produce a valid placement satisfying all constraints, or
// - reject the program because the constraints cannot be satisfied.
//
// The physical order of unconstrained items inside a layout is not part of the
// source-level semantics and is an implementation detail.
//
// Layouts support inheritance / composition with multiple parents:
//
//     layout D : A, B, C { ... }
//
// This is intentionally "multiple inheritance for storage fragments".

layout BaseState {
    cog var local_counter: u32 = 0;
    lut var lut_counter: u32 = undefined;
    hub var shared_counter: u32 = 100;
}

layout AlignedState {
    // Alignment constraints are the primary source-level control for guiding
    // packing, and are especially useful when the programmer wants the emitted
    // layout to support patterns such as ALTI-oriented dense grouping.
    //
    // The exact packing algorithm is the compiler's problem; the semantic
    // guarantee is only that this variable ends up satisfying the requested
    // alignment or compilation fails.
    cog var packed_value: u32 align(8) = 0;
}

layout ExplicitPlacement {
    // Explicit placement syntax is allowed for all global storage classes.
    //
    // This works both with and without `extern`.
    //
    // For `lut var`, this means the programmer can pin a value to a known LUT
    // address while still participating in layout composition.
    lut var table_head: u32 @(0x100) = undefined;
    hub var fixed_hub_flag: u32 @(0x2000) = 1;
}

layout Composite : BaseState, AlignedState, ExplicitPlacement {
    cog var extra_cog_value: u32 = 1;
    lut var lut_table: [16]u32 align(16) = [0...];
}

// Streamer/XBYTE/DDS-style LUT content does not need special syntax.
// Arrays plus `align(...)` plus `@(position)` are sufficient.
layout StreamerFriendly {
    lut var waveform: [256]u32 align(256) @(0x000) = [0...];
}

// Layout inheritance may shadow variables.
// This is legal, but must emit a warning.
layout ShadowingChild : BaseState {
    hub var shared_counter: u32 = 999;
    // The intent here is to allow deliberate shadowing in composed systems,
    // while still warning that a parent member of the same name exists.
}

//////////////////////////////////////////////////////////////////////////////
// Storage rules
//////////////////////////////////////////////////////////////////////////////

// `cog var` and `lut var` are not allowed at plain top-level.
// They are only meaningful as layout members, because their physical storage is
// task/image-relative.
//
// Examples of illegal declarations:
//
// cog var illegal_top_level_cog: u32 = 0;
// lut var illegal_top_level_lut: u32 = 0;
//
// In contrast, `hub var` is allowed both at top-level and inside layouts,
// because Hub memory is globally shared and not duplicated per task/image.

hub var global_hub_visible: u32 = 123;

layout HubScoped {
    hub var global_hub_visible: u32 = 456;
    // This shadows the top-level global name when the layout is imported.
    // The compiler must warn about this case.
    //
    // Unqualified access still resolves to the top-level global.
    // The layout member remains accessible as `HubScoped.global_hub_visible`.
}

//////////////////////////////////////////////////////////////////////////////
// Layout import into tasks
//////////////////////////////////////////////////////////////////////////////

// When a task applies layouts, their members are imported into the task scope.
//
// If multiple layouts contribute the same name, unqualified access becomes
// ambiguous and must be disambiguated with `LayoutName.member`.

layout A {
    cog var x: u32 = 1;
}

layout B {
    cog var x: u32 = 2;
}

cog task ambiguous_import() : A, B {
    // print(x);    // error: ambiguous
    // print(A.x);  // okay
    // print(B.x);  // okay
}

// Layout membership also defines the storage that helper functions inside the
// task may access implicitly, because those functions are associated with the
// task's final implicit layout.

cog task uses_layout() : Composite, HubScoped {
    fn helper() -> u32 {
        // `local_counter`, `lut_counter`, `shared_counter`, etc. are visible
        // here because task-local functions are implicitly bound to the task's
        // final layout.
        local_counter += 1;
        return local_counter;
    }

    var result: u32 = helper();
    _ = result;

    // The top-level hub global wins for the plain name:
    _ = global_hub_visible;

    // The layout-scoped shadowing declaration remains accessible explicitly:
    _ = HubScoped.global_hub_visible;
}

//////////////////////////////////////////////////////////////////////////////
// Function layout association
//////////////////////////////////////////////////////////////////////////////

// Functions outside tasks do not implicitly see cog/lut storage.
// By default they only see top-level hub globals.
//
// If they need access to task-relative layout storage, they must declare an
// explicit layout requirement.
//
// Layout association is never inferred automatically.

hub fn increment_shared() -> u32
  : layout(BaseState)
{
    shared_counter += 1;
    return shared_counter;
}

cog fn increment_local() -> u32
  : layout(BaseState)
{
    local_counter += 1;
    return local_counter;
}

lut fn increment_lut() -> u32
  : layout(BaseState)
{
    lut_counter += 1;
    return lut_counter;
}

// The constructor-style trailing metadata list is the chosen extensible syntax.
hub fn aligned_layout_function(value: u32) -> u32
  : layout(Composite)
  , align(64)
{
    shared_counter += value;
    return shared_counter;
}

// Automatic `fn` remains polymorphic and is not equivalent to an explicit
// `hub fn` merely because the compiler may choose to place its machine code
// into Hub memory for some caller.
//
// The distinction matters:
//
// - explicit `hub fn` has stable "hub function" semantics,
// - automatic `fn` remains fully compiler-controlled and may be cloned,
//   specialized, or removed as needed.

fn automatic_function(value: u32) -> u32 {
    return value + 1;
}

//////////////////////////////////////////////////////////////////////////////
// Function visibility / subset rules
//////////////////////////////////////////////////////////////////////////////

// Layout requirements form a monotonic capability system:
//
// - A function associated with layouts A,B may only be called from a task whose
//   final layout is a superset of A,B.
// - Such a function may only call functions whose required layouts are a subset
//   of its own.
//
// Task-local functions are private because they are attached to the task's
// implicit final layout, which cannot be named from outside.

hub fn uses_base_only() -> u32
  : layout(BaseState)
{
    return shared_counter;
}

hub fn uses_more() -> u32
  : layout(BaseState, AlignedState)
{
    packed_value += 1;

    // Okay: BaseState is a subset of (BaseState, AlignedState).
    return uses_base_only();
}

//////////////////////////////////////////////////////////////////////////////
// Addressable functions and function pointers
//////////////////////////////////////////////////////////////////////////////

// Only explicitly placed functions are addressable.
//
// Plain `fn` has no stable addressable identity because the compiler may clone
// it per task and per caller.

cog fn addressed_cog() {}
lut fn addressed_lut() {}
hub fn addressed_hub() {}

// Examples of legal function pointer types:
//
// var pc:  *cog fn() = addressed_cog;
// var pl:  *lut fn() = addressed_lut;
// var ph:  *hub fn() = addressed_hub;
//
// Function-pointer types also carry calling convention in the full type system.
// Indirect calls always perform the required mode switch automatically.

//////////////////////////////////////////////////////////////////////////////
// Task-local helper functions
//////////////////////////////////////////////////////////////////////////////

// A task acts like a second top-level scope with extras:
//
// - task-local functions are legal,
// - they implicitly see the task layout,
// - they are private to the task,
// - the task may have one startup parameter.

cog task helper_showcase(start: u32) : BaseState {
    fn add_step(value: u32) -> u32 {
        local_counter += value;
        return local_counter;
    }

    var current: u32 = add_step(start);
    _ = current;
}

//////////////////////////////////////////////////////////////////////////////
// Spawn syntax
//////////////////////////////////////////////////////////////////////////////

// The ergonomic forms are:
//
//     spawn    <task-call-expression>
//     spawnpair <task-call-expression>
//
// The operand is syntactically task-call-shaped, not an arbitrary callable.
//
// The operator may be used in three result-consumption modes:
//
// - consume no result: traps on failure
// - consume one result: returns cog id, traps on failure
// - consume two results: returns cog id + success, never traps

cog task spawned_example(value: u32) {
    _ = value;
}

cog task spawn_demo() {
    // 1. Trap-on-error, ignore cog id:
    spawn spawned_example(10);

    // 2. Trap-on-error, keep cog id:
    var id: u32 = spawn spawned_example(20);

    // 3. Non-trapping form:
    var id2: u32 = undefined;
    var ok: bool = undefined;
    id2, ok = spawn spawned_example(30);

    _ = id;
    _ = id2;
    _ = ok;
}

// Pair spawning launches the *same* task body twice.
// The code must distinguish the two cogs itself via COGID.

cog task pair_worker(step: u32) : BaseState {
    var id: u32 = @COGID();

    if ((id & 1) != 0) {
        local_counter += step;
    } else {
        loop {
            _ = local_counter;
            break;
        }
    }
}

cog task pair_demo() {
    spawnpair pair_worker(10);

    var low: u32 = spawnpair pair_worker(20);

    var low2: u32 = undefined;
    var success: bool = undefined;
    low2, success = spawnpair pair_worker(30);

    _ = low;
    _ = low2;
    _ = success;
}

// There is intentionally no dedicated syntax sugar for "replace current cog"
// or "spawn on specific cog". Those forms are considered sensitive enough to
// stay in the advanced builtin/compiler-provided API.

//////////////////////////////////////////////////////////////////////////////
// Imported-module tasks
//////////////////////////////////////////////////////////////////////////////

// Imported modules may declare tasks just like root modules.
//
// The only relevant distinction is that imported modules never gain an implicit
// task around their top-level declarations. Therefore, `cog var` / `lut var`
// at imported-module top-level remain illegal unless placed inside a layout.

//////////////////////////////////////////////////////////////////////////////
// Top-level legality revisited
//////////////////////////////////////////////////////////////////////////////

// This final section restates the crucial rule:
//
// Top-level executable code is legal only in the simple single-cog style.
//
// The following style is legal:
//
//     hub var counter: u32 = 0;
//     fn inc() { counter += 1; }
//     counter = 10;
//     inc();
//
// But as soon as the root module declares any explicit `task`, or uses
// `spawn` / `spawnpair` / advanced builtin spawning, executable top-level code
// is no longer allowed in that root module.
//
// At that point the programmer must move executable behavior into explicit
// tasks, typically:
//
//     cog task main {
//         ...
//     }
//
// This preserves the old simple Blade feel for single-cog programs while making
// multicore structure explicit and analyzable.
