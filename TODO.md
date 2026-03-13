# Planned Changes

## Rework basic MIR/LIR assumptions

The MIR/LIR seems to use load/store semantics for cog variables which makes no sense at all as these can just be virtual
values like everything else in the SSA. They are just names we assign to SSA slot.

This also implies that "reg var" globals are just the same as any local variable: Just a register.

This also means that variables don't need initialization instructions, but can be preloaded in their storage allocation:

```spin2
    MOV var_global_multiplicand, ##1000 ' double-## is implicit AUGS
    …
var_global_multiplicand
    LONG 0
```

can be optimized to

```spin2
    ' load is fully elided as statically initialized.
    …
var_global_multiplicand
    LONG 1000
```

## Rework register allocation

Right now, the register allocator works way too early in the code phase, generating bad and too much registers.

A good approach would be:

- MOV elision in ASMIR reduces number of unnecessary copies
- Register allocation is a new, separate pass between ASMIR and ASM emission
  - This pass is a whole-program optimization
  - Until this phase, all registers are *virtual* except for the *concrete* ones defined in the
    spec. These basically just include PTRA and PA/PB when emitted from the compiler, as well as
    all reserved special-purpose registers when referenced by the user.
  - The rough idea is:
    - Starting from the leaf functions, build a set of used registers for each remaining function in the code base
    - This allows us to know which function will potentially clobber which registers.
    - We can then use the same registers before/after these calls, but values passed over such a call must use different registers
  - The register allocator works like this:
    1. For each function, determine the minimum required set of registers without introducing conflicts.
    2. Based on a call graph analysis of the ASMIR, we can now determine which function blocks are leaves.
    3. The leaves can share all parameter and temporary registers, as they will never conflict.
    4. When the leaves are resolved, we can then use the same registers as long as they never pass a function call for local temporaries
    5. All temporary values and parameters that cross a function call must not use the same register slot as the called function (and its transients)

## `reg var` must be only `var` for locals

This change removes the restriction that each local variable must be allocated into its own register.

This is important as register space is expensive after all, and we don't have a stack to spill.

## `CALLPA` bindings are important

Leaf functions through CALL/PA should prefer the "PA" parameter for their first parameter value.

This plays together nicely with the register allocator running backwards.

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

## `asm volatile { }` blocks

Right now, all blocks are inherently volatile.

If we can make this an optional keyword, we can change the semantics:

- `asm { … }` is hand-written assembly code, but *may* be optimized by an ASMIR or ASM optimizer.
- `asm volatile { … }` must be taken verbatim by the assembler.

This would allow optimizing user-written assembly code and elide unnecessary MOV or copies emitted by the compiler.
