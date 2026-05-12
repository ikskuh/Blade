---
# blade-x3i0
title: 'BUG-2: Respect the AUGS + immediate ALTx silicon hazard'
status: todo
type: bug
priority: low
created_at: 2026-05-12T21:38:50Z
updated_at: 2026-05-12T21:38:50Z
parent: blade-1tar
---

Imported from TASKS.md.

The compiler must not let an `AUGS` intended for one instruction leak into an intervening immediate `ALTx`.

## Todo
- [ ] Add a regression around large-immediate codegen with an intervening `ALTx` instruction
- [ ] Ensure legalization does not emit an immediate `ALTx` that consumes or preserves the wrong `AUGS`
- [ ] Validate the final assembly ordering and operands so the hazard cannot occur
