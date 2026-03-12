# Blade — A Tiny Systems Language for the Propeller 2

*"What C was to the PDP-11"*

Working name: **Blade** (`.blade` extension). A high-level assembler for the Parallax
Propeller 2, targeting **COG execution mode**. Zig/Rust-inspired syntax. Every construct
compiles to predictable PASM2 with no hidden allocations, no runtime, and no surprises.

---

## 0. Design Principles

1. **Every statement maps to 1–3 PASM2 instructions.** The programmer must be able to
   predict the cost of any construct by looking at it. No invisible function calls,
   no heap, no vtables.

2. **The register file is the world.** In COG exec mode, code and data share 512 longs.
   The compiler treats registers as the primary storage, not an abstraction over memory.

3. **Coroutines are a calling convention, not a library.** CALLD-based cooperative
   multitasking is expressed directly in function signatures and yield semantics.

4. **Explicit over implicit.** Recursion requires a keyword. Storage class is mandatory.
   The programmer always knows where data lives and how calls are dispatched.

5. **The compiler is aggressive.** SSA-based register allocation, cost-based predication
   vs branching decisions, automatic tail calls, single-call inlining, and
   backwards-working parameter placement that eliminates MOVs.

---

## 1. Architecture Summary (What We're Compiling To)

### 1.1 COG Exec Memory Map

In COG exec mode, the Program Counter walks through Register RAM. Code *is* data.

```
$000 ─── General Purpose Registers ─── $1EF   (496 longs: code + data)
$1F0 ─── Dual-Purpose Registers ────── $1F7   (8 longs: IJMP3/IRET3..PA/PB)
$1F8 ─── Special-Purpose Registers ─── $1FF   (8 longs: PTRA/PTRB/DIR/OUT/IN)
```

**Hard limit**: All functions in COG/LUT exec must be ≤ 511 instructions. This is
an architectural constraint, not a language one — and it applies to all code, not
just REP blocks. The compiler enforces this per-function and reports overflow with
a register budget.

The compiler must **statically partition** `$000..$1EF` between code and data.
Functions occupy contiguous register spans. Variables are allocated in remaining
registers. The linker lays out the final map and verifies it fits.

### 1.2 Other Memory

| Region        | Size          | Access               | Width     | Blade Storage Class |
|---------------|---------------|----------------------|-----------|---------------------|
| Register RAM  | 512 × 32-bit  | Direct (1 cycle)     | long      | `reg`               |
| Lookup RAM    | 512 × 32-bit  | RDLUT/WRLUT (2+ cyc) | long      | `lut`               |
| Hub RAM       | 512 KB         | RD/WR (8+ cyc)       | byte/word/long | `hub`          |

### 1.3 Hardware Stack

8-level deep. Each entry stores `{C, Z, 10'b0, PC[19:0]}`. Used by:
- `CALL`/`RET` — automatic non-recursive calls
- `CALLPA`/`CALLPB` + `RET` — auto-tiered leaf / second-order leaf calls
- `PUSH`/`POP` — manual stack manipulation

The compiler **statically verifies** that no call chain exceeds 8 levels for
hardware-stack-based calling conventions.

### 1.4 Hub Stack (PTRB)

PTRB is an auto-incrementing/decrementing pointer into Hub RAM. It provides
unlimited-depth call stacks via `CALLB`/`RETB`. Reserved for `rec fn`.

**PTRA remains free** for user code — this is deliberate. Many functions accept
PTR expressions with auto-increment/decrement addressing modes (`PTRA++`,
`--PTRA`, `PTRA[offset]`), which are essential for efficient sequential hub
access: struct field reads, buffer walks, DMA patterns. Burning PTRA for the
recursive call stack would sacrifice this.

---

## 2. Type System

### 2.1 Primitive Integer Types

All types directly correspond to P2 hardware data paths:

| Type   | Width | Signedness | Backed By                                          |
|--------|-------|------------|-----------------------------------------------------|
| `bool` | 1 bit | —         | C or Z flag; TESTB/BITx WCZ; WRC/WRZ                |
| `bit`  | 1 bit | unsigned   | BITx instructions, TESTB; register bit position      |
| `nit`  | 2 bit | unsigned   | MUXNITS; bit-pair within a register                  |
| `nib`  | 4 bit | unsigned   | GETNIB/SETNIB + ALTGN/ALTSN                          |
| `u8`   | 8 bit | unsigned   | GETBYTE/SETBYTE + ALTGB/ALTSB; RDBYTE/WRBYTE         |
| `i8`   | 8 bit | signed     | Same + SIGNX #7                                      |
| `u16`  | 16 bit| unsigned   | GETWORD/SETWORD + ALTGW/ALTSW; RDWORD/WRWORD; MUL    |
| `i16`  | 16 bit| signed     | Same + SIGNX #15; MULS                               |
| `u32`  | 32 bit| unsigned   | Native register width. ADD/SUB/AND/OR/XOR; QMUL/QDIV |
| `i32`  | 32 bit| signed     | Same + ADDS/SUBS/CMPS/SAR; MULS                      |

### 2.2 The `uint(N)` / `int(N)` Generic Types

ZEROX and SIGNX can extend/truncate at any bit boundary 0..31:

```blade
var counter: uint(5) = 0;   // compiler inserts ZEROX reg, #4 after arithmetic
var offset: int(12) = -100;  // SIGNX reg, #11 to extend
```

`u8`, `i8`, `u16`, `i16` etc. are aliases: `u8` = `uint(8)`, `i16` = `int(16)`.

### 2.3 Booleans and Bit Packing

`bool` values live in C or Z flags when in flight, but can be packed into
registers using the BITx instruction family.

The BITx family (BITH, BITL, BITC, BITNC, BITZ, BITNZ, BITNOT) operates on
single bits *or contiguous bit ranges* within a register. With WCZ, they return
the **original** state of the target bit in both C and Z before modifying it.
This gives the compiler a single-instruction test-and-set/clear/toggle primitive.

**Key codegen pattern**: A conditional bool store like `flags.bit3 = (a != b)`
compiles to `CMP a, b WZ; BITNZ flags, #3` — one comparison, one bit-set
conditional on the result. No branches, no temporaries.

Global `bool` variables should be packed into shared `u32` registers by the
compiler, with each bool assigned a bit position.

### 2.4 Packed Arrays

Sub-long types can be packed into register arrays. The compiler uses ALTGx/ALTSx
for indexed access:

```blade
reg var palette: [32]nib = undefined;   // 32 nibbles in 4 registers
const color = palette[i];              // ALTGN i, #palette_base; GETNIB dest

reg var table: [16]u8 = undefined;     // 16 bytes in 4 registers
table[j] = 0xFF;                       // ALTSB j, #table_base; SETBYTE #0xFF
```

| Element Type | Elements per Long | ALTx Instruction |
|-------------|-------------------|------------------|
| `bit`       | 32                | ALTB + BITx/TESTB |
| `nit`       | 16                | (manual shift/mask or MUXNITS) |
| `nib`       | 8                 | ALTGN/ALTSN + GET/SETNIB |
| `u8` / `i8` | 4                | ALTGB/ALTSB + GET/SETBYTE |
| `u16`/`i16` | 2                | ALTGW/ALTSW + GET/SETWORD |
| `u32`/`i32` | 1                | ALTD/ALTS (plain register index) |

### 2.5 Structs (Register-Mapped)

```blade
const PinConfig = packed struct {
    mode: nib,       // bits 3:0
    drive: nit,      // bits 5:4
    _reserved: nit,  // bits 7:6
    schmitt: bool,   // bit 8
};

reg var pin_cfg: PinConfig = .{ .mode = 0x5, .drive = 0b11, .schmitt = true };
```

Field access compiles to GETNIB/GETBYTE/TESTB with compile-time-known positions.

---

## 3. Storage Model

### 3.1 Storage Classes

Every variable declaration **must** specify a storage class:

```blade
reg var counter: u32 = 0;
reg const MASK: u32 = 0xFF00FF00;
lut var buffer: [128]u32 = undefined;
hub var stream: [1024]u8 = undefined;
```

| Class | Keyword | Access Pattern | Typical Use |
|-------|---------|---------------|-------------|
| Register | `reg` | Direct, 0-cycle (pipelined) | Hot variables, accumulators, loop counters |
| Lookup | `lut` | RDLUT/WRLUT, 1 extra cycle stall | Lookup tables, large buffers, inter-cog comm |
| Hub | `hub` | RDBYTE/RDWORD/RDLONG, 8–20 cycles | Bulk data, shared state, recursive stack frames |

### 3.2 Register Allocation and Placement

```blade
reg var counter: u32 = 0 @(0x100);  // force allocation at register $100
```

### 3.3 Alignment

Variables can specify alignment for hardware requirements:

```blade
reg var dma_buf: [16]u32 align(16) = undefined;
```

Alignment is needed for:
- **ALTI**: requires specific field alignment in the target register.
- **SETQ + RDLONG/WRLONG block transfer**: contiguous aligned registers.
- **FIFO 64-byte block mode** (RDFAST/WRFAST/FBLOCK): hub addresses aligned to
  64-byte boundaries.

### 3.4 Hardware Registers

```blade
extern reg var DIRA: u32 @(0x1FA);
extern reg var DIRB: u32 @(0x1FB);
extern reg var OUTA: u32 @(0x1FC);
extern reg var OUTB: u32 @(0x1FD);
extern reg const INA: u32 @(0x1FE);
extern reg const INB: u32 @(0x1FF);
extern reg var PTRA: u32 @(0x1F8);    // free for user (hub data access)
extern reg var PTRB: u32 @(0x1F9);    // reserved for rec fn stack
extern reg var PA: u32 @(0x1F6);
extern reg var PB: u32 @(0x1F7);
```

---

## 4. Calling Conventions

### 4.1 `fn` — Standard Function (Automatic CC)

**The default.** The compiler analyzes the static call graph and auto-tiers:

| Call graph shape | Mechanism | Parameter passing |
|-----------------|-----------|-------------------|
| Calls nothing (leaf) | CALLPA + RET | One param in PA, result in PA |
| Calls only CALLPA-leaves | CALLPB + RET | One param in PB, result in PB |
| Calls anything else | CALL + RET | Global registers |

The CALLPA/CALLPB insight: **a CALLPB function can freely call CALLPA functions**,
because CALLPA pushes to the same hardware stack and only touches PA, while the
CALLPB frame's PB is untouched. This naturally maximizes the number of functions
on the fast parameter-passing path.

**Additional leaf optimization**: A leaf function (CALLPA-tier) can use PA freely
*even when calling other leaves*, as long as the parameter value doesn't need to
survive that child call. The compiler tracks PA liveness across call boundaries.

**Register sharing**: Functions that are **not in the same call graph** (i.e.,
statically provable never called overlappingly) share parameter registers. The
compiler doesn't just share between functions at the same tier — it shares across
the entire program where liveness doesn't conflict.

**Stack depth**: Static verification ≤ 8. Compile error suggesting `rec fn` on overflow.

**Tail calls**: When a function's last action is calling another function, the
compiler emits `JMP` instead of `CALL` — no stack slot consumed, no return overhead.
This applies across all tiers.

**Single-call inlining**: If a function is called from exactly one site in the
entire program, the compiler **always** inlines it. There is zero benefit to keeping
it as a separate function in COG exec (code is registers, there's no icache to share).

```blade
// Compiler sees: calls nothing → CALLPA leaf
fn square(x: u32) -> u32 {
    return x * x;         // MUL PA, PA
}

// Compiler sees: only calls CALLPA leaves → CALLPB second-order leaf
fn sum_of_squares(a: u32, b: u32) -> u32 {
    const aa = square(a);     // CALLPA a_reg, #square
    const bb = square(b);     // CALLPA b_reg, #square
    return aa + bb;
}
```

**SSA-based register allocation**: The compiler uses an SSA intermediate form
internally. A backwards-working register allocator derives that parameter values
should be placed directly into PA/PB/global param registers, often **eliding the
MOV entirely**. `yieldto filter(value)` doesn't need `MOV PA, value` if the
allocator already placed `value` in PA.

### 4.2 `leaf fn` — Explicit Leaf Constraint

A `leaf fn` is a promise: **this function makes no calls.** Any call in its body
is a compile error. Useful for guaranteeing CALLPA codegen won't change as the
codebase evolves:

```blade
leaf fn fast_clamp(val: u32, limit: u32) -> u32 {
    return @fle(val, limit);   // FLE PA, limit_reg
}
```

`leaf fn` is an explicit constraint. Unmarked `fn` functions still auto-detect
leaf status.

### 4.3 `inline fn` — Semantic Inlining

Forces inlining at every call site:

```blade
inline fn set_bit(reg_ref: u32, bit: u32) void {
    asm { BITH {reg_ref}, {bit} };
}
```

The body is spliced into the caller. No CALL/RET overhead. No register is consumed
for a return address. Useful for tiny utilities that would cost more to call than
to execute, and for wrapping single PASM2 instructions in a typed interface.

Note: single-call `fn` functions are auto-inlined anyway. `inline fn` is for
functions called from multiple sites where you still want inlining.

### 4.4 `rec fn` — Recursive Function

**Mechanism**: `CALLB #addr` / `RETB` (hub stack via PTRB)

Parameters pushed/popped from hub stack automatically by the compiler.

```blade
rec fn fibonacci(n: u32) -> u32 {
    if (n <= 1) return n;
    const a = fibonacci(n - 1);
    const b = fibonacci(n - 2);
    return a + b;
}
```

**Why PTRB, not PTRA**: PTRA is too valuable to burn on the call stack. Many hub
access patterns use PTRA with auto-increment/decrement addressing modes:

```blade
fn read_struct(hub ptr: *MyStruct) -> MyStruct {
    // These compile to RDLONG with PTRA++ expressions
    const field_a = ptr.a;   // RDLONG dest, PTRA++
    const field_b = ptr.b;   // RDLONG dest, PTRA++
    // ...
}
```

Reserving PTRB for the recursive stack keeps PTRA free for these patterns.

### 4.5 `coro fn` — Coroutine (CALLD-Based)

Each `coro fn` gets one extra long allocated **immediately before its first
instruction** in register space. This long holds the coroutine's **continuation
address** — the `{C, Z, PC}` value saved by CALLD when the coroutine yields.

**Restrictions:**
- Coroutines must be **top-level** (not nested, not closures).
- `yieldto` transfers control to a named coroutine with explicit parameters.
- The initial `yieldto` that starts the coroutine system must happen at top-level.

**No ring/pair declaration.** The topology emerges from usage.

```blade
coro fn producer() void {
    reg var value: u32 = 0;
    loop {
        value = read_sensor();
        yieldto filter(value);    // CALLD producer_cont, filter_cont
    }
}

coro fn filter(sample: u32) void {
    loop {
        if (sample > threshold) {
            yieldto consumer(sample);
        } else {
            yieldto producer();
        }
    }
}

coro fn consumer(value: u32) void {
    loop {
        send_byte(value);
        yieldto producer();
    }
}
```

**Codegen for `yieldto`**: `yieldto bar(x)` compiles to:

```pasm2
; If SSA allocator already placed x in PA, the MOV is elided
MOV  PA, x                    ; (only if needed)
CALLD foo_cont, bar_cont WCZ  ; save own {C,Z,PC+1} into foo_cont,
                              ; restore bar's {C,Z,PC} from bar_cont
```

The backwards-working register allocator should derive that parameter values
feed directly into the expected registers, often making the MOV a no-op.

**Initialization**: Each `foo_cont` register is seeded at startup with
`{0, 0, 10'b0, foo_entry_address}`.

### 4.6 `int1 fn` / `int2 fn` / `int3 fn` — Interrupt Handlers

P2 has three interrupt levels. Each maps to a dedicated IJMP/IRET register pair:

| Kind | Entry Register | Return Register | Return Insn | Yield Insn |
|------|---------------|-----------------|-------------|------------|
| `int1 fn` | IJMP1 ($1F4) | IRET1 ($1F5) | RETI1 | RESI1 |
| `int2 fn` | IJMP2 ($1F2) | IRET2 ($1F3) | RETI2 | RESI2 |
| `int3 fn` | IJMP3 ($1F0) | IRET3 ($1F1) | RETI3 | RESI3 |

**Not callable.** Interrupt handlers are entered exclusively by the hardware
interrupt mechanism. Attempting to call an `intN fn` from Blade code (via `CALL`,
`CALLPA`, `yieldto`, or any other mechanism) is a compile error. They exist
outside the normal call graph.

```blade
int1 fn on_timer() void {
    counter += 1;
}
// Compiles: body + RETI1
```

**Yielding from interrupts**: `yield` inside an `intN fn` compiles to `RESIn`,
which swaps the handler and interrupted-code continuations using the IJMP/IRET
register pair. The next interrupt resumes the handler right after the `yield`.

`RESIn` is `CALLD IJMPn, IRETn WCZ` — it saves the handler's current `{C,Z,PC+1}`
into IJMPn (so the *next* interrupt re-enters the handler at the right point) and
restores the interrupted code's `{C,Z,PC}` from IRETn.

**Returning from a yielding handler**: When the handler eventually returns (exits
its loop or reaches the end), the compiler emits a write of the handler's entry
address back into IRETn before RETI. No save is needed — the entry address is a
known constant. This ensures the next interrupt starts the handler fresh from the
top rather than resuming at a stale yield point:

```blade
int2 fn serial_handler() void {
    loop {
        const byte = pin.recv(RX_PIN);
        buffer[idx] = byte;
        idx += 1;
        yield;    // RESI2 — swap with interrupted context, resume here next INT2
    }
    // If we ever exit the loop, compiler emits:
    //   LOC IRET2, #serial_handler   ; reset entry point (known constant)
    //   RETI2                        ; return to interrupted code
}
```

Note: you do **not** save/restore IRETn around yields. During the yield/resume
cycle, IRETn and IJMPn naturally ping-pong between the handler continuation and
the interrupted code continuation — that's exactly how RESI works. The only time
you touch IRETn explicitly is on final return, to reset it to the entry point.

### 4.7 Return Values — The 34-Bit Return Channel

Every function can return up to **34 bits**: one 32-bit long (in a register) plus
two 1-bit bools (in the C and Z flags).

The compiler auto-selects flag placement, or the programmer specifies with `@C`/`@Z`:

```blade
fn read_sensor() -> u32 { ... }
fn is_ready() -> bool { ... }
fn compare(a: u32, b: u32) -> bool, bool { ... }
fn checked_add(a: u32, b: u32) -> u32, bool { ... }
fn custom(a: u32, b: u32) -> bool@Z { ... }
fn sub_with_borrow(a: u32, b: u32) -> u32, borrow: bool@C { ... }
```

**RET flag control**: RET supports individual flag modifiers — `RET WC`, `RET WZ`,
or `RET WCZ`. This means the compiler can selectively preserve or clobber each
flag independently:

| Return signature | RET variant | Effect |
|-----------------|-------------|--------|
| `-> u32` | `RET WCZ` | Restore both flags (no bool return) |
| `-> bool@C` | `RET WZ` | Preserve C (bool return), restore Z |
| `-> bool@Z` | `RET WC` | Preserve Z (bool return), restore C |
| `-> bool@C, bool@Z` | `RET` | Preserve both (both are returns) |
| `-> u32, bool@C` | `RET WZ` | Preserve C, restore Z |

### 4.8 Calling Convention Summary

| Kind | Keyword | Mechanism | Auto? | Stack | Max Depth |
|------|---------|-----------|-------|-------|-----------|
| Leaf | `fn` / `leaf fn` | CALLPA/RET | Yes | HW (8) | 8 |
| 2nd-order leaf | `fn` | CALLPB/RET | Yes | HW (8) | 8 |
| General | `fn` | CALL/RET | Yes | HW (8) | 8 |
| Inlined | `inline fn` | (spliced) | No | None | N/A |
| Recursive | `rec fn` | CALLB/RETB | No | Hub (PTRB) | ∞ |
| Coroutine | `coro fn` | CALLD/CALLD | No | None | N/A |
| Interrupt 1 | `int1 fn` | IJMP1/RETI1 | No (not callable) | None | N/A |
| Interrupt 2 | `int2 fn` | IJMP2/RETI2 | No (not callable) | None | N/A |
| Interrupt 3 | `int3 fn` | IJMP3/RETI3 | No (not callable) | None | N/A |

---

## 5. Top-Level Code

Blade allows **top-level statements** outside of any function, similar to Python.
This is the program entry point — there is no implicit `main()` call:

```blade
// uart_tx.blade
const exec_mode = .cog;

reg var bit_time: u32 = undefined;

// Top-level code: this is where execution begins
pin.high(TX_PIN);
bit_time = comptime { 180_000_000 / BAUD };

loop {
    const cmd = recv_byte();
    process(cmd);
}

// Functions defined alongside (order doesn't matter)
fn recv_byte() -> u32 { ... }
fn process(cmd: u32) void { ... }
```

The compiler gathers all top-level statements in source order and emits them as
the entry code starting at `$000`. Functions are laid out after (or around) the
entry code by the linker.

Top-level code can call any function, use any statement, and includes the
implicit infinite loop that most cog programs need. There's no ceremony.

---

## 6. Compile-Time Evaluation

`comptime` forces compile-time evaluation of expressions. The compiler evaluates
the expression eagerly during compilation and embeds the result as a constant:

```blade
const BIT_TICKS = comptime { 180_000_000 / 115_200 };   // → 1562
const SIN_TABLE = comptime { generate_sin_table(256) };  // computed at build
const MASK = comptime { (1 << NUM_BITS) - 1 };
```

`comptime` expressions must be deterministic and free of side effects. They can
call `comptime fn` functions:

```blade
comptime fn crc_init(poly: u32) -> [256]u32 {
    var table: [256]u32 = undefined;
    // ... compute CRC lookup table ...
    return table;
}

lut const CRC_TABLE: [256]u32 = comptime { crc_init(0xEDB88320) };
```

Benefits:
- Lookup tables computed at build time, emitted as register/LUT initializers.
- Complex constant expressions evaluated once, not at runtime.
- Reduces optimizer burden — the backend sees pre-computed constants.

---

## 7. Control Flow

### 7.1 Predicated Execution (The Primary Branching Strategy)

The compiler performs **cost analysis** on every if/else to decide between
predicated execution and branching. Predication is preferred whenever it produces
fewer total cycles or more predictable timing.

**The optimization principle**: On the P2, a taken branch costs 2–4 cycles for the
pipeline refill. A predicated instruction costs 2 cycles whether it executes or
is cancelled. When both paths are short, predication always wins.

**Example — single-operation if/else:**

```blade
if (x > threshold) {
    pin.high(led);
    counter += 1;
} else {
    pin.low(led);
}
```

Predicated (3 instructions, 6 cycles, deterministic):
```pasm2
CMP   x, threshold  WC     ; C = (x < threshold), NC = (x >= threshold)
DRVNC #led                  ; pin = high when x >= threshold, low otherwise
IF_NC ADD counter, #1       ; increment only when x >= threshold
```

Branching (5 instructions, 12–14 cycles, variable):
```pasm2
CMP   x, threshold  WC
IF_C  JMP #else             ; branch: 4 cycles
  DRVH  #led
  ADD   counter, #1
  JMP   #end                ; branch: 4 cycles
else:
  DRVL  #led
end:
```

The predicated version is **2x faster** and constant-time.

**Example — multi-statement if/else, still no branching:**

```blade
if (mode == 1) {
    init_uart();
    start_rx();
} else {
    init_spi();
    start_transfer();
}
```

Predicated (5 instructions, always 12 cycles with 2-cycle CALLs when not taken):
```pasm2
CMP   mode, #1  WZ
IF_Z  CALL #init_uart         ; 4 cycles if taken, 2 if cancelled
IF_Z  CALL #start_rx          ; 4 cycles if taken, 2 if cancelled
IF_NZ CALL #init_spi          ; 4 cycles if taken, 2 if cancelled
IF_NZ CALL #start_transfer    ; 4 cycles if taken, 2 if cancelled
```

This is 12 cycles in both paths — faster than the branching version (12 best, 14
worst) and perfectly deterministic.

Branching (7 instructions, 12–14 cycles, variable):
```pasm2
CMP   mode, #1 WZ
IF_NZ JMP #else               ; 2 or 4
  CALL #init_uart              ; 4
  CALL #start_rx               ; 4
  JMP  #end                    ; 4
else:
  CALL #init_spi               ; 4
  CALL #start_transfer         ; 4
end:
```

**When to branch**: The compiler switches to branching when the predicated version
would execute significantly more cancelled instructions than the branch cost saves.
Rule of thumb: if one path is ≥4 instructions longer than the other, branching
the short path and falling through the long one wins.

### 7.2 Single-Statement If → Always Predicated

```blade
if (carry) x = y;          // IF_C  MOV x, y
if (!zero) count += 1;     // IF_NZ ADD count, #1
```

### 7.3 Conditional Expressions

```blade
const result = if (carry) a else b;
// MOV result, b
// IF_C MOV result, a
```

### 7.4 Loops

#### `rep loop` — Zero-overhead hardware repeat (interrupt-shielded)

Uses the P2's REP instruction. The hardware repeats a fixed number of instructions
with zero branch overhead. **Interrupts are shielded** for the entire duration.

```blade
rep loop (32) {
    @wfbyte(data);
    data >>= 8;
}
// REP #2, #32
// WFBYTE data
// SHR data, #8
```

**Branching behavior**: In the final P2 silicon, REP does NOT terminate on branch.
A JMP/CALL inside a REP block causes **undefined behavior** — the REP hardware
keeps counting and wrapping the PC regardless. The compiler enforces: no JMP, CALL,
RET, or any other branch instruction inside `rep loop` or `rep for`.

However, **conditional execution prefixes (IF_xx) work perfectly inside REP**.
They predicate individual instructions without affecting the PC. This means:

**`continue` inside `rep`** is modeled via conditional execution. The compiler
transforms the remaining body instructions after the `continue` condition into
conditional form:

```blade
rep loop (count) {
    if (skip_this) continue;
    process(value);
    output(value);
}
// REP #5, count
//   TESTB skip_this, #0 WC        ; C = skip condition
//   IF_NC CALL #process            ; only if not skipping
//   IF_NC CALL #output             ; only if not skipping
```

`break` inside `rep` is a **compile error** (would require a branch).

#### `rep for` — Counted hardware repeat with index

```blade
rep for (i in 0..16) {
    table[i] = 0;
}
```

The compiler exploits ALTx auto-index (Src[17:9]) where possible, eliminating
the explicit increment instruction.

#### `noirq { }` — Interrupt-Shielded Block

REP with count=1 (execute once). Provides interrupt shielding without looping:

```blade
noirq {
    OUTA |= pin_mask;
    status = INA;
}
// REP #2, #1
// OR  OUTA, pin_mask
// MOV status, INA
```

Same body constraints as `rep loop` (no branches, IF_xx predicates are fine).

#### `for` — Software counted loop

```blade
for (count) {           // count is decremented each iteration
    process();
}
// loop: CALL #process
//       DJNZ count, #loop
```

#### `while` — Condition-tested loop

```blade
while (value != 0) {
    value = transform(value);
}
// loop: TJZ value, #end
//       CALLPA value, #transform
//       MOV value, PA
//       JMP #loop
// end:
```

#### `loop` — Infinite loop

```blade
loop {
    sample = read_adc();
    process(sample);
}
// loop: ...
//       JMP #loop
```

#### `break` and `continue`

Work in `for`, `while`, `loop`. Not in `rep loop`/`rep for` (break is impossible;
continue is via conditional execution, not a keyword — see above).

---

## 8. Pin and I/O Operations

```blade
pin.high(LED_PIN);          // DRVH #LED_PIN
pin.low(LED_PIN);           // DRVL #LED_PIN
pin.toggle(LED_PIN);        // DRVNOT #LED_PIN
pin.float(LED_PIN);         // FLTL #LED_PIN
pin.input(SENSOR_PIN);      // DIRL #SENSOR_PIN

const state = pin.read(SENSOR_PIN);  // TESTP WC → bool in C
if (pin.read(BUTTON)) { ... }

pin.drvflag(LED_PIN);       // DRVC — set pin to current C state
pin.drvnflag(LED_PIN);      // DRVNC

pin.mode(TX_PIN, config);   // WRPIN
pin.x(TX_PIN, baud_val);    // WXPIN
pin.y(TX_PIN, data);        // WYPIN
const rx = pin.recv(RX_PIN); // RDPIN
```

---

## 9. Inline Assembly

```blade
fn setup_streamer(mode: u32, count: u32) void {
    asm {
        SETQ  {count}
        XINIT {mode}, #0
    };
}
```

### Flag-output annotations

```blade
fn test_bit(val: u32, bit_pos: u32) -> bool@C {
    asm -> @C {
        TESTB {val}, {bit_pos} WC
    };
}
```

### Condition prefixes

```blade
asm {
    IF_C  ADD {acc}, {delta}
    IF_NC SUB {acc}, {delta}
};
```

---

## 10. Special Intrinsics

```blade
// Bit operations
const top_bit = @encod(value);       // ENCOD
const pop_cnt = @ones(value);        // ONES
const reversed = @rev(value);        // REV
const decoded = @decod(bit_num);     // DECOD
const mask = @bmask(width);          // BMASK

// Clamp
const clamped_lo = @fge(val, lo);    // FGE (unsigned)
const clamped_hi = @fle(val, hi);    // FLE (unsigned)
const abs_val = @abs(signed_val);    // ABS

// Extension
const byte_val = @zerox(reg, 7);     // ZEROX
const sext_val = @signx(reg, 11);    // SIGNX

// CORDIC (pipelined: issue then retrieve)
@qmul(a, b);  const lo = @getqx(); const hi = @getqy();
@qdiv(a, b);   const quot = @getqx(); const rem = @getqy();
@qrotate(x, angle); @qvector(x, y); @qsqrt(hi, lo);
@qlog(value); @qexp(value);

// CRC
const crc = @crcnib(state, poly);   // CRCNIB
const crc1 = @crcbit(state, poly);  // CRCBIT

// Timing
const now = @getct();                // GETCT
@waitx(cycles);                      // WAITX

// Random
const rnd = @getrnd();              // GETRND

// Locks
const lock = @locknew();            // LOCKNEW
const got: bool = @locktry(lock);   // LOCKTRY WC
@lockrel(lock); @lockret(lock);

// Cog control
const cog = @coginit(mode, addr, param);
@cogstop(cog_id);
```

---

## 11. Events

```blade
event.ct1.set(deadline);          // ADDCT1
if (event.ct1.poll()) { ... }     // POLLCT1 WC; IF_C ...
event.ct1.wait();                 // WAITCT1

event.se1.set(config);            // SETSE1
event.atn.wait();                 // WAITATN
@cogatn(cog_mask);                // COGATN
```

---

## 12. Complete Example — UART TX

```blade
// uart_tx.blade — Async serial transmitter
const exec_mode = .cog;

const TX_PIN: u32 = 62;
const BAUD: u32 = 115_200;
const BIT_TICKS: u32 = comptime { 180_000_000 / BAUD };

reg var bit_time: u32 = BIT_TICKS;
reg var tx_data: u32 = undefined;

// Top-level entry
pin.high(TX_PIN);

loop {
    const ch = recv_command();
    send_byte(ch);
}

// Compiler: calls nothing → CALLPA leaf
fn send_byte(char: u32) void {
    tx_data = char;
    tx_data |= 0x100;          // stop bit
    tx_data <<= 1;             // start bit (0)

    var next_tick = @getct();

    rep loop (10) {
        asm -> @C { TESTB {tx_data}, #0 WC };
        pin.drvflag(TX_PIN);       // DRVC — pin follows C
        tx_data >>= 1;
        next_tick += bit_time;
        event.ct1.set(next_tick);
        event.ct1.wait();
    };
}

// Compiler: calls send_byte (CALLPA leaf) → CALLPB second-order leaf
fn send_string(hub ptr: *const u8) void {
    var ch: u32 = undefined;
    loop {
        ch = ptr.*;             // RDBYTE ch, PTRA (using PTRA for sequential read)
        if (ch == 0) break;
        send_byte(ch);          // CALLPA ch, #send_byte
        ptr += 1;
    };
}

fn recv_command() -> u32 { ... }
```

---

## 13. Coroutine Example

```blade
const exec_mode = .cog;
reg var threshold: u32 = 128;

coro fn producer() void {
    reg var value: u32 = 0;
    loop {
        value = read_adc();
        yieldto filter(value);
    }
}

coro fn filter(sample: u32) void {
    loop {
        if (sample > threshold) {
            yieldto consumer(sample);
        } else {
            yieldto producer();
        }
    }
}

coro fn consumer(value: u32) void {
    loop {
        send_byte(value);
        yieldto producer();
    }
}

// Top-level entry
init_hardware();
yieldto producer();   // enter coroutine system (never returns)
```

---

## 14. Interrupt Handler Example

```blade
const exec_mode = .cog;
reg var tick_count: u32 = 0;
reg var next_deadline: u32 = 0;

int1 fn timer_tick() void {
    loop {
        tick_count += 1;
        next_deadline += TICK_INTERVAL;
        event.ct1.set(next_deadline);
        yield;     // RESI1 — return to interrupted code, resume here on next INT1
    }
    // If we ever exit the loop:
    // compiler restores IRET1 to entry point, then RETI1
}

// Top-level entry
next_deadline = @getct() + TICK_INTERVAL;
event.ct1.set(next_deadline);
asm { SETINT1 #0 };   // INT1 source = CT1 event

loop {
    do_work();  // preempted by timer_tick on INT1
}
```

---

## 15. Grammar Sketch (EBNF-ish)

```ebnf
program        = { module_decl | import | declaration | function | statement }

module_decl    = "const" "exec_mode" "=" ".cog" ";"

import         = "import" STRING "as" IDENT ";"

declaration    = storage_class ("var"|"const") IDENT ":" type ["=" expr] ["@" "(" expr ")"]
                 ["align" "(" expr ")"] ";"

storage_class  = "reg" | "lut" | "hub"

type           = "u8" | "i8" | "u16" | "i16" | "u32" | "i32"
               | "bool" | "bit" | "nit" | "nib"
               | "uint" "(" EXPR ")" | "int" "(" EXPR ")"
               | "[" EXPR "]" type
               | "packed" "struct" "{" { field } "}"
               | "*" ["const"] type
               | "void"

function       = func_kind "fn" IDENT "(" params ")" ["->" return_spec] block

func_kind      = "" | "leaf" | "inline" | "rec" | "coro" | "comptime"
               | "int1" | "int2" | "int3"

return_spec    = return_item { "," return_item }
return_item    = [IDENT ":"] type ["@C" | "@Z"]

params         = { [storage_class] IDENT ":" type "," }

block          = "{" { statement } "}"

statement      = declaration
               | assignment ";"
               | if_stmt
               | while_stmt
               | for_stmt
               | loop_stmt
               | rep_loop_stmt
               | rep_for_stmt
               | noirq_stmt
               | "return" [expr_list] ";"
               | "break" ";"
               | "continue" ";"
               | "yield" ";"                              // intN fn only
               | "yieldto" IDENT "(" [expr_list] ")" ";"  // coro fn / top-level
               | asm_block
               | expr ";"

if_stmt        = "if" "(" expr ")" (block | single_stmt)
                 ["else" (block | if_stmt | single_stmt)]

while_stmt     = "while" "(" expr ")" block
for_stmt       = "for" "(" IDENT ")" block
loop_stmt      = "loop" block
rep_loop_stmt  = "rep" "loop" "(" expr ")" block
rep_for_stmt   = "rep" "for" "(" IDENT "in" range ")" block
noirq_stmt     = "noirq" block

asm_block      = "asm" ["->" flag_outputs] "{" { asm_line } "}" ";"
asm_line       = [CONDITION] MNEMONIC { operand } ["WC"|"WZ"|"WCZ"]

comptime_expr  = "comptime" block
```

---

## 16. Open Questions / Future Work

1. **Operators**: Full operator table (arithmetic, bitwise, comparison, wrapping,
   saturating) — deferred to next iteration.

2. **LUT exec mode**: `exec_mode = .lut` frees all 512 registers for data.

3. **Multi-cog orchestration**: Higher-level syntax for cog launch, shared hub
   variables, lock discipline.

4. **LUT sharing**: Type-system support for paired-cog LUT write permissions.

5. **Streamer/FIFO abstractions**: Structured API beyond raw asm.

6. **Debug**: BRK/GETBRK integration, source-level register-to-variable mapping.

7. **Hub pointer semantics**: How hub pointers interact with PTRA, FIFO types.

8. **Bit-packed bool formalization**: Whether global bool packing is automatic
   or explicit (`packed bool`).

9. **`continue` in `rep`**: The conditional-execution model works, but the syntax
   may need refinement — does `continue` inside `rep loop` look too much like it
   branches? Consider alternative keyword or just require explicit IF_xx patterns.
