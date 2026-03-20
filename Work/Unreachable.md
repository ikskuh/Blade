# Code unreachable through the frontend

## Binder.ResolveVariableSymbol

- Date: 2026-03-20
- Location: `Blade/Semantics/Binder.cs`
- Reason: `ResolveVariableSymbol(Token token)` currently has no call sites anywhere in the compiler, so it cannot be exercised through Blade source input.
