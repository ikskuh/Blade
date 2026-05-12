---
# blade-h5at
title: Optimizations
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

- Implement load/store for larger types between (register|lut)<->hub with block transfers.
- Implement load/store for register-stored arrays with
  - u32: `ALTS`, `ALTD`, `ALTR`
  - u16: `ALTSW`, `ALTGW`, `SETWORD`, `GETWORD`
  - u8: `ALTSB`, `ALTGB`, `SETBYTE`, `GETBYTE`
  - nib: `ALTSN`, `ALTGN`, `SETNIB`, `GETNIB`
  - bool/bit: `BITZ`, `BITNZ`, `BITC`, `BITNZ`, `BITH`, `BITL`, `BITNOT`, `TESTB`, `TESTBN`
- Implement common constant multiply/divide strategies (QMUL takes 51 cycles, so we have up to 25 instructions before a blocking QMUL is good)
  - `* POT` == shift by log2(pot)
  - `* 3` = `a*2 + a`
  - `* 5` = `a*4 + a`
  - `* 6` = `a*4 + a*2`
  - `* 7` = `a*8 - a`
  - ...
