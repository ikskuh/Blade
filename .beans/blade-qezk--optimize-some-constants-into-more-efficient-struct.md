---
# blade-qezk
title: Optimize some constants into more efficient structures
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

This can be encoded using e.g. `BITH` or `NOT g_reinterpreted_signed, #0`

```
// EXPECT: pass
// STAGE: final-asm
// CONTAINS:
// - MOV g_reinterpreted_signed, main_c_4294967295
// - main_c_4294967295      LONG $FFFFFFFF
// ! MOV g_reinterpreted_signed, #-1
cog task main {
    
    cog var reinterpreted_signed: i8 = 0;
    reinterpreted_signed = bitcast(i8, 255 as u8);
}
```
