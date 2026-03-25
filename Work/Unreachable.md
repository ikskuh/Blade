# Code unreachable through the frontend

## Binder residual defensive fallbacks

- `Blade.Semantics.Binder` still has a few remaining default / impossible-case sinks around expression, literal, postfix-target, and type binding (`BindExpression`, `BindLiteralExpression`, `BindPostfixUnaryExpression`, `BindType`).
- The easy invariant branches have already been converted to assertions in `Binder.cs`.
- The remaining items need either:
  - a more structural refactor to make the switch sites exhaustive, or
  - additional frontend fixtures if one of them turns out to be genuinely reachable after all.

## Removed MIR/LIR dead paths

- `MirSelectInstruction` / `LirSelectOperation` were unreachable from the frontend. `if (...) ... else ...` expressions lower through block/phi form in `MirLowerer.LowerIfExpression`, and there was no remaining production construction site for the select IR nodes.
- `MirErrorStatementInstruction`, `MirErrorStoreInstruction`, and their LIR counterparts were also unreachable on the normal compile path because `CompilerDriver` only invokes the IR pipeline when binder diagnostics are zero.
