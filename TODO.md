# Planned Changes

## `CALLPA` bindings are important

Leaf functions through CALL/PA should prefer the "PA" parameter for their first parameter value.

This plays together nicely with the register allocator running backwards.

## Implementation of array loads/stores

Right now only the high-level part is implemented, but lowering the array access is not.

## Implementation of `lut var`

LUT variables need explicit indexing through RDLUT, WRLUT

## Implementation of `hub var`

LUT variables need explicit indexing through different RD/WR instructions.

## `asm fn`

Similar to a Zig proposal:

- Function body is only inline assembly.
- Function contract must be obeyed by the implementor
- Storage for parameters is allocated

```blade
// with inline asm:
fn test_and_set_bit(val: u32, bit_num: u32) -> u32 {
    reg var out: u32 = 0;
    asm {
        MOV   {out}, {val}
        TESTB {out}, {bit_num} WC
        IF_NC BITH  {out}, {bit_num}
    };
    return out;
}

// with asm fn:
asm fn test_and_set_bit(val: u32, bit_num: u32) -> u32, bool@C {
          MOV   {out}, {val}
          TESTB {out}, {bit_num} WC
    IF_NC BITH  {out}, {bit_num}
          RET   WZ
}
```

## `rec fn` seems to miscompile

Validate that `rec fn` uses CALLB and stack spilling when calling other rec functions.

## Assert that DIRA, OUTA, ... names are always bound to the correct special function register

In FinalAssemblyWriter.WriteConBlock, skip CON alias emission when the fixed-address alias name is already a real hardware register identifier (DIRA, OUTA, INA, etc.).

## `undefined` keyword for no-init locals

## Type improvements

- Distinct between `[*]T` and `*T` like in Zig. This gives better quality code without basically any drawbacks.

## constant file rendering is broken

uses the old rendering method which is weirdly formatted.

## Optimizer removes the required NOP

```
REP #1, #0
NOP
```

gets optimized by the NOP optimizer

this means we need a way to handle the "TRAP" as a single ASMIR instruction

## Add `VariableStorageClass.Flag` and wire asm output binding through it

Inline asm output bindings currently use `VariableStorageClass.Automatic`; introduce a dedicated `VariableStorageClass.Flag` and propagate it through binder/IR/codegen semantics.

## KNOWN SILICON BUGS

Intervening ALTx/AUGS/AUGD instructions between SETQ/SETQ2 and RDLONG/WRLONG/WMLONG-PTRx instructions will cancel
the special-case block-size PTRx deltas. The expected number of longs will transfer, but PTRx will only be modified according to
normal PTRx expression behavior:

  SETQ  #16-1    'ready to load 16 longs
  ALTD  start_reg  'alter start register (ALTD cancels block-size PTRx deltas)
  RDLONG 0,ptra++  'ptra will only be incremented by 4 (1 long), not 16*4 as anticipated!!!

Intervening ALTx instructions with an immediate #S operand, between AUGS and the AUGS' intended target instruction (which would
have an immediate #S operand), will use the AUGS value, but not cancel it. So, the intended AUGS target instruction will use and
cancel the AUGS value, as expected, but the intervening ALTx instruction will also use the AUGS value (if it has an immediate #S
operand). To avoid problems in these circumstances, use a register for the S operand of the ALTx instruction, and not an immediate #S
operand.

  AUGS  #$FFFFF123  'This AUGS is intended for the ADD instruction.
  ALTD  index,#base  'Look out! AUGS will affect #base, too. Use a register, instead.
  ADD  0-0,#$123  '#$123 will be augmented by the AUGS and cancel the AUGS.
