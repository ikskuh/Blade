# Blade Optimization Options

Blade exposes optimization toggles per IR stage:

- `-fmir-opt=<csv>` / `-fno-mir-opt=<csv>`
- `-flir-opt=<csv>` / `-fno-lir-opt=<csv>`
- `-fasmir-opt=<csv>` / `-fno-asmir-opt=<csv>`

Use `*` to refer to all optimizations in a stage. Directives are applied in command-line order.

Example:

```sh
blade demo.blade -fno-asmir-opt=* -fasmir-opt=elide-nops
```

This disables all ASMIR optimizations, then re-enables only `elide-nops`.

## MIR optimizations

- `cost-inline`: Cost-based MIR inlining pass.
- `const-prop`: Constant propagation and constant folding in MIR.
- `copy-prop`: Copy/alias propagation in MIR.
- `cfg-simplify`: Trivial branch/goto threading and control-flow simplification.
- `dce`: MIR dead code elimination and unreachable block removal.

## LIR optimizations

- `copy-prop`: Copy propagation across LIR instructions and terminators.
- `cfg-simplify`: LIR block threading and linear-block merge simplification.
- `dce`: Dead instruction and unreachable block elimination in LIR.

## ASMIR optimizations

- `copy-prop`: Straight-line copy propagation across ASMIR instructions.
- `dce-reg`: Dead pure register-local instruction elimination.
- `drop-jmp-next`: Removes unconditional `JMP` to immediately following label.
- `ret-fusion`: Fuses trailing `RET` into previous instruction predicate (`_RET_`).
- `conditional-move-fusion`: Fuses conditional jump-over-single-instruction into predicated instruction.
- `muxc-fusion`: Fuses `IF_C/IF_NC` `OR/ANDN` pairs into `MUXC`/`MUXNC`.
- `elide-nops`: Removes semantic no-ops (`NOP`, `MOV x,x`, and selected immediate no-op ALU forms).
- `cleanup-self-mov`: Cleans redundant adjacent duplicate labels and self-moves.
