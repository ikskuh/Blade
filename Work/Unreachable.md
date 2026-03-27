# Code unreachable through the frontend

## Binder residual defensive fallbacks

- `Blade.Semantics.Binder` still has a few remaining default / impossible-case sinks around expression, literal, postfix-target, and type binding (`BindExpression`, `BindLiteralExpression`, `BindPostfixUnaryExpression`, `BindType`).
- The easy invariant branches have already been converted to assertions in `Binder.cs`.
- The remaining items need either:
  - a more structural refactor to make the switch sites exhaustive, or
  - additional frontend fixtures if one of them turns out to be genuinely reachable after all.

## Dead syntax node classes (found by BLD0001 analyzer)

- `AsmFlagOutputSyntax` — a syntax node for the `-> @flag` part of an ASM output binding. The parser never constructs it; `FlagAnnotationSyntax` is used instead. Only referenced in `SyntaxNodeConstructionTests`. Deleted 2026-03-27.
- `SeparatedSyntaxList` (static helper class) — provided `Empty<T>()` factory method. Never called from the compiler; only from `SyntaxNodeConstructionTests`. Deleted 2026-03-27.

## Removed MIR/LIR dead paths

- `MirSelectInstruction` / `LirSelectOperation` were unreachable from the frontend. `if (...) ... else ...` expressions lower through block/phi form in `MirLowerer.LowerIfExpression`, and there was no remaining production construction site for the select IR nodes.
- `MirErrorStatementInstruction`, `MirErrorStoreInstruction`, and their LIR counterparts were also unreachable on the normal compile path because `CompilerDriver` only invokes the IR pipeline when binder diagnostics are zero.
