# Planned Changes

## `CALLPA` bindings are important

Leaf functions through CALL/PA should prefer the "PA" parameter for their first parameter value.

This plays together nicely with the register allocator running backwards.

## Implementation of array loads/stores

Right now only the high-level part is implemented, but lowering the array access is not.

## `rec fn` seems to miscompile

Validate that `rec fn` uses CALLB and stack spilling when calling other rec functions.

## Assert that DIRA, OUTA, ... names are always bound to the correct special function register

In FinalAssemblyWriter.WriteConBlock, skip CON alias emission when the fixed-address alias name is already a real hardware register identifier (DIRA, OUTA, INA, etc.).

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

## Return values don't properly compile at all for bit-style return values

```blade
// EXPECT: pass
// STAGE: final-asm
// CONTAINS:
// - ADD
// NOTE:
//   asm fn with flag return annotation (-> bool@C).

asm fn add_with_carry(a: u32, b: u32) -> bool@C {
    ADD {a}, {b} WC
}

reg var flag: bool = false;
flag = add_with_carry(0xFFFF_FFFF, 1);
```

will yield

```pasm
AUGS #8388607
MOV _r1, #511
MOV _r2, #1
' inline asm flag-output @C begin

ADD _r1, _r2 WC

' inline asm flag-output @C end
MOV g_flag, #0
```

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

## Bug: Address of array element

`ptr = &ptr[1];` yields `E0223: Address-of requires an addressable variable or parameter.`

## Pointer arithmetic

Implement `ptr = ptr + 1` for `[*]T`

## Spurious WRLONG on pointer ref?

```blade
hub var greeting: [16]u8 = undefined;
length = count_string(&greeting);
```

compiles to

```spin2
l_top_bb0
    MOV _r1, #0
    WRLONG _r1, h_greeting
    MOV PA, h_greeting
    CALLPA PA, #count_string
    MOV g_length, PA
    ' halt: endless loop
    REP #1, #0
```

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

## `count` value gets swallowed and won't be incremented

```blade
hub var greeting: [16]u8 = undefined;

fn count_string(ptr: [*]hub const u8) -> u32 {
    var count: u32 = 0;

    while(ptr[0] != 0) {
        count += 1;
        ptr = bitcast([*]hub const u8, bitcast(u32, ptr) + 1);
    }

    return count;
}

reg var length: u32 = 0;

length = count_string(&greeting);
```

has no sign of `ADD`, `#1` or `INC` included.

## Implement REP through magic "relative labels"

```pasm

  REP @.end, count
    NOP
    NOP
.end
```

## Division by "literal zero" must yield compile error

This includes eager constant folding and comptime evaluation.

## Missing peer type resolution for enum literals in comparison operators

```blade
type Mode = enum(u8) { On, Off };

var mode_b: Mode = .Off;
if (mode_b == .Off) {
  // must work
}
```

## just coverage-report with delta information is super helpful for agents

TextDeltaSummary

https://reportgenerator.io/usage


## compiler bugs 

Found it! if without else inside a comptime function body triggers the bug. The binder likely
lowers bare if differently when constant-folding for comptime - perhaps transforming it into a
naked BoundBlockStatement when the then-branch evaluates to true. This is a compiler bug.


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