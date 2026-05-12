---
# blade-rr59
title: 'LOW-6: Implement non-aligned bitfield insert'
status: todo
type: task
priority: normal
created_at: 2026-05-12T21:38:55Z
updated_at: 2026-05-12T21:38:55Z
parent: blade-jftu
---

Imported from TASKS.md.

`LowerAlignedBitfieldFallback` emits `E0401` for bit widths or offsets that do not align to nibble (4), byte (8), or word (16) boundaries. P2 has `SETNIB`/`SETBYTE`/`SETWORD` for aligned cases only.

## Todo
- [ ] Implement a shift-and-mask sequence for arbitrary bitfield inserts
- [ ] Use `MOVBYTS`/`BITL`/`SHL`/`AND`/`OR` or an equivalent instruction sequence
- [ ] Cover the fallback path with a demonstrator that uses a non-aligned bitfield
