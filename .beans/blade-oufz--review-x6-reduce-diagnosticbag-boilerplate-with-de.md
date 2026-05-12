---
# blade-oufz
title: 'REVIEW-X6: Reduce DiagnosticBag boilerplate with descriptor-driven reporting'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:39:23Z
updated_at: 2026-05-12T21:39:23Z
parent: blade-6ks1
---

Imported from TASKS.md.

`DiagnosticBag` still exposes a large repetitive surface of near-identical reporting helpers.

## Todo
- [ ] Centralize diagnostic descriptors and format templates
- [ ] Keep typed wrappers only where they add real readability or type safety
- [ ] Preserve existing diagnostic codes and message text
