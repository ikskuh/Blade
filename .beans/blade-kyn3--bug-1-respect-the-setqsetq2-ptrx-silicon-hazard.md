---
# blade-kyn3
title: 'BUG-1: Respect the SETQ/SETQ2 + PTRx silicon hazard'
status: todo
type: bug
priority: high
created_at: 2026-05-12T21:38:45Z
updated_at: 2026-05-12T21:38:45Z
parent: blade-1tar
---

Imported from TASKS.md.

The compiler must not emit `ALTx`/`AUG*` instructions between `SETQ`/`SETQ2` and PTRx bulk-transfer instructions.

## Todo
- [ ] Add a regression that exercises bulk PTRx transfer codegen
- [ ] Ensure legalization or scheduling preserves adjacency between `SETQ`/`SETQ2` and the corresponding `RDLONG`/`WRLONG`/`WMLONG` PTRx instruction
- [ ] Keep acceptance criteria at final emitted assembly shape, not just intermediate IR
