---
# blade-ettm
title: immediate values are still going through a register often
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

```pasm
    MOV _r4, #0
    CMP _r5, _r4 WZ
```

is generated, which could also just be `CMP _r5, #0 WZ`

most likely issue is that both LIR and ASMIR cannot represent immediates, thus we cannot inline these in an early stage yet.

probable solution is to:

- Add immediate value forwarding to ASMIR optimizations
