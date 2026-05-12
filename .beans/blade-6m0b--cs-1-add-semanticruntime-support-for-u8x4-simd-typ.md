---
# blade-6m0b
title: 'CS-1: Add semantic/runtime support for u8x4 SIMD type'
status: todo
type: feature
priority: low
created_at: 2026-05-12T21:38:40Z
updated_at: 2026-05-12T21:38:40Z
---

Imported from TASKS.md.

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`, and the lexer/parser already recognize `u8x4`. The remaining work is in the type system and coercion rules.

## Todo
- [ ] Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`)
- [ ] Make `IsAssignable` support implicit coercion both ways between `[4]u8` and `u8x4`
- [ ] Restrict integer literal to `u8x4` conversion to array-literal form `[a,b,c,d]`
- [ ] Defer swizzle operations, which are not in `reference.blade`
- [ ] Add tests for `var v: u8x4 = [1,2,3,4];` and coercion from/to `[4]u8`
