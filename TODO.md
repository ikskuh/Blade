# Planned Changes

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

## `rec fn` seems to miscompile

Validate that `rec fn` uses CALLB and stack spilling


## Assert that DIRA, OUTA, ... names are always bound to the correct special function register

In FinalAssemblyWriter.WriteConBlock, skip CON alias emission when the fixed-address alias name is already a real hardware register identifier (DIRA, OUTA, INA, etc.).

## `undefined` keyword for no-init locals

## `noinline fn` should be added to control code size

## Test strategy to find regressions and miscompilations

Right now, nothing is really tested except for the unit tests, which have bad coverage atm.

The idea is that we introduce a proper testing framework or test runner outside NUnit Tests which:

- Can run on a lot of files
- Can validate generated instruction sequences
- Can validate/match on generated code snippets (only raw code, always ignores comments and whitespace)
- Can be used to run hand-written tests against hand-optimized assembly code

## Consolidate all instruction metadata encodings inside the code base

