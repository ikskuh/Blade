---
# blade-gb3y
title: 'REVIEW-C16: Apply the shared flag-name parser to asm output bindings'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:39:10Z
updated_at: 2026-05-12T21:39:10Z
parent: blade-6ks1
---

Imported from TASKS.md.

Return-item flags already accept `SyntaxFacts.IsIdentifierLike(...)`; asm output bindings still require a strict identifier token.

## Todo
- [ ] Extract one shared parser helper for flag names
- [ ] Apply it to asm output bindings so both flag positions accept the same token set
- [ ] Add positive and negative parser coverage around flag annotations
