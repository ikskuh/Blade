---
# blade-vo52
title: Inline asm should allow expression operands
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:33Z
updated_at: 2026-05-12T21:49:33Z
parent: blade-46a7
---

Imported from TODO.md.

Use cases:

```blade
lut var foo: u32 = 0;

asm {
  RDLUT %1, #{&foo} // should be usable for getting the address of a variable
  ADD %1, #{'A' - 10} // for better constants
};
```
