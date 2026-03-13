# Planned Changes

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

