---
# blade-g2xy
title: Properly implement intrinsics
status: todo
type: feature
priority: normal
created_at: 2026-05-12T21:49:32Z
updated_at: 2026-05-12T21:49:32Z
parent: blade-46a7
---

Imported from TODO.md.

- Don't derive them in ASMIR stage, but create proper instances of "IntrinsicFunction" with
  defined parameter lists and return values.
- Instructions like `COGID {#}D {WC}` have a dual-use:
  - `COGID reg` writes own cog id to `reg`, usage is `var id: u32 = @COGID();`
  - `COGID id  WC` writes alive status of cog `id` to `C`. usage is `var status: bool = @COGID(reg);`
  - `COGID #id WC` writes alive status of cog `id` to `C`. usage is `var status: bool = @COGID(10);`
- Instructions like `RDFAST {#}D,{#}S` should be able to take pointers for `S`, but not for `D`.
  - This requires maintaining an additional hand-written instruction database (yaml or json) for all instructions / mnemonics
- Instructions like `RFBYTE D {WC/WZ/WCZ}` should return `u8`, as `D` will receive a zero-extended byte, and never 32 bit
  - This also requires the instruction database to define properties of the consumed or returned values (here: bit count/size)

Also we need `ALIGNW` and `ALIGNL` for storage emission
