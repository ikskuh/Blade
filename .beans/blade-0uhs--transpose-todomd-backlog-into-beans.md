---
# blade-0uhs
title: Transpose TODO.md backlog into Beans
status: completed
type: task
priority: normal
created_at: 2026-05-12T21:48:41Z
updated_at: 2026-05-12T21:49:33Z
---

Import the backlog currently tracked in TODO.md into Beans so the issue tracker reflects the current TODO source.

## Todo
- [x] Review TODO.md headings and match them against existing beans
- [x] Create a parent epic for newly imported TODO.md items
- [x] Create child beans for missing actionable TODO.md headings
- [x] Validate the resulting Beans set and summarize MCP availability

## Summary of Changes
- Beans MCP tools are not exposed in this Codex session.
- The local `beans` CLI works and was used as the supported fallback.
- Created parent epic `blade-46a7` for imported TODO.md backlog items.
- The parent epic now contains 41 imported child beans.
