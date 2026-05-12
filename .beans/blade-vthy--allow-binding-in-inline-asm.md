---
# blade-vthy
title: Allow `#{binding}` in inline asm
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:33Z
updated_at: 2026-05-12T21:49:33Z
parent: blade-46a7
---

Imported from TODO.md.

```
ADD {value}, #{HEX_CHAR_ASC_OFFSET}
```

should enforce the operand to be evaluated at comptime and be embedded as a constant.

This still means we should can potentially lower the operand into a cog constant for deduplication/AUGx reasons.
