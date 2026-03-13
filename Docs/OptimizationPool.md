# Propeller 2 / Blade Optimization Pool

This optimization pool contains a collection of possible optimizations
that can be implemented in Blade.

The optimizations are on different levels of abstraction and potentially
play together nicely to collapse long code sequences into shorter or faster
ones.

## LIR

These optimizations are performed on the LowLevel IR.

### `CALLPA` to `CALL` elision

A call to `CALLPA` can be changed into a `CALL` when `PA` is already properly prefilled. This means
the LIR can already refer to PA for the variable that is filled as the parameter value later on.

## Register Allocation

### Eliding unnecessary parameter inits

Similar to the `CALLPA` optimization:
When working bottom-to-top, the register allocator knows where the value needs to end up (e.g. in a parameter register),
thus we can initialize our SSA variable with that register.

This allows eliding an unnecessary copy from the argument value into the parameter register.

## ASMIR

These optimizations can be applied through inspecting tiny pieces of instruction sequences.

### Exchanging bit masking with `BITL`, `BITH`, `BITNOT`, ...

Typical bit manipulation patterns look like this:

```
lhs1 |=  0x10;
lhs2 &= ~0x30;
lhs3 ^=  0xF0;
```

These can all be well optimized with the `BIT?` instructions to reduce the size of the immediate operand:

- `BITH`: Sets a range of N consecutive bits to 1. Typical source pattern: `a |= 0x10`.
- `BITL`: Sets a range of N consecutive bits to 0. Typical source pattern: `a $= ~0x10`.
- `BITNOT`: Inverts a range of N consecutive bits. Typical source pattern: `a ^= 0x10`.
- `BITC`: Sets a range of N consecutive bits to C flag.
- `BITNC`: Sets a range of N consecutive bits to inverse of C flag.

### Quick return by `_RET_`

That optimization is fairly simple:

If an instruction is unconditionally executed and the instruction after it is a RET, the `RET` can be removed
and the condition can be set to `_RET_`. This is only possible if the `RET` is not a direct jump target (so no
instruction jumps to it).

```
    IF_ALWAYS ANYOP …
    IF_ALWAYS RET
```

can be optimized to

```
    _RET_ ANYOP …
```

### Conditional Move

general rule: If a conditional jump skips a single unconditional instruction, it can fuse the condition
into the unconditional instruction and elide the jump:

```
   IF_Z JMP #$top_inl_after_0
        MOV _r2, _r3
$top_inl_after_0
```

can be optimized into

```
   IF_Z MOV _r2, _r3
```

### Identification of NOPs

All potential NOP operations can be removed from the ASMIR without any harm.

- `NOP`
- `MOV x, x`
- ... (to be done)


> TODO: Ingest https://p2docs.github.io/idiom.html
> TODO: Ingest https://p2docs.github.io/faster.html
