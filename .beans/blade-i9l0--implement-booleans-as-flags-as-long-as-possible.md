---
# blade-i9l0
title: Implement booleans as flags as long as possible
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

Right now, booleans are lowered to integers, then upgraded to flags
when needed for branching. This can be inverted to lower them to flags
first, then upgrade to integers when out of flags.
