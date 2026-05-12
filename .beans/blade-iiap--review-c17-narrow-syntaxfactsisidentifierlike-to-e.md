---
# blade-iiap
title: 'REVIEW-C17: Narrow SyntaxFacts.IsIdentifierLike to explicit contextual cases'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:39:14Z
updated_at: 2026-05-12T21:39:14Z
parent: blade-6ks1
---

Imported from TASKS.md.

`IsIdentifierLike` currently accepts identifiers plus every keyword in the keyword table.

## Todo
- [ ] Replace the broad keyword-table check with explicit allowlists or smaller context-specific predicates
- [ ] Audit the existing call sites so each one admits only the intended keyword subset
- [ ] Add parser coverage for accepted and rejected contextual names
