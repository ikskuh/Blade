# Code unreachable through the frontend

## Binder defensive switch fallbacks

- `Blade.Semantics.Binder` lines `1209`, `1220`, `1246`, `1261`, `1299`, `1321`, `1377`, `1395`, and `2568`
- These are the default / impossible-case fallbacks after the parser has already committed to a specific syntax node kind or after operator binding has already filtered the valid token set.
- They look like cleanup candidates for `Debug.Assert` plus a narrow error fallback at the outermost call site, rather than something the frontend can intentionally drive.

## Binder top-level registration fallback

- `Blade.Semantics.Binder` lines `367-377`
- `BindGlobalVariable` falls back to an error symbol only if a previously collected top-level declaration was not registered in the global scope.
- That appears to duplicate an earlier invariant from `DeclareTopLevelVariables`, so it does not look frontend-reachable.

## Binder scalar-width double check

- `Blade.Semantics.Binder` lines `2143-2145`
- `BindBitcastExpression` reaches this block only after both source and target already passed `TypeFacts.IsScalarCastType`, which itself is implemented in terms of `TryGetScalarWidth`.
- This looks like a defensive re-check rather than a frontend-reachable branch.

## Binder named-type builtin path

- `Blade.Semantics.Binder` line `2749`
- Builtin type keywords are parsed as `PrimitiveTypeSyntax`, not `NamedTypeSyntax`, so the `BuiltinTypes.TryGet(...)` success path inside `BindNamedType` does not appear reachable from Blade source.

## Binder comptime support dead branches

- `Blade.Semantics.Binder` lines `1884-1887` and `1899`
- `GetComptimeSupportResult` shares the same body resolver used by the evaluator. The evaluator rejects missing bodies before support analysis is queried, so the "missing body during support lookup" branch looks dead.
- Reporting a `ComptimeFailureKind.None` also appears impossible because `ReportComptimeFailure` is only called after a failure result was already produced.

## Binder assignability redundancy

- `Blade.Semantics.Binder` line `2841`
- `IsAssignable` already returns `true` for `source.IsUndefinedLiteral` one line earlier, so the pointer-specific `undefined` check is shadowed by the broader early return.
