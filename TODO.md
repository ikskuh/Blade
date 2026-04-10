# Planned Changes

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

## immediate values are still going through a register often

```pasm
    MOV _r4, #0
    CMP _r5, _r4 WZ
```

is generated, which could also just be `CMP _r5, #0 WZ`

most likely issue is that both LIR and ASMIR cannot represent immediates, thus we cannot inline these in an early stage yet.

probable solution is to:

- Add immediate value forwarding to ASMIR optimizations

## symbol attributes

`[Used]` and `[LinkName("_start")]`

## Assert.NotNull

            AsmOperand yieldStateDestination = ctx.Tier == CallingConventionTier.Coroutine
                && ctx.CoroutineCallingConvention.TryGetValue(ctx.Function.Name, out CoroutineCallingConventionInfo? sourceInfo)
                && sourceInfo is not null
                ? new AsmPlaceOperand(sourceInfo.StatePlace)
                : ctx.TopLevelYieldStatePlace is not null
                    ? new AsmPlaceOperand(ctx.TopLevelYieldStatePlace)
                    : Assert.UnreachableValue<AsmOperand>();

## introduce new dump "asmir-prealloc"

New dump to analyze the code after ASMIR optimizations but before
the register allocator folded variables.

## Missed optimization

```blade
reg var shared: u32 = 0;
shared = shared + 1;
```

compiles to

```pasm
MOV _r4, g_shared
ADD _r4, #1
MOV g_shared, _r4
```

while `shared += 1` compiles to `ADD _r4, #1`.

`Demonstrators/Optimizations/asmir-global_reg-operator-no-copy.blade`

## Rewrite how inline assembly works in general

Right now, it is transposed into a block/IR for non-volatile asm, and volatile asm seems to be treated as a string
which is regularly rewritten.

This seems brittle, and i think a better solution is having a "block" of inline assembly which just also contains IR
that must never be reordered at all.

This should simplify the code generation pipeline by reducing a lot of "rewriting".

## Clean up demonstrators

Apply new `?` operand matching instead of hardcoding internals like generated symbol names `_r1` and such.

## Code Smells

`nodes.Add(new AsmInstructionNode(ParseMnemonic(opcode), [place, value]));`

## Optimizations

- Implement load/store for larger types between (register|lut)<->hub with block transfers.
- Implement load/store for register-stored arrays with
  - u32: `ALTS`, `ALTD`, `ALTR`
  - u16: `ALTSW`, `ALTGW`, `SETWORD`, `GETWORD`
  - u8: `ALTSB`, `ALTGB`, `SETBYTE`, `GETBYTE`
  - nib: `ALTSN`, `ALTGN`, `SETNIB`, `GETNIB`
  - bool/bit: `BITZ`, `BITNZ`, `BITC`, `BITNZ`, `BITH`, `BITL`, `BITNOT`, `TESTB`, `TESTBN`
- Implement common constant multiply/divide strategies (QMUL takes 51 cycles, so we have up to 25 instructions before a blocking QMUL is good)
  - `* POT` == shift by log2(pot)
  - `* 3` = `a*2 + a`
  - `* 5` = `a*4 + a`
  - `* 6` = `a*4 + a*2`
  - `* 7` = `a*8 - a`
  - ...

## Argument/return fusion

Implement optimization that function argument/retval storage places can be fused.
Different labels for clarity, but same memory slot for efficiency when proven that 
they cannot overlap anyways.

## Rework register allocator to run backwards

This should give much much better results than running front-to-back.

## Rewire inline assembly into proper language syntax

Right now, inline assembly is an afterthought.

Wire it properly into the language frontend so it's parsed by the parser, not by a
later staged validator.

This removes a lot of surface from the compiler and gives additional useful properties
like proper location tracking

## Add emission of JSON based debug info

- Source <=> Assembly mapping
- Resolved values for all symbols

## Properly implement intrinsics

- Don't derive them in ASMIR stage, but create proper instances of "IntrinsicFunction" with
  defined parameter lists and return values.
- Instructions like `COGID {#}D {WC}` have a dual-use:
  - `COGID reg` writes own cog id to `reg`, usage is `var id: u32 = @COGID();`
  - `COGID id  WC` writes alive status of cog `id` to `C`. usage is `var status: bool = @COGID(reg);`
  - `COGID #id WC` writes alive status of cog `id` to `C`. usage is `var status: bool = @COGID(10);`
- Instructions like `RDFAST {#}D,{#}S` should be able to take pointers for `S`, but not for `D`.
  - This requires maintaining an additional hand-written instruction database (yaml or json) for all instructions / mnemonics
- Instructions like `RFBYTE D {WC/WZ/WCZ}` should return `u8`, as `D` will receive a zero-extended byte, and never 32 bit
  - This also requires the instruction database to define properties of the consumed or returned values (here: bit count/size)

## Refactor storage organization

These should not be modelled as assembly instructions, but as typed blocks.

Also `object?` is a bad storage format, we already have a Comptime Value for this

Also we need `ALIGNW` and `ALIGNL` for storage emission

## Rework final assembly writer to use less "hacks"

`functionNames.Contains(name)` is bad code. We already know everything from
the symbol types themselves.

`FormatPlaceOperand` and `FormatSymbolOperand` seem 100% redundant.

Why isn't a "place" a symbol?

##  Rework parser to not require `IReadOnlyList<object>` at all

see topic

## Improve BladeValue/TypeSymbol

- Support comptime comparison for `==` and `!=` on types in the language.


## Compiler Bug

`extern var foo: u32` is legal, but doesn't make sense (foo has no name/storage assigned!)

## header comment parsing in regression tester is broken

```blade
// EXPECT: fail

// this is not a header comment anymore
```

## Design Issue: `yieldto` from module constructor

In the original design, `yieldto` was only allowed on the top-level code.

With the introduction of modules, `yieldto` could now be called in the magic top-level constructor code of a module.

This is an issue, as this implies calling `yieldto` with a potential non-zero stack level, and any call to `yieldto` cannot return.

Goal is to come up with a better first-class coroutine support that works well with nested function calls as `yieldto` is the wrong keyword for kicking off a "coroutine process".

Also consider if coroutines should be able to return back to the caller, which would technically be possible by any coroutine to execute a `RET`.

Maybe treating a "coroutine process" as a function which can return, but internally jump between routines makes sense?


