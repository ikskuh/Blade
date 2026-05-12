---
# blade-81yo
title: Suboptimal code order generation for blocks
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

```blade
reg var a: u32 = 0;
reg var b: u32 = 0;

if(a == b) { // MARKER 1
    asm volatile { 
        COGATN #1  // MARKER 2
    };
}
else { // MARKER 3
    asm volatile { 
        COGATN #2 // MARKER 4
    };
}
// MARKER 5
```

```spin2
  l_top
  l_top_bb0
    MOV _r3, g_a
    MOV _r2, g_b
    CMP _r3, _r2 WZ 
    WRZ _r2
    TJZ _r2, #l_top_bb3   ' MARKER 3
    JMP #l_top_bb2        ' MARKER 1
  l_top_bb1
    REP #1, #0            ' MARKER 5
  l_top_bb2
    COGATN #1             ' MARKER 2
    JMP #l_top_bb1
  l_top_bb3
    COGATN #2             ' MARKER 4
    JMP #l_top_bb1
```

is definitly worse than

```spin2
  l_top
  l_top_bb0
    MOV _r3, g_a
    MOV _r2, g_b
    CMP _r3, _r2 WZ
    WRZ _r2
    TJZ _r2, #l_top_bb3   ' MARKER 3
                          ' MARKER 1 (no instruction/branch necessary)
    COGATN #1             ' MARKER 2
    JMP #l_top_bb1
  l_top_bb3
    COGATN #2             ' MARKER 4
                          ' (no instruction/branch necessary)
  l_top_bb1
    REP #1, #0            ' MARKER 5
```
