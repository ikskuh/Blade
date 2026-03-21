# Difference between Implementation and Docs/reference.blade

## CS-13: `u8x4` SIMD type

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`.

- Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`).
- `IsAssignable`: `[4]u8` ↔ `u8x4` (implicit coercion both ways).
- Integer literal → `u8x4` only through array literal `[a,b,c,d]`.
- Future: swizzle operations (deferred, not in reference.blade).
- Tests: `var v: u8x4 = [1,2,3,4];`, coerce from/to `[4]u8`.

---

## CS-21: "assert" statement

`reference.blade` defines `assert` as a statement-like compile-time check:

- `assert <condition>;`
- `assert <condition>, "message";`

This needs explicit syntax support. The current lexer/parser do not know the
`assert` keyword and there is no statement node for it.

Syntax/frontend work:

- Add `assert` as a keyword token.
- Add `AssertStatementSyntax`.
- Parse both legal forms in statement position, so `assert` works both at
  top-level and inside function bodies.
- Parse the optional message as a string literal token, not as a general
  expression. The reference explicitly says the message cannot be a variable
  reference, so the syntax should enforce that directly.

Binder/semantic work:

- `assert` is compile-time only. It should not survive into MIR.
- Bind the condition as an expression and evaluate it immediately.
- The condition must fold to a boolean constant.
- Reuse or generalize the current constant-evaluation helpers. `TryEvaluateConstantInt`
  is too narrow for this task; `assert` needs boolean results, and `CS-20` query
  operators should be usable inside assertions.
- When the condition is `false`, emit one diagnostic code for assertion failure:
  - without message: `assertion failed`
  - with message: `assertion failed: <message>`
- Non-constant conditions should report a separate diagnostic instead of silently
  becoming runtime checks.
- Non-boolean conditions should still use normal type checking.

Recommended implementation shape:

- Introduce a bound statement node for `assert` only if it helps the binder
  pipeline; otherwise the binder can consume the syntax and emit either nothing
  or an error immediately.
- Prefer a reusable `TryEvaluateConstantValue(BoundExpression, out object?)`
  helper over baking the logic into the `assert` path. That helper will also be
  useful for future `comptime` work.

Tests:

- `assert true;` produces no diagnostics.
- `assert false;` produces the generic assertion-failed diagnostic.
- `assert false, "must hold";` produces the same diagnostic code with the custom message.
- `assert 1;` reports a type mismatch against `bool`.
- `const msg: [4]u8 = "oops"; assert false, msg;` is rejected because the message is not a string literal.
- `var x: u32 = 1; assert x == 1;` is rejected because the condition is not compile-time constant.

---

## Non-semantic items already complete (for reference)

These were handled in the syntax frontend refactor and need no further work:

- [x] `~` / `%` / `%=` / `<<<` / `>>>` / `<%<` / `>%>` / `...` tokens
- [x] `and` / `or` / `type` / `union` / `enum` / `bitfield` / `bitcast` / `u8x4` keywords
- [x] Character literals (`'x'`), escape sequences, `z"..."` strings
- [x] Quaternary (`0q`) and octal (`0o`) number literals
- [x] `asm { } -> name: type@Flag;` post-body output binding syntax
- [x] `for(expr) -> [&]item[, index]` binding syntax
- [x] `rep for(expr) -> binding` / `rep loop` (infinite) syntax
- [x] `type Name = ...;` alias declarations
- [x] `asm [volatile] fn name(...) -> ret { body }` declarations
- [x] Union / enum / bitfield / non-packed struct type syntax nodes
- [x] Multi-pointer `[*]` syntax
- [x] Pointer attributes (storage, const, volatile, align) in syntax
- [x] `expr as Type` cast syntax
- [x] `bitcast(Type, expr)` syntax
- [x] `.member` enum literal syntax
- [x] `[expr, expr...]` array literal syntax
- [x] `TypeName { .field = value }` typed struct literal syntax
- [x] Named argument `name = expr` syntax
