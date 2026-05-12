---
# blade-6kdd
title: Missed optimization
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

```blade
reg var shared: u32 = 0;
shared = shared + 1;
```

compiles to

```pasm
MOV _r4, g_shared
ADD _r4, #1
MOV g_shared, _r4
```

while `shared += 1` compiles to `ADD _r4, #1`.

`Demonstrators/Optimizations/asmir-global_reg-operator-no-copy.blade`
