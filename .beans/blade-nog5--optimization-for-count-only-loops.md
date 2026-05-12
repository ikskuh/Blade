---
# blade-nog5
title: Optimization for count-only loops
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:33Z
updated_at: 2026-05-12T21:49:33Z
parent: blade-46a7
---

Imported from TODO.md.

```blade
for(8) {
  // ...
}
```

should be lowered to when the loop count is comptime known and non-zero.

```pasm
  MOV     counter, #8
nib_loop:
  ...
  DJNZ    counter, #nib_loop
```

If it's zero, the loop body can be fully omitted when lowering, and must emit a warning.

The optimization can also be used for comptime-known captures with counter and ranges: 

```blade
for(10..20) -> value {
  // ...
}
```

can be lowered to

```pasm
  MOV     counter, #8
  MOV     value,   #10
nib_loop:
  ...
  ADD     value,   #1
  DJNZ    counter, #nib_loop
```

which does not need to compare the iterator value, which saves a lot of cycles.
