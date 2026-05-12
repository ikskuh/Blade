---
# blade-smz5
title: 'REVIEW-X4: Refactor compilation option parsing boilerplate'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:39:19Z
updated_at: 2026-05-12T21:39:19Z
parent: blade-6ks1
---

Imported from TASKS.md.

`CompilationOptionsCommandLine.TryParse` still repeats the same parse or error or continue gate for each option family.

## Todo
- [ ] Introduce a staged dispatcher or result object model so option parsing is easier to audit
- [ ] Centralize error propagation instead of repeating the same control flow after each parse attempt
- [ ] Preserve the current CLI surface and diagnostics
