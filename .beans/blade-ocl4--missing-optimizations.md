---
# blade-ocl4
title: Missing optimizations
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:31Z
updated_at: 2026-05-12T21:49:31Z
parent: blade-46a7
---

Imported from TODO.md.

```
    ' function count_string (Leaf)
  count_string
  count_string_bb0
    MOV _r2, _r2
    MOV _r1, #0
    JMP #count_string_bb2
  count_string_bb1
    _RET_ MOV PA, _r3
  count_string_bb2
    MOV _r4, #0
    ADD _r4, _r2
    RDBYTE _r5, _r4
    MOV _r4, #0
    CMP _r5, _r4 WZ
    WRNZ _r4
    CMP _r4, #0 WZ
    IF_Z MOV _r3, _r1
    IF_Z JMP #count_string_bb1
  count_string_bb3
    JMP #count_string_bb2
```

is far from optimal code
