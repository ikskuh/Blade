---
# blade-xboa
title: Rework final assembly writer to use less "hacks"
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

`functionNames.Contains(name)` is bad code. We already know everything from
the symbol types themselves.

`FormatPlaceOperand` and `FormatSymbolOperand` seem 100% redundant.

Why isn't a "place" a symbol?
