# Binder Semantics Refactor

You are working in the Blade compiler repository (C#, .NET). Your task is to **propose a careful plan and then implement it** to fix/clean up the **semantic binder** and its import/module/type/name-resolution semantics.

Important: **Do not assume a solution.** Ask me open questions first. Wait for answers before making invasive changes.

## Context (current repo reality)

Before proposing changes, read and summarize (briefly) the current behavior from the code:

- `Blade/Semantics/Binder.cs`
- `Blade/Semantics/Symbols.cs` (especially `Scope`, `Symbol`, `ModuleSymbol`)
- `Blade/Semantics/ImportedModule.cs`
- Any `Bound*` types involved in module/program representation (search for `BoundProgram`)
- `Blade/CompilationModuleLoader.cs` (import loading, resolved paths, aliasing)

You must ground your plan in what the code actually does today (with file/identifier references).

## Goals (required end-state)

### 1) Single namespace (everything is a `Symbol`)

Introduce a **single namespace** for:

- types (aliases, structs/unions/enums/bitfields, etc.)
- module imports
- functions
- variables / constants
- any other declared names

This should be achieved by representing **all declarations as `Symbol`** instances stored in scopes (or a single authoritative module namespace).

### 2) Remove (or radically reduce) `ImportedModule`

We already have `ModuleSymbol` for imported modules.

- Evaluate what `ImportedModule` currently *contributes* (exports snapshot? caching? indirection? avoiding cycles?).
- Propose how to remove it (or reduce it to an internal detail) such that the public name-resolution model becomes: **a `ModuleSymbol` points to the bound module**.

### 3) Rename `BoundProgram` → `BoundModule` (or equivalent)

From my understanding, `BoundProgram` is not a whole-program abstraction; it’s really “a bound module”.

- Rename it into a more accurate shape (likely `BoundModule`).
- Ensure `ModuleSymbol` points at that bound module object.

### 4) Single builtin module instance

There must be exactly **one** builtin module instance (not recreated per import).

Reason: module identity must be stable, and future comptime comparisons like `mod_a == mod_b` must not break due to accidental duplication.

### 5) No namespace pollution by imports

`import foo;` must **not** inject `foo`’s exported members into the importing module’s top-level namespace.

I only want the alias itself (`foo`) in the namespace; members are accessed via qualification.

### 6) Qualification must support enum members from imported module types

This must work:

`assert forward.space == builtin.MemorySpace._hub;`

That implies:

- you can access a **type** from a module (`builtin.MemorySpace`)
- you can access an **enum member** from that type (`._hub`)
- you can use it in an expression and fold/evaluate it in comptime contexts if needed

### 7) Comptime constants must flow across module boundaries

Imported modules must expose constant values to importers where the language semantics demand it (otherwise the language becomes hard to use for real projects).

Be explicit about:

- which things are “constant” (const variables? enum members? constexpr results?)
- where they live (symbol metadata? bound nodes? dedicated constant table?)
- how they’re evaluated and cached, across modules

### 8) Single `Binder` instance; per-module state objects; import graph ordering

Avoid recursive/nested binders for imported modules.

Desired structure:

- a single `Binder` orchestrates binding of all modules
- module-local binding data lives in a local “state” object (e.g. `ModuleBindingState`)
- binder constructs the builtin module once and binds it globally before everything else
- binder performs a **topological sort** of the import graph and binds modules in dependency order
- circular imports: define the intended semantics and diagnostics

## Non-negotiable constraints

- Ask open questions rather than guessing, especially around name collisions, visibility, module identity, and comptime.
- Do not “fix” semantics by reintroducing import namespace pollution.
- Avoid large sweeping changes until there’s an agreed plan and migration strategy.
- Preserve diagnostics quality (error spans and messages should not regress).
- Keep builds warning-free and follow repo style (explicit types where not obvious, etc.).

## What I want from you (deliverables)

### Phase 1: Questions (must come first)

Ask me open questions about the intended semantics, including at minimum:

- **Name collisions:** Can a type and a value share the same name in a module? If not, what diagnostic and at what stage?
- **Exports:** What is “exported” by default? (All top-level decls? Only marked? Today’s behavior?)
- **Import aliasing:** Is `import "path" as alias;` the only way, or can alias be implicit from file/module name?
- **Builtin module name:** Should it always be `builtin`? Can users shadow it? Should it be reserved?
- **Module identity:** What defines “same module”? Resolved full path? Normalized canonical path? Case sensitivity?
- **Circular imports:** Are they forbidden always? If allowed, what’s the model (forward-declared module namespaces, partial binding, etc.)?
- **Comptime model:** What constructs are allowed at comptime and must be importable (const globals, enum members, pure function results, etc.)?
- **Qualified resolution syntax:** Are `a.b.c` chains allowed for nested imports? Can modules re-export modules?

Do not proceed to implementation until I answer.

### Phase 2: Proposed plan

Write a concrete plan that includes:

- A small “before/after” description of name resolution rules (values vs types vs modules) and how they unify.
- The new core data structures (symbol kinds, bound module shape, module namespace table, constant table).
- A migration strategy that keeps the repo building at each step (sequenced refactor).
- How you will preserve or improve diagnostics and spans during the refactor.
- How import graph sorting is computed and where it lives.

### Phase 3: Implementation

After questions are answered and the plan is approved:

- Implement the refactor in small, reviewable commits (still in one PR/patch, but staged).
- Update call sites accordingly (`Binder`, module loader, bound nodes, etc.).
- Ensure there is only one builtin module instance.
- Ensure `assert forward.space == builtin.MemorySpace._hub;` works end-to-end (add/adjust a regression fixture if needed).

### Phase 4: Verification

Run and report results for:

- `just accept-changes`

If something fails and is unrelated, call it out explicitly; do not fix unrelated failures.

## Helpful notes (do not treat as final design)

- Today, “values” are stored in `Scope`, while “types” are resolved via binder-local alias/type tables. Unifying these will require deciding how type symbols participate in the namespace.
- Today, module member access in expressions resolves functions/variables/modules, while qualified type syntax resolves types. To support `builtin.MemorySpace._hub`, you likely need a coherent model for “type-as-namespace” for enums (and possibly other types).
- If you change symbol storage, consider how you’ll keep lookups fast and deterministic (string comparer, shadowing rules, etc.).

## Output format

When responding:

1) Start by listing your open questions (bulleted), grouped by topic.
2) Then summarize what you learned from reading the current code (short).
3) Then propose 2–3 design options with tradeoffs.
4) Then propose a recommended plan (stepwise, dependency-ordered).
