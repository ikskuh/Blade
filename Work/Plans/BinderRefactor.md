# Binder Semantics Refactor — Single Namespace, Real Modules, Cross-Module Comptime

## Open Questions
- None (decisions confirmed): single namespace, no shadowing anywhere, exports = all top-level declarations (incl. imported modules), circular imports forbidden with one cycle-chain diagnostic, module identity = `Path.GetFullPath` (OS-dependent path case rules), module names case-sensitive, comptime behaves the same across module boundaries.

## Current Behavior (grounded in repo)
- **Imports** are resolved in `Blade/CompilationModuleLoader.cs` and loaded into `LoadedCompilation.ModulesByFullPath` (currently keyed case-insensitively).
- **Binding is recursive**: `Blade/Semantics/Binder.cs` creates a nested `Binder` per import (`LoadAndBindModule`) and caches via `ImportedModuleDefinition`.
- **Modules are modeled via `ImportedModule`** (`Blade/Semantics/ImportedModule.cs`), and `ModuleSymbol` points to an `ImportedModule` (`Blade/Semantics/Symbols.cs`).
- **Namespace is split**: values/modules/functions live in `Scope`, but types live in binder-local alias tables (`_typeAliases`, `_resolvedTypeAliases`) and are resolved separately.
- **Qualified enum member across modules fails today** because module member access doesn’t expose types (no `ExportedTypes` handling in expression member access).

## Design Options (with tradeoffs)
1) **(Chosen)** Rename current `TypeSymbol` → `BladeType`, introduce new `TypeSymbol : Symbol` that names a `BladeType`.
   - Pro: matches your model exactly; enables a true single namespace for declarations.
   - Con: large mechanical rename across binder/bound/IR.
2) Keep current `TypeSymbol` name and add `TypeDeclSymbol`.
   - Pro: less churn.
   - Con: violates the “TypeSymbol is wrong name” direction and keeps ambiguity.
3) Keep `ImportedModule` as a public semantic abstraction.
   - Pro: less refactor upfront.
   - Con: conflicts with “module identity must be stable” and “ModuleSymbol points to bound module”.

## Recommended Implementation Plan (dependency-ordered, buildable steps)
1) **Loader: fix module identity + case rules**
   - Change `LoadedCompilation.ModulesByFullPath` to use an OS-appropriate comparer (Windows: ignore-case; otherwise: case-sensitive) while keeping module *names* case-sensitive.
   - Keep builtin detection as exact name match (`builtin` only), but treat it like any other module regarding aliasing/collisions.

2) **Types: rename semantic type object**
   - Rename `Blade/Semantics/TypeSymbol.cs` contents from `TypeSymbol` → `BladeType` (and propagate through `BoundExpression.Type`, symbol fields, builtins, IR).
   - Ensure `BuiltinTypes` (or renamed equivalent) maps keywords → `BladeType`.

3) **Namespace unification: introduce declared type symbols**
   - Add new `TypeSymbol(string name, …) : Symbol` that represents a named type declaration and references a `BladeType` once resolved.
   - Replace `TypeAliasSymbol` usage with `TypeSymbol` as the declaration stored in `Scope`.
   - Keep type-alias resolution lazy/cached in binder/module-state (with cycle detection), but the *name* lives in the single symbol table from the start.

4) **No-shadowing rule (everywhere)**
   - Update `Blade/Semantics/Symbols.cs::Scope.TryDeclare` to reject any declaration whose name exists in the current scope **or any parent**.
   - Update `DiagnosticBag.ReportSymbolAlreadyDeclared` message to remove “in this scope” wording (since it can now be a parent-scope collision).

5) **Bound shape: `BoundProgram` → `BoundModule`**
   - Rename `Blade/Semantics/Bound/BoundProgram.cs` → `BoundModule` and adjust `CompilerDriver`, dumps, and `IrPipeline` signatures accordingly.
   - `BoundModule` becomes “one module’s bound result” (statements, global-storage vars, functions, plus import list/namespace handle as needed).

6) **Remove `ImportedModule` from the public semantic model**
   - Change `ModuleSymbol` to hold a `BoundModule` reference instead of `ImportedModule`.
   - Replace `BoundModuleCallExpression(ImportedModule …)` with `BoundModuleCallExpression(BoundModule …)`.
   - Delete `Blade/Semantics/ImportedModule.cs` (or reduce it to an internal transitional helper only during the refactor; end state is no public `ImportedModule`).

7) **Single binder instance + per-module binding state + topo order**
   - Introduce `ModuleBindingState` (per module): module namespace `Scope`, top-level-statement scope, type-resolution cache/stack, bound members, and a reference to already-bound imported `BoundModule`s.
   - `Binder.Bind(LoadedCompilation, …)`:
     - Build import graph from `LoadedCompilation` (including builtin node).
     - Detect cycles with a DFS stack and emit **one** `E0231` diagnostic containing the cycle chain.
     - Topologically order modules and bind in dependency order (builtin first).
   - Eliminate nested binder creation and `_moduleDefinitionCache`/`_moduleBindingStack`.

8) **Qualification: make `a.b.c` work uniformly**
   - Expression binding (`BindMemberAccessExpression`):
     - Module receiver: resolve *any* symbol from the referenced module namespace (module/type/function/global).
     - Type receiver: introduce `BoundTypeExpression` (a comptime “type value”) so `MemorySpace._hub` and `builtin.MemorySpace._hub` bind cleanly.
     - Enum member access: when receiver is a type-value of an enum `BladeType`, bind directly to `BoundEnumLiteralExpression` (so comptime folding works without adding member-access support to comptime).
   - Type binding (`BindNamedType` / `BindQualifiedType`):
     - Resolve via the same symbol-chain logic: module segments walk through exported `ModuleSymbol`s; final segment must be `TypeSymbol` yielding a `BladeType`.

9) **Cross-module comptime constants (no module boundary)**
   - Eagerly compute and cache constant values for eligible const globals (e.g. `reg const`) during module binding and register them in a binder-wide constant table keyed by `Symbol`.
   - Extend `ComptimeEvaluator` with a callback resolver for missing symbols:
     - If symbol is an eligible constant, return its cached value.
     - Otherwise, keep the existing forbidden-symbol behavior.
   - Ensure const evaluation can reference imported-module constants because modules are already bound in topo order.

10) **Backend: ensure imported functions/globals are actually compiled**
   - Update MIR lowering entry to traverse the reachable import graph starting from the root `BoundModule` and lower **all functions** from all reachable modules (not just the root module’s function list).
   - Keep module-call semantics: `BoundModuleCallExpression` lowers the target module’s `TopLevelStatements`.

## Test Plan (must pass)
- Add a regression fixture (in `Demonstrators/` or `RegressionTests/`) proving end-to-end:
  - `assert forward.space == builtin.MemorySpace._hub;`
- Add a regression fixture proving:
  - Shadowing/collisions are rejected across scopes with `E0201`.
- Add a regression fixture proving:
  - Circular imports produce exactly one `E0231` with a chain like `A -> B -> C -> A`.
- Run `just accept-changes` (and rely on its included gates for coverage/regressions).

## Assumptions / Defaults Locked
- Module exports: all top-level decls (type symbols, function symbols, explicit-storage globals, and imported-module symbols); not top-level `var/const` global statements.
- All shadowing forbidden (locals/params cannot reuse any outer name).
- Module identity: `Path.GetFullPath` string with OS-dependent case-sensitivity for file paths; module *names* are always case-sensitive.
- Builtin is a normal module named `builtin`, except it is synthetic/no file and must be a single shared instance.
