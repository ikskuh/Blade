---
# blade-hs0c
title: Argument/return fusion
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

Implement optimization that function argument/retval storage places can be fused.
Different labels for clarity, but same memory slot for efficiency when proven that 
they cannot overlap anyways.
