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

### Width-sensitive multiply lowering

When one operand is statically known to fit into 16 bits, the compiler should prefer
`MUL`/`MULS`-based lowering over `QMUL`.

Candidate triggers:

- Source type is `u16` / `i16`.
- Range analysis proves the upper 16 bits are zero or sign-extension.
- Bit-masking immediately before the multiply narrows the operand.

Useful forms:

- `16 x 16 -> 32`: a single `MUL` / `MULS`.
- `32 x 16 -> 32`: the `GETWORD` + `MUL` + `SHL` + `ADD` idiom from `idiom.html`.
- `x * 2^k`: strength-reduce to shifts/adds instead of using CORDIC.

This avoids CORDIC latency, reduces `GETQX` pressure, and leaves more scheduling freedom.

### Signed division sign normalization

Normalize signed division before lowering to P2 operations:

- If only the dividend is signed, lower to `ABS dividend`, `QDIV`, `GETQX`, `NEGC`.
- If both operands are signed, compute the sign XOR once, divide on absolute values,
  and negate the quotient only if needed.

This keeps signed division branch-free and maps directly to the `idiom.html` sequences.

### Multi-word arithmetic idioms

When Blade grows more 64-bit lowering, prefer direct P2 idioms instead of generic lowering:

- 64-bit absolute value via `ABS high`, conditional `NEG low`, conditional borrow fixup.
- Full-width signed multiply via `QMUL` plus sign correction only when the high half is required.

## Register Allocation

### Eliding unnecessary parameter inits

Similar to the `CALLPA` optimization:
When working bottom-to-top, the register allocator knows where the value needs to end up (e.g. in a parameter register),
thus we can initialize our SSA variable with that register.

This allows eliding an unnecessary copy from the argument value into the parameter register.

### Preserve CORDIC overlap windows

When values produced by `QMUL` / `QDIV` / `QFRAC` / `QROTATE` / `QVECTOR` are not needed
immediately, keep unrelated live ranges in registers so later scheduling passes can move
independent ALU work between the CORDIC submit and `GETQX` / `GETQY`.

This is less about picking a specific register and more about not destroying useful
instruction-movement opportunities with unnecessary reloads or copies.

## Code Layout / Scheduling

These optimizations depend on whole-block or whole-function placement rather than a single instruction pair.

### Place hot branch targets in Cog/LUT RAM

Taken branches into Cog/LUT are much cheaper than taken branches into Hub RAM.

That suggests:

- Keep tight loops and frequently re-entered blocks in Cog or LUT RAM.
- Allow mostly straight-line, cold, or error-handling paths to live in Hub RAM.
- Bias block ordering so the common path falls through and the cold path is branched to.

### Auto-form `REP` loops

The innermost counted loop should be lowered to `REP` whenever the body is branch-free
and call-free. This is especially attractive for tiny loops over fixed trip counts.

Related opportunities:

- Small memset/memcpy-style loops in Cog/LUT scratch.
- Tiny arithmetic kernels where loop overhead would otherwise dominate.
- `noirq {}` style single-iteration shielding that is already modeled as `REP count=1`.

### CORDIC latency hiding and pipelining

`GETQX` / `GETQY` are only expensive when the result is not ready yet. The backend should
therefore treat CORDIC submission and result collection as separate scheduling points:

- Move independent ALU work, address arithmetic, compares, or flag-free loads/stores
  between `Q*` and `GETQ*`.
- If multiple CORDIC ops are independent, issue them back-to-back and harvest results later.
- For counted loops, consider software-pipelined CORDIC forms where the next iteration's
  `Q*` is issued before the current iteration's `GETQ*`.

This is most valuable for multiply/divide-heavy kernels and fixed-point math.

### Prefer FIFO/block transfers for Hub streams

Sequential Hub reads/writes should not stay as plain `RDLONG` / `WRLONG` streams when a
branch-free region can use the FIFO:

- Lower long sequential reads to `RDFAST` + `RFLONG`.
- Lower long sequential writes to `WRFAST` + `WFLONG`.
- Consider `RFVAR` / `RFVARS` for varint-style decode loops.

The same principle applies to bulk transfers:

- Zeroing or filling a large Cog/LUT area can use `SETQ` / `SETQ2` block reads from
  `##$80000` instead of scalar stores.

### FIFO random-access prefetch

For random Hub accesses, there is an advanced option beyond `RDLONG` / `WRLONG`:
issue non-blocking `RDFAST` / `WRFAST`, do useful non-Hub work while the FIFO fills or flushes,
then consume via `RF*` / `WF*`.

This should only trigger when the compiler can prove:

- Enough independent non-Hub work exists to cover the setup latency.
- No conflicting Hub/FIFO use occurs in the overlap window.
- The access pattern is stable enough that FIFO setup cost amortizes.

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

### `QMUL` avoidance patterns

Once registers and immediates are known, some multiplies can still be rewritten out of CORDIC:

- Immediate power-of-two multiplies become shifts.
- Immediate small constants can become shift/add sequences if that beats `QMUL`.
- Proven `32 x 16` multiplies can become the `GETWORD`/`MUL`/`SHL`/`ADD` idiom.

This should be costed against code size as well as latency.

### Signed `QDIV` / `QMUL` idiom selection

If ASMIR still contains a generic signed divide or full-width signed multiply, match it to the
flag-based idioms from `idiom.html` instead of emitting extra compare/branch scaffolding.

### Nibble/byte permutation idioms

Recognize fixed shuffle patterns and lower them to dedicated P2 sequences.

Example candidate:

- Nibble-reverse of a 32-bit word using `SPLITB`, `REV`, `MOVBYTS`, `MERGEB`.

These are niche, but when they appear they beat generic mask/shift ladders.

### Fast zero-fill of contiguous scratch

A large run of `MOV reg, #0` or equivalent stores into contiguous Cog/LUT scratch can be
collapsed into a block clear sequence using `SETQ` / `SETQ2` plus `RDLONG` from `##$80000`.

### Identification of NOPs

All potential NOP operations can be removed from the ASMIR without any harm.

- `NOP`
- `MOV x, x`
- ... (to be done)

## Sources Ingested

- `https://p2docs.github.io/idiom.html`
- `https://p2docs.github.io/faster.html`
