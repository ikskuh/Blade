# Generalize Non-Returning Functions With `noreturn`

## Summary
Introduce `noreturn` as a first-class function property carried by `FunctionSymbol`, parsed through the existing function metadata syntax:

```blade
fn foo() : noreturn, layout(Bla), ... {
}
```

This property becomes the single frontend truth for “this function must not return normally.” `coro fn` implicitly sets that property and stops using its bespoke non-return validation path. The body function of `task _start` also implicitly sets it. Calls to `noreturn` functions are treated as terminating control flow for return-path analysis and lowering.

## Key Changes
### Syntax, symbols, and diagnostics
- Add a `noreturn` metadata property to function metadata parsing beside `layout(...)` and `align(...)`.
- Add a dedicated syntax node for that property and a keyword/token for `noreturn`.
- Extend `FunctionSymbol` with a boolean `IsNoReturn` property.
- Resolve `noreturn` during `ResolveFunctionMetadata(...)` and set it on the symbol there.
- `coro fn` must implicitly set `IsNoReturn = true` even when no explicit metadata is written.
- The entry body function for `task _start` must implicitly set `IsNoReturn = true`.
- Decide duplicate behavior in the same style as existing metadata:
  - repeated explicit `noreturn` is accepted
  - duplicate explicit `noreturn` should produce a warning/error consistent with current metadata policy
  - implicit `coro fn` / `_start` plus explicit `: noreturn` is accepted and not treated as conflicting
- Add/update diagnostics so a `noreturn` function that can return normally reports the same semantic problem independent of calling convention.
- Replace the coroutine-specific “cannot return” validation path with the generalized `noreturn` validation path.
  - explicit `return` inside a `noreturn` function is invalid
  - falling off the end of a `noreturn` function is invalid
  - a loop that can break out and continue to end still counts as returning normally
- Keep the existing `yieldto`-specific validation for coroutines. Only the non-return rule is generalized.

### Binder and control-flow semantics
- Replace the current `FunctionKind.Coro` checks in return binding and function-body exit analysis with checks against `FunctionSymbol.IsNoReturn`.
- Rename or generalize `AnalyzeCoroutineExitPaths(...)` into a generic “normal exit reachability” analysis for non-returning functions.
- `BindReturnStatement(...)` must reject `return` whenever `_currentFunction.IsNoReturn` is true.
- The usual “missing return value on some path” rule still applies only to functions with actual return values; `noreturn` is a separate rule.
- Calls to `noreturn` functions must terminate control flow in analysis:
  - statement-level calls should make subsequent code unreachable for `AlwaysReturns(...)` / exit analysis
  - nested expression calls should still be representable for diagnostics/binding, but any statement/expression tree containing one must be treated as non-continuing for function-exit purposes
- Do not model `noreturn` as a return type. It is a function property/effect only.
- `task _start` remains syntax-wise a task without return syntax; its generated entry-body `FunctionSymbol` simply gets `IsNoReturn = true`.

### IR and backend propagation
- Preserve `IsNoReturn` from bound `FunctionSymbol` through MIR/LIR/ASM-visible function objects where needed for analysis/lowering.
- Remove backend assumptions that entrypoint termination must be synthesized with `blade_halt`.
- Entry-point lowering should instead rely on the semantic fact that runtime `_start` is non-returning.
- Any special “entry point returns by jumping to halt hook” behavior in call graph docs/comments and lowering should be updated to “entry point does not continue because `_start` is `noreturn`”.
- `coro fn` keeps its coroutine ABI and `yieldto` lowering. Only the non-return rule stops being special-cased by calling convention.
- Full reports must continue to include final assembly; `FinalAssemblyWriter` must not synthesize non-modeled halt scaffolding.

### Public/API shape
- `FunctionSymbol` gains `IsNoReturn`.
- Function metadata syntax gains `noreturn`.
- No new compatibility shims or legacy string APIs.
- `FunctionKind` remains about calling convention/kind, not about normal-return behavior.
- If MIR/LIR model classes need the property for analysis, add an explicit boolean there rather than inferring from `FunctionKind`.

## Test Plan
- Parser tests:
  - accepts `fn foo() : noreturn { ... }`
  - accepts mixing `noreturn, layout(...), align(...)`
  - rejects malformed metadata placement/usages
- Binder/semantic tests:
  - plain `fn ... : noreturn` with a reachable end errors
  - plain `fn ... : noreturn { return; }` errors
  - `coro fn` still errors on reachable normal exit, but now through the generalized no-return path
  - `coro fn` plus explicit `: noreturn` is accepted
  - `task _start` body function is implicitly `noreturn`
  - a call to a `noreturn` function satisfies surrounding “must return on all paths” analysis because control flow terminates there
  - a normal function calling a `noreturn` callee and then falling off the end is accepted when no further return is required
- IR/codegen/regression:
  - remove/update regressions expecting `blade_halt`
  - add regressions showing final asm for `_start` no longer depends on synthetic halt labels
  - keep coroutine/yieldto regressions to ensure ABI/lowering behavior is unchanged
- Reports:
  - bound/MIR/LIR dumps should show `noreturn` on functions if function metadata/properties are displayed
  - final assembly report still renders without writer-synthesized halt blocks

## Assumptions And Defaults
- `noreturn` is a metadata property after the signature colon, not a return type and not a function modifier.
- Calls to `noreturn` functions terminate control flow for analysis and lowering.
- `coro fn` keeps coroutine semantics and ABI; only its bespoke non-return validation is removed.
- The body function of `task _start` implicitly receives `IsNoReturn`; no separate syntax is added for tasks.
- Entry-point/backend halt-hook behavior should be removed rather than preserved behind compatibility logic.
