# Code unreachable through the frontend

## Binder residual defensive fallbacks

- `Blade.Semantics.Binder` still has a few remaining default / impossible-case sinks around expression, literal, postfix-target, and type binding (`BindExpression`, `BindLiteralExpression`, `BindPostfixUnaryExpression`, `BindType`).
- The easy invariant branches have already been converted to assertions in `Binder.cs`.
- The remaining items need either:
  - a more structural refactor to make the switch sites exhaustive, or
  - additional frontend fixtures if one of them turns out to be genuinely reachable after all.
