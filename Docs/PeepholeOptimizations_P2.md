# P2 Peephole Optimization Patterns for Blade

Comprehensive catalog of peephole optimization patterns for the Parallax Propeller 2
instruction set, suitable for implementation in the Blade compiler's ASMIR pass.

Sources: P2 instruction set v35, flexspin `optimize_ir.c`, p2docs.github.io idiom.html /
faster.html, Parallax forums.

---

## 1. Condition Code Fusion

These patterns eliminate branch instructions by folding conditions into adjacent instructions.

### 1.1 Conditional Move (already in OptimizationPool)

```
    IF_Z  JMP   #after
          MOV   _r2, _r3
after
```
→
```
    IF_NZ MOV   _r2, _r3
```

**Generalization**: Any conditional jump that skips a single unconditional instruction
can fuse the *inverted* condition into the skipped instruction and elide the jump.
This applies to all ALU ops, not just MOV. The only constraint is that the
skipped instruction is unconditional (`IF_ALWAYS`) and the jump target label has
no other incoming edges (i.e., nothing else jumps to `after`).

### 1.2 Multi-Instruction Predication

When an `IF_xx JMP` skips N unconditional instructions and the branch target has no
other predecessors, all N instructions can be predicated with the inverted condition.
Cost model: predicated execution is *constant* at 2 cycles per instruction regardless
of whether the condition is true or false. Branching costs 4 cycles (cog/LUT target)
or 13+ cycles (hub target) when taken, 2 cycles when not taken. So for N instructions
inside an if-body that executes roughly 50% of the time:

- Predicated: 2N cycles always.
- Branched: ~(0.5 × 2 + 0.5 × (4 + 2N)) = 1 + 2 + N = N + 3 cycles (cog target).

For N ≤ ~3 instructions with a cog-resident target, predication is competitive. For
hub-resident targets (13+ cycle branch penalty), predication wins for much larger N.

### 1.3 If/Else Predication Pair

```
    CMP   x, y     WCZ
    IF_Z  JMP   #else_branch
    MOV   a, b              ' then body
    JMP   #endif
else_branch
    MOV   a, c              ' else body
endif
```
→
```
    CMP   x, y     WCZ
    IF_Z  MOV   a, c
    IF_NZ MOV   a, b
```

Total: 3 instructions, constant 6 cycles. The branching version is 4 or 5 instructions
with variable timing. This is the "constant 12 cycles" pattern from the Blade design doc
(at 2 cycles per instruction × 6 instructions for a 2-statement then/else).

### 1.4 MUXC/MUXNC/MUXZ/MUXNZ Fusion (from flexspin)

```
    IF_C  OR    a, b
    IF_NC ANDN  a, b
```
→
```
          MUXC  a, b
```

Likewise for the Z-flag variants. The pattern is: setting bits of `a` where `b` is 1
according to flag state is exactly what `MUX*` does.

Also works in the reverse form:
```
    IF_NC ANDN  a, b
    IF_C  OR    a, b
```
→ `MUXC a, b`

And the negated variants (`IF_NC OR` + `IF_C ANDN` → `MUXNC`).

### 1.5 WRC/WRNC/WRZ/WRNZ for Bool Materialization

When materializing a flag as a 0/1 value:

```
    IF_C  MOV   result, #1
    IF_NC MOV   result, #0
```
→
```
          WRC   result
```

Likewise `WRNC`, `WRZ`, `WRNZ` for the other flag combinations.

### 1.6 NEGC/NEGNC/NEGZ/NEGNZ for Conditional Negate

```
    CMP   x, #0   WC
    IF_C  NEG   y
```
→
```
    CMP   x, #0   WC
          NEGC  y
```

This saves the branch or conditional prefix *and* the pattern is already what the
hardware provides as a single instruction.

### 1.7 SUMC/SUMNC/SUMZ/SUMNZ for Conditional Add/Sub

When code conditionally adds or subtracts the same operand:

```
    IF_C  SUB   d, s
    IF_NC ADD   d, s
```
→
```
          SUMC  d, s
```

`SUMC` computes `D = D - S` if C else `D = D + S`. This is especially useful for
CORDIC angle accumulation, PID controllers, and two's-complement absolute-value
patterns.

---

## 2. `_RET_` Condition Code Patterns

### 2.1 `_RET_` Elision (already in OptimizationPool)

```
          ANYOP  …
          RET
```
→
```
    _RET_ ANYOP  …
```

Preconditions:
- `ANYOP` is unconditionally executed (`IF_ALWAYS`).
- `RET` is unconditionally executed.
- `RET` is not a jump target.
- Neither instruction uses WCZ effects that conflict with `_RET_`'s flag semantics.

**This saves 2 cycles** (a RET is a branch costing 4 cycles; `_RET_` piggybacks
on the final instruction at 2 cycles).

### 2.2 `_RET_` with Flag-Returning Instructions

When the function returns flags via C/Z (Blade's 34-bit return channel), the
instruction before `RET WCZ` can be folded:

```
          CMP   x, y     WCZ
          RET             WCZ
```

Here we *cannot* simply do `_RET_ CMP x, y WCZ` because `_RET_` restores
the caller's C/Z. The `RET WCZ` form preserves the subroutine's flags for the
caller, so `_RET_` is not applicable when the subroutine needs to return flag
state. This is a non-optimization that must be guarded against.

### 2.3 `_RET_` on Hub Read/Write

A common pattern at function epilogues is to store a result and return:

```
          WRLONG  result, ptr
          RET
```
→
```
    _RET_ WRLONG  result, ptr
```

This is valid because WRLONG has no result register write that would conflict.

---

## 3. Redundant Instruction Elimination

### 3.1 NOP Identification (already in OptimizationPool)

The following are semantic NOPs and can be removed:

- `NOP`
- `MOV   x, x` (without WC/WZ)
- `OR    x, #0` (without WC/WZ)
- `AND   x, x` (without WC/WZ — careful: with WC it yields parity)
- `XOR   x, #0` (without WC/WZ)
- `ADD   x, #0` (without WC/WZ — with WC it produces carry=0)
- `SUB   x, #0` (without WC/WZ)
- `SHL   x, #0` (without WC/WZ)
- `SHR   x, #0` (without WC/WZ)
- `SAR   x, #0` (without WC/WZ)
- `ROL   x, #0` / `ROR x, #0` (without WC/WZ)
- `RCL   x, #0` / `RCR x, #0` (without WC/WZ — *only* if C not read after)
- `ZEROX x, #31` (no-op: zero-extend above bit 31 = no change)
- `SIGNX x, #31` (no-op: sign-extend from bit 31 = no change)
- `CMP   x, y` without WC/WZ/WCZ (literally does nothing)
- `TEST  x, y` without WC/WZ/WCZ (literally does nothing)
- `MODCZ ?, ?` without WC/WZ/WCZ (literally does nothing)

### 3.2 Redundant Move After Operation (from flexspin)

```
    MOV   a, b
    MOV   b, a
```
→ Delete second `MOV`.

More generally:
```
    ADD   a, b
    MOV   b, a
```
→ If `a` is dead after, rewrite to `ADD b, a` (swap dest) and delete the MOV.
This applies to any commutative op (ADD, AND, OR, XOR, MUL, etc.).

### 3.3 Dead Write Elimination

If a register is written but never read before being overwritten, the first write
can be eliminated. This requires liveness analysis but is a high-value peephole.

### 3.4 Redundant ZEROX / SIGNX After Typed Load

```
    RDBYTE  d, ptr          ' loads 8-bit, zero-extended to 32 bits
    ZEROX   d, #7           ' redundant: already zero-extended
```
→ Delete the ZEROX.

Similarly:
- `RDWORD d, ptr` followed by `ZEROX d, #15` → delete ZEROX.
- `GETBYTE d, s, #N` followed by `ZEROX d, #7` → delete ZEROX.
- `GETWORD d, s, #N` followed by `ZEROX d, #15` → delete ZEROX.
- `GETNIB d, s, #N` followed by `ZEROX d, #3` → delete ZEROX.

### 3.5 Redundant AND After Narrow Load (from flexspin)

```
    RDBYTE  d, ptr
    AND     d, #$FF
```
→ Delete the AND (RDBYTE already zero-extends).

More generally, `AND x, #mask` is redundant after any instruction that guarantees
the upper bits are zero and the mask covers all the meaningful bits.

### 3.6 Chained Redundant AND (from flexspin)

```
    AND   x, #$0F
    MOV   y, x
    AND   y, #$FF
```
→ Delete the final AND (the value is already ≤ $0F, so AND with $FF is no-op).

---

## 4. Instruction Strength Reduction

### 4.1 MOV #1 + SHL → DECOD (from flexspin, P2 only)

```
    MOV   a, #1
    SHL   a, N
```
→
```
    DECOD a, N
```

`DECOD D, S` computes `D = 1 << S[4:0]`.

### 4.2 XOR with Single-Bit Immediate → BITNOT (from flexspin, P2 only)

```
    XOR   a, #(1 << K)
```
→
```
    BITNOT a, #K
```

This is only valid when the immediate is a single-bit constant. Saves AUGS for
large immediates.

### 4.3 OR with Single-Bit Immediate → BITH

```
    OR    a, #(1 << K)
```
→
```
    BITH  a, #K
```

### 4.4 ANDN with Single-Bit Immediate → BITL

```
    ANDN  a, #(1 << K)       ' clear bit K
```
→
```
    BITL  a, #K
```

### 4.5 OR / ANDN / XOR with Contiguous Bit Masks → BIT* with Range

The BIT* instructions support a *range* of bits via the S field's upper bits
(or a prior SETQ). When the mask is a contiguous run of 1-bits:

```
    OR    a, ##$000000F0      ' set bits 7:4
```
→
```
    BITH  a, #%00011_00100    ' set bits [4+3 : 4] = [7:4]
```

where the S operand encodes `{length-1, start_bit}` as `{S[9:5], S[4:0]}`.
The compiler should detect contiguous bit masks and use the ranged form.

### 4.6 BMASK for Mask Generation

```
    MOV   mask, ##$0000FFFF
```
→
```
    BMASK mask, #15           ' mask = (2 << 15) - 1 = $FFFF
```

Any `(1 << (N+1)) - 1` pattern can use BMASK and avoid AUGS/AUGD.

### 4.7 Multiply by Power of Two → SHL

```
    QMUL  x, #(1 << K)
    GETQX result
```
→
```
    MOV   result, x
    SHL   result, #K
```

Or even:
```
    MUL   x, #(1 << K)
```
→ `SHL x, #K` (when K ≤ 15, since MUL is 16×16).

### 4.8 Small Constant Multiply → Shift/Add

For known small constants, the following are faster than QMUL:

- `× 3`: `MOV t, x; SHL x, #1; ADD x, t`  (3 instrs, 6 cycles)
- `× 5`: `MOV t, x; SHL x, #2; ADD x, t`
- `× 6`: `MOV t, x; SHL t, #1; ADD t, x; SHL t, #1` — or `SHL x, #1; MOV t, x; SHL x, #1; ADD x, t`
- `× 7`: `MOV t, x; SHL x, #3; SUB x, t`
- `× 9`: `MOV t, x; SHL x, #3; ADD x, t`
- `× 10`: `MOV t, x; SHL x, #2; ADD x, t; SHL x, #1`
- `× 2^k ± 1`: Always reducible to shift + add/sub.

The break-even vs QMUL depends on whether the CORDIC latency can be hidden.
Isolated multiplies: the shift/add form wins for constants ≤ ~10. In pipelined
CORDIC chains, QMUL might still be preferable.

### 4.9 SCA/SCAS as Fused Multiply-Shift

`SCA D, S` computes `(D[15:0] × S[15:0]) >> 16` and feeds it to the *next*
instruction's S operand. `SCAS` does the same signed, with a `>> 14` and a
fixed-point scale where $4000 = 1.0.

These can replace the idiom:
```
    MUL   result, scale
    SHR   result, #16
    ADD   accum, result
```
→
```
    SCA   value, scale
    ADD   accum, 0             ' S field is overridden by SCA result
```

Note: SCA/SCAS shield the next instruction from interrupts.

---

## 5. Compare/Branch Fusion

### 5.1 CMP + IF_Z JMP → TJZ/TJNZ

```
    CMP   x, #0    WZ
    IF_Z  JMP   #target
```
→
```
    TJZ   x, #target
```

And `IF_NZ JMP → TJNZ`. This saves one instruction.

### 5.2 CMP + IF_C JMP → TJS/TJNS (Sign Test)

```
    CMPS  x, #0    WC
    IF_C  JMP   #target
```
→ `TJS x, #target` (jump if signed/negative).

`IF_NC JMP → TJNS` (jump if non-negative).

### 5.3 SUB + IF_Z JMP → DJZ/DJNZ

```
    SUB   counter, #1
    CMP   counter, #0    WZ
    IF_NZ JMP   #loop_top
```
→
```
    DJNZ  counter, #loop_top
```

This collapses 3 instructions into 1. Also handles `DJZ` for the zero case.
Similarly, `ADD counter, #1` + test → `IJZ/IJNZ`.

### 5.4 TEST for Full-Word Check → TJF/TJNF

```
    CMP   x, ##$FFFFFFFF   WZ
    IF_Z  JMP   #target
```
→
```
    TJF   x, #target               ' jump if D == $FFFF_FFFF
```

### 5.5 MOV + TEST → Direct Flags from MOV

`MOV D, S` with WZ sets Z if result is zero. So:
```
    MOV   x, y
    CMP   x, #0    WZ
```
→
```
    MOV   x, y     WZ
```

Delete the CMP. This applies broadly: any instruction that sets Z = (result==0)
makes a subsequent `CMP x, #0 WZ` redundant. Nearly all ALU ops support WZ.

### 5.6 Absorbed Flag Test from Shift (from flexspin)

```
    TEST  tmp, #1   WZ
    SHR   tmp, #1
```
→
```
    SHR   tmp, #1   WC
```

Then replace `IF_Z/IF_NZ` references with `IF_NC/IF_C`. The last bit shifted
out goes into C, which is exactly the bit that was tested.

Similarly:
```
    TEST  tmp, ##$80000000   WZ
    SHL   tmp, #1
```
→ `SHL tmp, #1 WC` (MSB goes into C).

---

## 6. Hub Memory and FIFO Patterns

### 6.1 SETQ + RDLONG/WRLONG for Block Transfer

Multiple consecutive `RDLONG`/`WRLONG` to/from sequential cog registers:

```
    RDLONG  reg0, ptr
    ADD     ptr, #4
    RDLONG  reg1, ptr
    ADD     ptr, #4
    RDLONG  reg2, ptr
```
→
```
    SETQ    #2              ' transfer 3 longs (count = N-1)
    RDLONG  reg0, ptr
```

Preconditions: destination registers must be contiguous in cog address space.
This is 1 cycle per long vs ~9-17 cycles per RDLONG.

The same works with `SETQ2` for LUT RAM.

### 6.2 Block Zero via $80000 (already in OptimizationPool)

```
    MOV   reg0, #0
    MOV   reg1, #0
    ...
    MOV   regN, #0
```
→
```
    SETQ  #N
    RDLONG reg0, ##$80000
```

Reads from hub address $80000 yield all zeros. This clears N+1 contiguous
cog registers in ~N+1 cycles.

### 6.3 Sequential Hub Access → FIFO

When the compiler detects a loop that reads/writes sequential hub addresses:

```
loop:
    RDLONG  val, ptr
    ADD     ptr, #4
    ...process val...
    DJNZ    count, #loop
```
→
```
    RDFAST  #0, ptr           ' start FIFO read from ptr
    ...
loop:
    RFLONG  val
    ...process val...
    DJNZ    count, #loop
```

RFLONG is 2 cycles vs RDLONG's 9-17 cycles. The constraint is that the FIFO
cannot be active during hub-exec branching or other FIFO operations.

---

## 7. CORDIC Patterns

### 7.1 16×16 Multiply Preference over QMUL

When both operands are statically known to fit in 16 bits (from type info or
range analysis), prefer `MUL`/`MULS` (2 cycles, instant result) over
`QMUL` + `GETQX` (2 + up to 58 cycles).

### 7.2 32×16 Multiply Idiom (from p2docs idiom.html)

When one operand is 32-bit and the other 16-bit:

```
    GETWORD  tmp, x, #1       ' x[31:16]
    MUL      tmp, y            ' tmp = x_hi * y
    SHL      tmp, #16          ' shift to upper position
    MUL      x, y              ' x = x_lo * y (x[15:0] * y)
    ADD      x, tmp            ' combine
```

5 instructions, 10 cycles, no CORDIC stall. Versus QMUL + GETQX at
2 + (up to 58) cycles.

### 7.3 Signed QDIV Single-Signed (from p2docs idiom.html)

```
    ABS   x       WC
    QDIV  x, y
    GETQX res
    NEGC  res
```

### 7.4 Signed QDIV Both-Signed (from p2docs idiom.html)

```
    ABS   x       WC
    MODZ  _C      WZ           ' Z = original C
    ABS   y       WC           ' C = sign of y
    QDIV  x, y
    GETQX res
    IF_C_NE_Z NEG res          ' negate if signs differed
```

### 7.5 CORDIC Latency Interleaving (already in OptimizationPool, scheduling-level)

Between `Q*` and `GETQ*`, schedule independent ALU work, address arithmetic,
or flag-free loads:

```
    QMUL  a, b
    MOV   x, y                 ' independent work: "free"
    ADD   ptr, #4              ' independent work: "free"
    GETQX c                    ' less blocking time
```

---

## 8. Instruction-Specific Rewrites

### 8.1 CMPSUB for Modular Reduction

```
    CMP   d, s      WC
    IF_NC SUB   d, s
```
→
```
    CMPSUB d, s
```

`CMPSUB` atomically does: if D ≥ S then D -= S, C = 1; else D unchanged, C = 0.

### 8.2 FGE/FLE/FGES/FLES for Clamping

```
    CMP   d, #max    WC
    IF_NC MOV   d, #max
```
→
```
    FLE   d, #max                  ' D = min(D, max)
```

```
    CMP   d, #min    WC
    IF_C  MOV   d, #min
```
→
```
    FGE   d, #min                  ' D = max(D, min)
```

Signed variants: `FGES` / `FLES`.

### 8.3 INCMOD / DECMOD for Wrap-Around Counters

```
    ADD   counter, #1
    CMP   counter, #limit    WZ
    IF_Z  MOV   counter, #0
```
→
```
    INCMOD counter, #limit
```

`INCMOD`: if D == S then D = 0, C = 1; else D = D + 1, C = 0.

```
    SUB   counter, #1       WZ
    IF_Z  MOV   counter, #limit
```
→
```
    DECMOD counter, #limit
```

`DECMOD`: if D == 0 then D = S, C = 1; else D = D - 1, C = 0.

### 8.4 TESTB / TESTBN for Single-Bit Test

```
    AND   x, ##(1 << K)   WZ
```
→
```
    TESTB x, #K            WZ       ' Z = !x[K]  (inverted!)
```

Wait — `TESTB D, S WZ` writes `Z = D[S[4:0]]`, so Z=1 if the bit is set.
The `AND` version sets Z if the *result* is zero, meaning the bit was *clear*.
So the mapping requires inverting the condition. Alternatively:

```
    TEST  x, ##(1 << K)   WZ       ' Z = ((x & mask) == 0), i.e. bit K is clear
```
→
```
    TESTBN x, #K           WZ       ' Z = !D[K], so Z=1 when bit is clear
```

Or with `TESTB x, #K WC` to get `C = x[K]`, then branch on `IF_C/IF_NC`.

The key win: `TESTB`/`TESTBN` avoid the AUGS needed for `##(1 << K)` when K > 8.

### 8.5 TESTB with ANDC/ORC/XORC for Multi-Bit Accumulation

When testing multiple bits and combining results:

```
    TESTB  flags, #3    WC        ' C = bit 3
    TESTB  flags, #7    ANDC      ' C = C AND bit 7
    IF_C   ...
```

This replaces multi-instruction AND/shift/compare sequences with two instructions.

### 8.6 ENCOD for Log2 / Highest Bit

```
    ' find position of highest set bit
    MOV   pos, #31
loop:
    SHL   x, #1         WC
    IF_NC DJNZ pos, #loop
```
→
```
    ENCOD pos, x                   ' pos = position of top '1' bit
```

### 8.7 ONES for Population Count

Any loop or table-based popcount → `ONES D, S`.

### 8.8 REV for Bit Reversal

Any loop-based bit reversal → `REV D`.

### 8.9 ABS for Absolute Value

```
    CMPS  x, #0    WC
    IF_C  NEG   x
```
→
```
    ABS   x
```

### 8.10 SUBR for Reverse Subtraction

```
    MOV   tmp, s
    SUB   tmp, d
    MOV   d, tmp
```
→
```
    SUBR  d, s                     ' D = S - D
```

### 8.11 ZEROX for Zero Extension

```
    AND   x, ##$000000FF
```
→
```
    ZEROX x, #7                    ' zero-extend above bit 7
```

```
    AND   x, ##$0000FFFF
```
→
```
    ZEROX x, #15
```

Any `AND x, ##((1 << (N+1)) - 1)` → `ZEROX x, #N`. Avoids AUGS.

### 8.12 SIGNX for Sign Extension

```
    SHL   x, #24
    SAR   x, #24
```
→
```
    SIGNX x, #7                    ' sign-extend from bit 7
```

Any shift-left-then-arithmetic-shift-right pattern for sign extension.
Also replaces the common `SHL #(32-width); SAR #(32-width)` idiom.

### 8.13 GETBYTE / SETBYTE / GETWORD / SETWORD for Field Access

```
    SHR   x, #8
    AND   x, #$FF
```
→
```
    GETBYTE x, x, #1              ' get byte 1 (bits [15:8])
```

```
    SHR   x, #16
    AND   x, #$FFFF
```
→
```
    GETWORD x, x, #1              ' get word 1 (bits [31:16])
```

```
    AND   tmp, #$FF
    SHL   tmp, #16
    ANDN  x, ##$00FF0000
    OR    x, tmp
```
→
```
    SETBYTE x, tmp, #2            ' set byte 2
```

### 8.14 MOVBYTS for Byte Shuffle

Any fixed byte permutation of a 32-bit register:
```
    MOVBYTS x, #%%3210            ' identity (no-op)
    MOVBYTS x, #%%0123            ' reverse byte order (endian swap)
    MOVBYTS x, #%%1032            ' swap pairs
    MOVBYTS x, #%%0000            ' broadcast byte 0 to all positions
```

Replaces multi-instruction shift/mask/or byte-swap sequences.

### 8.15 SPLITB/MERGEB + REV + MOVBYTS for Nibble Reverse (from p2docs)

```
    SPLITB   x
    REV      x
    MOVBYTS  x, #%%0123
    MERGEB   x
```

For nibble-reverse of a 32-bit word. Replaces ~8+ instruction mask/shift ladder.

---

## 9. Immediate Optimization

### 9.1 AUGS/AUGD Avoidance

P2 instructions have a 9-bit S field. Constants > 511 require AUGS (which shields
the next instruction from interrupts and costs 2 cycles). The compiler should prefer:

- `BMASK` over `MOV ##mask` for `(2 << N) - 1` masks.
- `DECOD` over `MOV ##(1 << N)` for single-bit values.
- `ZEROX` over `AND ##mask` for zero-extension.
- `BIT*` instructions over `OR/ANDN/XOR ##mask` for bit field manipulation.
- `NEG d, #(-val & 511)` instead of `MOV d, ##large_negative` when the positive
  form fits in 9 bits.
- `SUB d, #small` instead of `ADD d, ##(0xFFFFFFFF - small + 1)`.

### 9.2 Constant Folding Through AUGS

When AUGS is needed, fold constant arithmetic at compile time:

```
    MOV   x, ##CONST_A
    ADD   x, ##CONST_B
```
→
```
    MOV   x, ##(CONST_A + CONST_B)
```

### 9.3 MOV #0 → Special Cases

`MOV x, #0` is already efficient (1 instruction, no AUGS), but for *conditional*
zeroing, consider:
- `IF_Z MOV x, #0` is fine.
- `BITL x, #0` sets bit 0 to 0 — not the same thing.
- For zeroing, `MOV` or `XOR x, x` (but XOR with WC gives parity, not what you want).

---

## 10. Flag Manipulation Patterns

### 10.1 MODCZ for Flag Composition

When both C and Z need to be set from complex conditions:

```
    ' Set C = (a > b), Z = (c == d)
    CMP   a, b     WC        ' C = borrow of (a - b)
    CMP   c, d     WZ        ' Z = (c == d) — but clobbers C!
```

Use MODCZ to compose:
```
    CMP   a, b     WC
    CMP   c, d     WZ        ' only write Z, preserves C
```
Actually, `CMP c, d WZ` already only writes Z if you don't add WC. But if the
flag-setting instruction always writes both, use MODCZ to save/restore:

```
    MODCZ _C, 0     WC       ' save C to... C (no-op, but pattern for composition)
```

The real use of MODCZ is setting flags to known states:
```
    MODCZ _SET, _CLR   WCZ   ' C = 1, Z = 0
    MODCZ _CLR, _SET   WCZ   ' C = 0, Z = 1
```

---

## 11. Addressing Mode Patterns

### 11.1 PTRx Auto-Increment/Decrement

When the compiler generates explicit pointer arithmetic around loads/stores:

```
    RDLONG  val, ptr
    ADD     ptr, #4
```
→
```
    RDLONG  val, ptr++           ' PTRA++ or PTRB++
```

The P2 supports rich PTR expressions: `PTRx[offset]`, `PTRx++`, `++PTRx`,
`PTRx--`, `--PTRx`, and scaled index forms. The compiler should match
load/add and store/add pairs to PTR auto-increment forms.

### 11.2 PUSHA/PUSHB and POPA/POPB

```
    WRLONG  val, ptra
    SUB     ptra, #4
```
→ Depends on direction. The actual PUSHA/PUSHB use pre-decrement:
```
    PUSHA   val                   ' hub[--PTRA] = val
    PUSHB   val                   ' hub[--PTRB] = val
```
And POPA/POPB use post-increment:
```
    POPA    val                   ' val = hub[PTRA++]   (wrong — actually --PTRA)
```

(Check exact semantics: POPA is `val = hub[--PTRA]`, POPB is `val = hub[--PTRB]`.)

---

## 12. Cross-Instruction Patterns (from flexspin)

### 12.1 MOV + AND → TEST (from flexspin)

```
    MOV   tmp, x
    AND   tmp, y     WZ
```
→ If `tmp` is dead after:
```
    TEST  x, y       WZ
```

### 12.2 ADD + MOV + SUB → MOV + ADD (from flexspin)

```
    ADD   objptr, x
    MOV   tmp, objptr
    SUB   objptr, x
```
→
```
    MOV   tmp, objptr
    ADD   tmp, x
```

This pattern appears when computing a temporary offset from a base pointer.

### 12.3 Absorbed ZEROX/SIGNX into Loads (from flexspin)

When an extension operation follows a load and the load already produces the
correctly-sized value, the extension is redundant (see §3.4).

Flexspin also optimizes away sign extensions when the subsequent operation
doesn't depend on the upper bits.

---

## 13. Patterns NOT in OptimizationPool (New Discoveries)

### 13.1 CMPM for MSB-of-Difference

`CMPM D, S` sets C = MSB of (D - S), which is the *sign bit* of the difference.
This is useful for signed comparison without a separate CMPS:

```
    SUBS  tmp, d, s              ' tmp = d - s
    TESTB tmp, #31   WC          ' C = sign bit
```
→
```
    CMPM  d, s       WC          ' C = MSB of (d - s)
```

### 13.2 _RET_ PUSH for Tail-Call Trampolines

From the Parallax forums: `_RET_ PUSH #addr` simultaneously returns from the
current function and pushes a new return address, enabling lightweight coroutine
chaining or tail-call-like patterns without extra instructions.

### 13.3 SKIP/SKIPF for Multi-Instruction Predication

For longer if/else bodies (> 3 instructions), the SKIP instruction can be more
efficient than individual conditional prefixes:

```
    SKIP  #%pattern            ' bit pattern: 1 = skip, 0 = execute
    instr1                     ' executed if bit 0 = 0
    instr2                     ' executed if bit 1 = 0
    ...
```

SKIP affects the next 1-32 instructions. This avoids the code duplication of
predication while still avoiding branches. The compiler would need to compute
the skip bitmask at compile time.

### 13.4 ALTI for Computed Instruction Execution

`ALTI D` executes the value in register D as if it were an instruction. This
enables computed dispatch patterns (jump tables, polymorphic calls) without
branches:

```
    ALTS  index, #table
    MOV   tmp, 0               ' loads table[index]
    ALTI  tmp                   ' executes table[index] as instruction
    NOP                         ' placeholder consumed by ALTI
```

### 13.5 CRCBIT / CRCNIB for CRC

Any CRC computation loop should be lowered to `CRCBIT` (1 bit per instruction)
or `CRCNIB` (4 bits per instruction, shields next instruction). For full-byte
CRC: `SETQ #data; REP #1, #8; CRCNIB poly`.

### 13.6 MUXNITS / MUXNIBS for Masked Update

`MUXNITS D, S` copies each non-zero bit-pair from S into D; `MUXNIBS` does
the same for non-zero nibbles. These replace complex mask/shift/or patterns
for partial register updates at 2-bit or 4-bit granularity.

### 13.7 MUXQ for Arbitrary Bit-Mask Move

After `SETQ mask`:
```
    SETQ  mask
    MUXQ  d, s                 ' D = (D & ~mask) | (S & mask)
```

Replaces the 3-instruction `AND tmp, mask; ANDN d, mask; OR d, tmp` sequence.

### 13.8 RCL/RCR for Multi-Word Shift

When shifting a multi-word value:
```
    SHL   lo, #1     WC       ' C = bit shifted out of lo
    RCL   hi, #1              ' shift C into hi
```

This is the correct 64-bit left shift. The compiler should recognize 64-bit
shift operations and emit this 2-instruction pair.

### 13.9 ADDX/SUBX/ADDSX/SUBSX for Multi-Word Arithmetic

64-bit add:
```
    ADD   lo_a, lo_b     WC   ' add low words, C = carry
    ADDX  hi_a, hi_b          ' add high words + carry
```

64-bit subtract:
```
    SUB   lo_a, lo_b     WC   ' sub low words, C = borrow
    SUBX  hi_a, hi_b          ' sub high words + borrow
```

### 13.10 64-Bit Absolute Value (from p2docs idiom.html)

```
    ABS        high      WC
    IF_C  NEG  low       WZ
    IF_C_AND_NZ SUB high, #1
```

The third instruction handles the borrow when negating the low word produces
a non-zero result.

---

## 14. Summary: Priority Ranking for Implementation

**Tier 1 — High frequency, trivial to implement:**
1. `_RET_` elision (§2.1)
2. NOP identification (§3.1)
3. Conditional move fusion (§1.1)
4. MUXC/MUXNC fusion (§1.4)
5. Redundant MOV elimination (§3.2)
6. CMP #0 → flag absorption (§5.5)

**Tier 2 — High value, moderate complexity:**
7. DJZ/DJNZ/TJZ/TJNZ formation (§5.1–5.3)
8. ZEROX/SIGNX for extension (§8.11, §8.12)
9. GETBYTE/GETWORD for field access (§8.13)
10. FGE/FLE/FGES/FLES for clamping (§8.2)
11. CMPSUB for modular reduction (§8.1)
12. INCMOD/DECMOD for wrap counters (§8.3)
13. MOV+AND → TEST (§12.1)
14. AUGS avoidance via BMASK/DECOD/BIT* (§9.1)

**Tier 3 — Valuable, requires range analysis or scheduling:**
15. 16×16 MUL preference (§7.1)
16. 32×16 multiply idiom (§7.2)
17. Small constant multiply strength reduction (§4.8)
18. Shift absorbed flag test (§5.6)
19. SETQ block transfers (§6.1–6.2)
20. PTRx auto-increment matching (§11.1)
21. Multi-word arithmetic idioms (§13.8–13.10)

**Tier 4 — Advanced / niche:**
22. FIFO lowering for sequential access (§6.3)
23. CORDIC pipelining (§7.5)
24. SKIP for multi-instruction predication (§13.3)
25. MUXQ for arbitrary masked moves (§13.7)
26. Nibble reverse idiom (§8.15)
27. CRCNIB lowering (§13.5)
