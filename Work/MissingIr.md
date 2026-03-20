# Missing IR Lowerings

This file tracks IR operations that are still not lowered end-to-end today.

Source of truth:

- `Blade/IR/Mir/MirLowerer.cs`
- `Blade/IR/Lir/LirLowerer.cs`
- `Blade/IR/Asm/AsmLowerer.cs`

## MIR -> LIR

There is no dedicated unsupported-lowering diagnostic at this stage.

`LirLowerer` mechanically lowers every current `MirInstruction` variant into LIR.
When the pipeline reports `E0401_UnsupportedLowering`, that diagnostic currently
comes from `AsmLowerer` during LIR -> ASMIR lowering, not from MIR -> LIR.

## LIR -> ASMIR

`AsmLowerer` reports `E0401_UnsupportedLowering`. Some diagnostics are
normalized before reporting:

- `load.member.*` -> `load.member`
- `store.member:*` -> `store.member`
- `structlit.*` -> `structlit`
- `yieldto:<target>` -> `yieldto`

## Currently Reachable Unsupported Lowerings

These are produced by the current compiler and can reach `AsmLowerer` from
normal source programs.

### Aggregate values and member access

- `structlit`
  - Produced for regular struct literals such as `.{ ... }` and typed struct
    literals after binding.
  - MIR emits `structlit.<field>...`; diagnostics are normalized to `structlit`.

- `load.member`
  - Produced for struct/union member reads.
  - MIR emits `load.member.<name>.<byteOffset>`; diagnostics are normalized to
    `load.member`.

- `store.member`
  - Produced for struct/union member writes.
  - MIR emits store targets like `member:<name>:<byteOffset>`; diagnostics are
    normalized to `store.member`.

### Register-space indexing and dereference

- `load.index.reg`
  - Register-backed array indexing is still unsupported in ASMIR lowering.

- `store.index.reg`
  - Register-backed array stores, including array literal materialization, are
    still unsupported in ASMIR lowering.

- `load.deref.reg`
  - Register-space single-pointer dereference loads are unsupported.

- `store.deref.reg`
  - Register-space single-pointer dereference stores are unsupported.

Hub and LUT index/deref lowering do exist; the missing cases are the `reg`
variants.

### Range values

- `range`
  - Range expressions lower to a MIR op but have no ASMIR lowering.

### Placeholder loop / shielding ops

- `rep.setup`
- `rep.iter`
- `repfor.setup`
- `repfor.iter`
- `noirq.begin`
- `noirq.end`

These are emitted today as placeholder MIR ops for `rep`, `rep for`, and
`noirq`.

### Coroutine / interrupt transfer placeholders

- `yield`
- `yieldto`

These currently lower only to TODO comments in ASMIR lowering.

### Bitfield insert fallback gaps

- `bitfield.insert.<bitOffset>.<bitWidth>` for unsupported shapes

Currently supported insert shapes are:

- whole-word replace: `bitWidth == 32 && bitOffset == 0`
- single-bit insert
- nibble-aligned 4-bit insert
- byte-aligned 8-bit insert
- word-aligned 16-bit insert

Other insert shapes still report `E0401_UnsupportedLowering`.

Bitfield extract is in better shape: the lowerer has a generic fallback for
non-aligned extract cases, so the known gap is insert, not extract.

## Internal / Fallback-Only Unsupported Lowerings

These are not normal user-facing language gaps, but the lowerer can still
report them in fallback paths.

- `phi-move`
  - Reported if ASMIR lowering cannot resolve a block parameter target while
    emitting phi argument moves.

- `error.statement`
  - Emitted for bound error statements that survive far enough into the IR
    pipeline.

- arbitrary unknown opcode names
  - `AsmLowerer` has a final catch-all for unrecognized LIR opcodes.

- unknown non-`LirOpInstruction` instruction kinds
  - There is also a catch-all for unexpected LIR instruction subclasses.

## Currently Unused Support Path

`AsmLowerer` contains `LowerPseudo(...)` support for `pseudo.*` opcodes, but the
current MIR generator does not emit `MirPseudoInstruction`. The active
placeholder loop/shielding ops listed above are plain MIR/LIR opcodes such as
`rep.setup`, not `pseudo.rep.setup`.
