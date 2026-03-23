# Planned Changes

## `CALLPA` bindings are important

Leaf functions through CALL/PA should prefer the "PA" parameter for their first parameter value.

This plays together nicely with the register allocator running backwards.

## Implementation of array loads/stores

Right now only the high-level part is implemented, but lowering the array access is not.

## constant file rendering is broken

uses the old rendering method which is weirdly formatted.

## KNOWN SILICON BUGS

From the Propeller 2 documentation:

---

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

---

These silicon bugs must be respected by the compiler, otherwise miscompilations appear.

## Implement "negtive SEQ" and "negative CONTAINS" items

Replace "CONTAINS_NOT:" with "CONTAINS:" and use an explicit marker for that:

```blade
// CONTAINS:
// - FOO
// ! BAR
```

where `! BAR` means that `BAR` must not be contained.

The same for sequence:


```blade
// CONTAINS:
// - ADD
// ! MOV
// - ADD
```

This means the sequence must be ADD, no MOV, then ADD again, which allows
us testing better for compiler optimizations.

This can even be optimized into a more general form:

```blade
// - A dash means at least once for CONTAINS and once for SEQUENCE
// ! A bang menas never for CONTAINS and "not between this and the next positive item"
// 1x A decimal + "x" means "exactly that many times" for SEQUENCE AND CONTAINS, while "0x" is an alias for "!"
```

This allows us to check potential loop unrolling, the exact number of things generated
and so on.

## Pointer arithmetic

Implement `ptr = ptr + 1` for `[*]T`

## Missing optimizations

```
    ' function count_string (Leaf)
  count_string
  count_string_bb0
    MOV _r2, _r2
    MOV _r1, #0
    JMP #count_string_bb2
  count_string_bb1
    _RET_ MOV PA, _r3
  count_string_bb2
    MOV _r4, #0
    ADD _r4, _r2
    RDBYTE _r5, _r4
    MOV _r4, #0
    CMP _r5, _r4 WZ
    WRNZ _r4
    CMP _r4, #0 WZ
    IF_Z MOV _r3, _r1
    IF_Z JMP #count_string_bb1
  count_string_bb3
    JMP #count_string_bb2
```

is far from optimal code

## Implement booleans as flags as long as possible

Right now, booleans are lowered to integers, then upgraded to flags
when needed for branching. This can be inverted to lower them to flags
first, then upgrade to integers when out of flags.

## Implement REP through magic "relative labels"

```pasm

  REP @.end, count
    NOP
    NOP
.end
```

## Division by "literal zero" must yield compile error

This includes eager constant folding and comptime evaluation.

## New rules on interrupt handlers + function pointers

Interrupt handlers must be installed similar to this:

```blade
// Function pointer syntax:
type Int3Handler = *int3 fn();

// Extern variables for the handler locations:
extern reg var IRET1: u32         @(0x1F5);
extern reg var IJMP1: *int1 fn()  @(0x1F4);
extern reg var IRET2: u32         @(0x1F3);
extern reg var IJMP2: *int2 fn()  @(0x1F2);
extern reg var IRET3: u32         @(0x1F1);
extern reg var IJMP3: Int3Handler @(0x1F0);

// Handler functions:
int1 fn int1_fn() {}
int2 fn int2_fn() {}
int3 fn int3_fn() {}

// Functions that are having a pointer taken must not be elided and are considered reachable,
// otherwise they'd be eliminted by DCE and the pointers would go into emptyness.
// Setup of the handlers must work like this:
IJMP1 = &int1_fn;
IJMP2 = &int2_fn;
IJMP3 = &int3_fn;

// Function pointers require explicit calling convention annotation:
var reg fptr1: *fn(a: u32, b: u32) = undefined;
var reg fptr2: *fn(a: u32, b: u32) -> u32 = undefined;

fptr1(10, 20);
var out: u32 = fptr2(10, 20);
```

## Suboptimal code order generation for blocks

```blade
reg var a: u32 = 0;
reg var b: u32 = 0;

if(a == b) { // MARKER 1
    asm volatile { 
        COGATN #1  // MARKER 2
    };
}
else { // MARKER 3
    asm volatile { 
        COGATN #2 // MARKER 4
    };
}
// MARKER 5
```

```spin2
  l_top
  l_top_bb0
    MOV _r3, g_a
    MOV _r2, g_b
    CMP _r3, _r2 WZ 
    WRZ _r2
    TJZ _r2, #l_top_bb3   ' MARKER 3
    JMP #l_top_bb2        ' MARKER 1
  l_top_bb1
    REP #1, #0            ' MARKER 5
  l_top_bb2
    COGATN #1             ' MARKER 2
    JMP #l_top_bb1
  l_top_bb3
    COGATN #2             ' MARKER 4
    JMP #l_top_bb1
```

is definitly worse than

```spin2
  l_top
  l_top_bb0
    MOV _r3, g_a
    MOV _r2, g_b
    CMP _r3, _r2 WZ
    WRZ _r2
    TJZ _r2, #l_top_bb3   ' MARKER 3
                          ' MARKER 1 (no instruction/branch necessary)
    COGATN #1             ' MARKER 2
    JMP #l_top_bb1
  l_top_bb3
    COGATN #2             ' MARKER 4
                          ' (no instruction/branch necessary)
  l_top_bb1
    REP #1, #0            ' MARKER 5
```

## Optimize storage of global `bool` variables

`bool` variables can be trivially tightly packed into a register. Each boolean
variable takes exactly a single bit.

The following operations map incredibly nicely to the Propeller 2 architecture:

- `a = true;` => `BITH backing, #index`
- `a = false;` => `BITL backing, #index`
- `a = !a` => `BITNOT backing, #index`
- `if(a)` => `TESTB backing, #index WZ`, `IF_Z JMP`
- `if(!a)` => `TESTB backing, #index WZ`, `IF_NZ JMP`
- `if(a & b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b ANDZ`, `IF_Z JMP`
- `if(a | b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b ORZ`, `IF_Z JMP`
- `if(a ^ b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_Z JMP`
- `if(a != b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_Z JMP`
- `if(a == b)` => `TESTB backing, #index_a WZ`, `TESTB backing, #index_b XORZ`, `IF_NZ JMP`
- `a = (x == y)` => `CMP x, y WZ`, `BITZ backing, #index`
- `a = (x != y)` => `CMP x, y WZ`, `BITNZ backing, #index`
- `if(a) { x |= 0x23; } else { x &= ~0x23; }` is `TESTB backing, #index WZ`, `MUXZ x, #$23`
- `if(a) { x = -x; }` is `TESTB backing, #index WZ`, `NEGC x`
- `if(a) { x += y; } else { x -= y; }` is `TESTB backing, #index WZ`, `SUMC x, y`

## Definition of Done

New acceptance criteria for AI agents so they consider their work done.

This shall be codified into a `just accept` recipe which they have to invoke without error before completing their work.

Acceptance criteria are:

- Not less code coverage than before.
- At least two new demonstrators are written, one with `fail`, one with `pass`.

## implement pointer arithmetics

see reference.blade

## refactor optimization system into a highly modular approach

Factor out each optimization into an `IMirOptimization`, `ILirOptimization`, `IAsmOptimization`.

These optimizations can then be registered through an attribute:

```csharp

[AsmOptimizer("eliminate-self-move")]
public sealed class SelfMovOptimizer : IAsmOptimization
{
  // Implements the interface functions
}

```

and at startup, the compiler scans its own assembly for `typeof(AsmOptimizerAttribute)` parts (same for mir and lir respectively)
and registers the optimizations into a "known optimization pool".

This allows us to easily add new types into the optimization pool and makes the code way more modular.

## immediate values are still going through a register often

```pasm
    MOV _r4, #0
    CMP _r5, _r4 WZ
```

is generated, which could also just be `CMP _r5, #0 WZ`

most likely issue is that both LIR and ASMIR cannot represent immediates, thus we cannot inline these in an early stage yet.

probable solution is to:

- Add immediate value forwarding to ASMIR optimizations
