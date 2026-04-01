# `Work/HwTestSuite.md` — Desired State

This document describes the intended hardware test suite for the Blade compiler.
All fixtures live under `Demonstrators/HwTest/` and must carry `// EXPECT: pass-hw`.
All run results are computed values that fit in `rt_result: u32`.

Result-packing conventions used throughout:

- **Scalar result** — computed value written directly to `rt_result`.
- **Bool/bit result** — `0` (false/0) or `1` (true/1) written to `rt_result`.
- **Multi-check result** — bit- or byte-packed into `rt_result`; each fixture documents its layout in a `NOTE`.
- **Signed result** — `bitcast(u32, ...)` applied before storing into `rt_result`.

---

## Stage 0 — Coverage Matrix

Maps each current HwTest family and each planned family to its relevant section of `Docs/reference.blade`.

| Fixture family                        | Reference section                               | Coverage status              |
| ------------------------------------- | ----------------------------------------------- | ---------------------------- |
| `test_runner`                         | Parameter injection baseline                    | broad smoke — baseline       |
| `hw_arithmetic_u32`                   | Expressions & Operators → arithmetic            | broad smoke, needs more RUNS |
| `hw_bitwise_u32`                      | Expressions & Operators → bitwise               | broad smoke, needs more RUNS |
| `hw_shifts_u32`                       | Expressions & Operators → shifts/rotates        | broad smoke, needs more RUNS |
| `hw_comparisons_u32`                  | Expressions & Operators → comparison            | broad smoke, needs more RUNS |
| `hw_unary_i32`                        | Expressions & Operators → unary                 | broad smoke, needs more RUNS |
| `hw_signed_arithmetic`                | Expressions & Operators → signed arithmetic     | broad smoke, needs more RUNS |
| `hw_compound_assign`                  | Expressions & Operators → compound assign       | broad smoke, needs more RUNS |
| `hw_if_expression`                    | Control Flow → if-else expression               | broad smoke, needs more RUNS |
| `hw_function_calls`                   | Functions → default cc, noinline                | broad smoke, needs more RUNS |
| `hw_for_loop`                         | Control Flow → for loop                         | broad smoke, needs more RUNS |
| `hw_while_loop`                       | Control Flow → while loop                       | broad smoke, needs more RUNS |
| `hw_bool_logic` _(planned)_           | Expressions & Operators → boolean               | missing family               |
| `hw_casts_and_bitcasts` _(planned)_   | Type System → casts, bitcast                    | missing family               |
| `hw_multi_return` _(planned)_         | Functions → multi-return, discard               | missing family               |
| `hw_named_args_and_const` _(planned)_ | Functions → named args; Variables → local const | missing family               |
| `hw_leaf_call` _(planned)_            | Calling Conventions → leaf fn                   | missing family               |
| `hw_recursive_fn` _(planned)_         | Calling Conventions → rec fn                    | missing family               |
| `hw_rep_for` _(planned)_              | Control Flow → rep for                          | missing family               |
| `hw_noirq` _(planned)_                | Control Flow → noirq                            | missing family               |
| `hw_enums` _(planned)_                | Type System → enum                              | missing family               |
| `hw_bitfields` _(planned)_            | Type System → bitfield                          | missing family               |
| `hw_pointer_arithmetic` _(planned)_   | Expressions & Operators → pointer arithmetic    | missing family               |
| `hw_struct_single_word` _(planned)_   | Type System → struct                            | missing family               |
| `hw_hub_storage` _(planned)_          | Variables → hub var/const                       | missing family               |
| `hw_lut_storage` _(planned)_          | Variables → lut var/const                       | missing family               |
| `hw_modules` _(planned)_              | Module Subsystem                                | missing family               |
| `hw_asm_value` _(planned)_            | Inline Assembly → asm, asm volatile, asm fn     | missing family               |
| `hw_asm_flag` _(planned)_             | Inline Assembly → asm fn → bool@C               | missing family               |

---

## Stage 1 — Strengthen Existing Families With More `RUNS`

Expand each existing file in place. No new files.

### `test_runner`

Baseline parameter-injection fixture. Already uses all 8 `rt_paramN` values.
Keep as-is; it documents the RUNS syntax and the runtime contract.

Current RUNS:

```
- [ 0, 0, 0, 0, 0, 0, 0, 0 ] = 0
- [ 1, 2, 3, 4, 5, 6, 7, 8 ] = 8
- [ 0xFFFFFFFF, 0, 0, 0, 0, 0, 0, 0 ] = 0xFFFFFFFF
```

---

### `hw_arithmetic_u32`

Tests `+`, `-`, `*`, `/`, `%` on `u32` through a single `arithmetic(a, b)` function.
Current: one run with `a=10, b=3`.

**Planned run matrix:**

| Run | `rt_param0`  | `rt_param1` | `rt_result`  | Rationale                                      |
| --- | ------------ | ----------- | ------------ | ---------------------------------------------- |
| 1   | `10`         | `3`         | `54`         | existing — general case                        |
| 2   | `0`          | `1`         | `0`          | zero left operand; identity for `+` and `*`    |
| 3   | `1`          | `1`         | `4`          | identity divisor; confirms `div`=1 and `mod`=0 |
| 4   | `100`        | `7`         | `242`        | asymmetric; distinguishes `/` from `%`         |
| 5   | `0xFFFFFFFF` | `1`         | `0xFFFFFFFE` | wraparound add and sub                         |

Run 2: 0+1 + 0−1 + 0×1 + 0/1 + 0%1 = 1 + 0xFFFFFFFF + 0 + 0 + 0 = 0.
Actually: `0+1=1`, `0-1=0xFFFFFFFF (u32)`, `0*1=0`, `0/1=0`, `0%1=0` → sum = `0`.
Corrected: result = 0 + 0xFFFFFFFF + 0 + 0 + 0 = 0xFFFFFFFF as u32 addition, which wraps. Actually `1 + 0xFFFFFFFF = 0x100000000 = 0` (mod 2^32). So result = 0.

Run 3: `a=1, b=1`: `1+1=2`, `1-1=0`, `1*1=1`, `1/1=1`, `1%1=0` → sum = 4.

Run 4: `a=100, b=7`: `107 + 93 + 700 + 14 + 2 = 916` = `0x394`.

Run 5: `a=0xFFFFFFFF, b=1`: `0 + 0xFFFFFFFE + 0xFFFFFFFF + 0xFFFFFFFF + 0 = …`. Wraparound test.

> **Note:** Compute final expected values precisely before implementing. The NOTE comment in each fixture must show the arithmetic chain.

---

### `hw_bitwise_u32`

Tests `&`, `|`, `^`, `~` on `u32` through `bitwise(a, b)`.
Current: one run with `a=0xAAAA, b=0x5555`.

**Planned run matrix:**

| Run | `rt_param0`  | `rt_param1`  | Rationale                                                       |
| --- | ------------ | ------------ | --------------------------------------------------------------- |
| 1   | `0xAAAA`     | `0x5555`     | existing — disjoint nibble masks                                |
| 2   | `0`          | `0`          | zero inputs; confirms all-zero outputs except `~a = 0xFFFFFFFF` |
| 3   | `0xFFFFFFFF` | `0xFFFFFFFF` | all-ones inputs; confirms `&=all-ones`, `                       | =all-ones`, `^=0`, `~=0` |
| 4   | `0xF0F0F0F0` | `0x0F0F0F0F` | alternating byte masks — disjoint                               |
| 5   | `0xA5A5A5A5` | `0xA5A5A5A5` | self-alias; confirms `^=0`, `&=a`, `                            | =a`                      |

---

### `hw_shifts_u32`

Tests `<<`, `>>`, `<<<`, `>>>`, `<%<`, `>%>` on `u32` through `shifts(val, amount)`.
Current: one run with `val=0x80000001, amount=4`.

**Planned run matrix:**

| Run | `rt_param0` (val) | `rt_param1` (amount) | Rationale                                                |
| --- | ----------------- | -------------------- | -------------------------------------------------------- |
| 1   | `0x80000001`      | `4`                  | existing — general distinguishing case                   |
| 2   | `0x12345678`      | `0`                  | shift by zero — all six results equal `val`              |
| 3   | `0x00000001`      | `1`                  | shift by 1 with LSB set — SAL shifts in LSB              |
| 4   | `0x80000000`      | `31`                 | shift by 31 with MSB set — SAR shifts in MSB all the way |

---

### `hw_comparisons_u32`

Tests `==`, `!=`, `<`, `<=`, `>`, `>=` on `u32` using packed multi-pair result.
Current: 3 pairs packed into 3 bytes (`a<b`, `a>b`, `a==b`).
Bit layout per pair: bit0=eq, bit1=ne, bit2=lt, bit3=le, bit4=gt, bit5=ge.

**Planned additional runs:**

| Run | params                              | Rationale                                           |
| --- | ----------------------------------- | --------------------------------------------------- |
| 1   | `[5,10, 10,5, 7,7]`                 | existing — canonical three-way coverage             |
| 2   | `[0,0xFFFFFFFF, 0xFFFFFFFF,0, 0,0]` | boundary values: min vs max, max vs min, zero-zero  |
| 3   | `[1,1, 0,1, 1,0]`                   | equal, `a<b` at 0/1 boundary, `a>b` at 0/1 boundary |

---

### `hw_unary_i32`

Tests `-`, `+`, `~` on `i32` through `unary(x)`.
Current: one run with `x=42`.

**Planned run matrix:**

| Run | `rt_param0`              | Rationale                                            |
| --- | ------------------------ | ---------------------------------------------------- |
| 1   | `42`                     | existing — positive input                            |
| 2   | `0`                      | zero input; confirms `-0=0`, `+0=0`, `~0=0xFFFFFFFF` |
| 3   | `-1` (i.e. `0xFFFFFFFF`) | negative input                                       |

Run 2: `0 + 0 + 0xFFFFFFFF = 0xFFFFFFFF`. Store as `bitcast(u32, ...)`.

---

### `hw_signed_arithmetic`

Tests `+`, `-`, unary `-` on `i32` through `signed_ops(a, b)`.
Current: one run with `a=-50, b=30`.

**Planned run matrix:**

| Run | `rt_param0`  | `rt_param1` | Rationale                        |
| --- | ------------ | ----------- | -------------------------------- |
| 1   | `-50`        | `30`        | existing — mixed sign            |
| 2   | `-10`        | `-20`       | both negative                    |
| 3   | `0`          | `0`         | zero crossing; confirms identity |
| 4   | `0x7FFFFFFF` | `1`         | positive overflow boundary       |

---

### `hw_compound_assign`

Tests `|=`, `&=`, `^=`, `<<=`, `>>=` on `u32` through a chained `compound(x)` function.
Current: one run with `x=0x1234`.

**Planned run matrix:**

| Run | `rt_param0`  | Rationale                                             |
| --- | ------------ | ----------------------------------------------------- |
| 1   | `0x1234`     | existing                                              |
| 2   | `0`          | zero seed — confirms bit masking propagates correctly |
| 3   | `0xFFFFFFFF` | all-ones seed                                         |
| 4   | `0xDEADBEEF` | arbitrary asymmetric seed                             |
| 5   | `0x00FF00FF` | alternating-byte seed                                 |

> **Extension:** consider adding `+=`, `-=`, `*=` to the chained function so compound-arithmetic operators are also exercised at runtime.

---

### `hw_if_expression`

Tests `if (cond) a else b` expression through `max(a,b)` and `min(a,b)`.
Current: one run with `a=100, b=200`.

**Planned run matrix:**

| Run | `rt_param0` | `rt_param1`  | Rationale                                |
| --- | ----------- | ------------ | ---------------------------------------- |
| 1   | `100`       | `200`        | existing — `a < b`                       |
| 2   | `200`       | `100`        | `a > b` — swapped                        |
| 3   | `50`        | `50`         | `a == b` — equal inputs; max + min = 2*a |
| 4   | `0`         | `0xFFFFFFFF` | boundary values                          |

---

### `hw_function_calls`

Tests nested `noinline` function calls through `compute(x) = add(mul(x,x), x)`.
Current: one run with `x=10`.

**Planned run matrix:**

| Run | `rt_param0` | Rationale                                             |
| --- | ----------- | ----------------------------------------------------- |
| 1   | `10`        | existing — `x^2 + x = 110`                            |
| 2   | `0`         | zero — confirms call chain returns 0                  |
| 3   | `1`         | `1 + 1 = 2` — confirms argument transport at boundary |
| 4   | `5`         | `25 + 5 = 30` — intermediate value                    |

> **Extension:** add a second noinline function that passes 3+ arguments to cover multi-parameter ABI transport.

---

### `hw_for_loop`

Tests `for(n) -> i` through `sum_indices(n) = sum of 0..n-1`.
Current: one run with `n=10`.

**Planned run matrix:**

| Run | `rt_param0` | `rt_result` | Rationale                               |
| --- | ----------- | ----------- | --------------------------------------- |
| 1   | `10`        | `45`        | existing                                |
| 2   | `0`         | `0`         | n=0 — zero iterations                   |
| 3   | `1`         | `0`         | n=1 — single iteration yielding index 0 |
| 4   | `5`         | `10`        | typical small count                     |

> **Extension:** add a second function testing `for(n)` (count-only, no index) to confirm the no-index form also works at runtime.

---

### `hw_while_loop`

Tests `while(cond)` through `sum_to(n) = 1+2+…+n`.
Current: one run with `n=10`.

**Planned run matrix:**

| Run | `rt_param0` | `rt_result` | Rationale                |
| --- | ----------- | ----------- | ------------------------ |
| 1   | `10`        | `55`        | existing                 |
| 2   | `0`         | `0`         | n=0 — loop never entered |
| 3   | `1`         | `1`         | single iteration         |
| 4   | `5`         | `15`        | typical small count      |

---

## Stage 2 — Add Missing Runtime Families

Each new file in this stage promotes a feature that already passes non-Hw demonstrators.

---

### `hw_bool_logic` _(new file)_

**Covers:** `and`, `or`, `!`, non-short-circuit `&`, `|`, `^` on `bool`/`bit`.
**Source demonstrators:** `Demonstrators/BitBools/pass_bool_bit_ops.blade`, `pass_conditional.blade`.

**Fixture design:**
Single `bool_ops(a: bool, b: bool) -> u32` function that packs 7 boolean results into bits 0–6 of `rt_result`:

```
bit0 = a and b      (short-circuit AND)
bit1 = a or  b      (short-circuit OR)
bit2 = a &   b      (non-short-circuit AND)
bit3 = a |   b      (non-short-circuit OR)
bit4 = a ^   b      (XOR)
bit5 = !a
bit6 = !b
```

**Planned run matrix:**

| Run | `rt_param0` (a) | `rt_param1` (b) | `rt_result` | Rationale                                          |
| --- | --------------- | --------------- | ----------- | -------------------------------------------------- |
| 1   | `1` (true)      | `1` (true)      | `0x0F`      | both true: and=1,or=1,&=1,\|=1,^=0,!a=0,!b=0       |
| 2   | `0` (false)     | `0` (false)     | `0x60`      | both false: and=0,or=0,&=0,\|=0,^=0,!a=1,!b=1      |
| 3   | `1` (true)      | `0` (false)     | `0x62`      | a true, b false: and=0,or=1,&=0,\|=1,^=1,!a=0,!b=1 |
| 4   | `0` (false)     | `1` (true)      | `0x42`      | a false, b true: and=0,or=1,&=0,\|=1,^=1,!a=1,!b=0 |

> Result values above are illustrative; compute exact packed values when implementing.

---

### `hw_casts_and_bitcasts` _(new file)_

**Covers:** `as` (narrowing/widening integer casts), `bitcast` between integer and structured types.
**Source demonstrators:** `Demonstrators/Language/casts_and_bitcasts.blade`.

**Fixture design:**
Two functions:
- `cast_chain(x: u32) -> u32`: applies `x as u8 as u32` (round-trip truncation) and `(x as i32) as u32` (sign interpretation), returns packed pair.
- `bitcast_roundtrip(x: u32) -> u32`: `bitcast` to a `bitfield(u32)` and back, return the result.

**Planned run matrix (4 runs):**

| Run | Input        | Rationale                                                         |
| --- | ------------ | ----------------------------------------------------------------- |
| 1   | `0x00000042` | small value — truncation is lossless                              |
| 2   | `0x000001FF` | value with bit 8 set — u8 truncation drops the high byte          |
| 3   | `0x80000000` | MSB set — sign bit visible after `as i32`                         |
| 4   | `0xDEADBEEF` | arbitrary pattern — bitcast round-trip must preserve bits exactly |

---

### `hw_multi_return` _(new file)_

**Covers:** 2-value and 3-value returns, discard `_`, `bool`/`bit` flag return slots.
**Source demonstrators:** `Demonstrators/Language/pass_multi_return*.blade`.

**Fixture design:**
Functions:
- `two_vals(a: u32, b: u32) -> u32, bool`: returns `(a + b, a < b)`.
- `three_vals(a: u32, b: u32) -> u32, bool, bit`: returns `(a + b, a < b, a == b)`.
- Top-level uses `_` to discard the second return value from `two_vals` and verifies both full-capture and partial-discard paths.

Result packing: pack the three flag bits from `three_vals` into bits 0–1 of the low byte, and the sum result as the upper 24 bits of `rt_result` (or use byte-lane packing — document in NOTE).

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_param1` | Rationale     |
| --- | ----------- | ----------- | ------------- |
| 1   | `5`         | `10`        | `a < b` path  |
| 2   | `10`        | `5`         | `a > b` path  |
| 3   | `7`         | `7`         | `a == b` path |
| 4   | `0`         | `0`         | zero inputs   |

---

### `hw_named_args_and_const` _(new file)_

**Covers:** named-argument call syntax, local `const` initialized from runtime data.
**Source demonstrators:** `Demonstrators/Language/const_and_named_args.blade`.

**Fixture design:**
```
fn sub(a: u32, b: u32) -> u32 { return a - b; }
```
- Call with positional args: `sub(rt_param0, rt_param1)` → `r1`.
- Call with named args reversed: `sub(b=rt_param1, a=rt_param0)` → `r2` (same value as r1).
- Call with mixed: `sub(rt_param0, b=rt_param1)` → `r3` (same value).
- Local const: `const k: u32 = rt_param0 * 2;` → use `k` as an operand to confirm runtime initialization.

Result: `r1 + r2 + r3 + k` written to `rt_result`.

**Planned run matrix (3 runs):**

| Run | `rt_param0` | `rt_param1` | Rationale                       |
| --- | ----------- | ----------- | ------------------------------- |
| 1   | `20`        | `5`         | normal case                     |
| 2   | `0`         | `0`         | zero — confirms const from zero |
| 3   | `7`         | `7`         | equal args — symmetric          |

---

### `hw_leaf_call` _(new file)_

**Covers:** `leaf fn` with multiple parameters; CALLPA-based call ABI and argument transport.
**Source demonstrators:** `Demonstrators/Bugs/pass_leaf_multi_param_abi_final_asm.blade`, `pass_leaf_call_result_survives_later_call_final_asm.blade`.

**Fixture design:**
```
leaf fn add_leaf(a: u32, b: u32, c: u32) -> u32 { return a + b + c; }
```
Call from non-leaf function to exercise CALLPA path. Use all 3 parameters.

Result: `add_leaf(rt_param0, rt_param1, rt_param2)` written to `rt_result`.

**Planned run matrix (4 runs):**

| Run | params               | Rationale                   |
| --- | -------------------- | --------------------------- |
| 1   | `[1, 2, 3]`          | simple sum = 6              |
| 2   | `[0, 0, 0]`          | zero — confirms return of 0 |
| 3   | `[10, 20, 30]`       | scaled arguments            |
| 4   | `[0xFFFFFFFF, 1, 0]` | wraparound in addition      |

---

### `hw_recursive_fn` _(new file)_

**Covers:** `rec fn` with hub-based stack; base case and multi-step recursion.
**Source demonstrators:** `Demonstrators/Language/pass_recursive_fn.blade`.

**Fixture design:**
```
rec fn factorial(n: u32) -> u32 {
    if (n <= 1) { return 1; }
    return n * factorial(n - 1);
}
```
Result: `factorial(rt_param0)` written to `rt_result`.

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_result` | Rationale                        |
| --- | ----------- | ----------- | -------------------------------- |
| 1   | `0`         | `1`         | base case — n=0 → 1 (or n≤1 → 1) |
| 2   | `1`         | `1`         | base case — n=1 → 1              |
| 3   | `4`         | `24`        | multi-step recursion             |
| 4   | `6`         | `720`       | deeper recursion                 |

---

### `hw_rep_for` _(new file)_

**Covers:** `rep for(count)` (count-only) and `rep for(count) -> i` (with index variable).
**Source demonstrators:** `Demonstrators/Language/pass_rep_for.blade`.

**Fixture design:**
Two functions:
- `rep_count(n: u32) -> u32`: uses `rep for(n)` to add a constant per iteration, returns sum.
- `rep_indexed(n: u32) -> u32`: uses `rep for(n) -> i` to sum indices 0..n-1, returns sum.

Result: `rep_count(rt_param0) + rep_indexed(rt_param1)` written to `rt_result`.

**Planned run matrix (4 runs):**

| Run | `rt_param0` (count for rep_count) | `rt_param1` (count for rep_indexed) | Rationale                |
| --- | --------------------------------- | ----------------------------------- | ------------------------ |
| 1   | `5`                               | `5`                                 | normal case              |
| 2   | `0`                               | `0`                                 | zero iterations for both |
| 3   | `1`                               | `1`                                 | single iteration         |
| 4   | `8`                               | `4`                                 | asymmetric counts        |

---

### `hw_noirq` _(new file)_

**Covers:** `noirq { }` block — REP-backed uninterruptible single-pass block.
**Source demonstrators:** `Demonstrators/Language/pass_noirq_block.blade`.

**Fixture design:**
```
noinline fn with_noirq(x: u32) -> u32 {
    var result: u32 = x;
    noirq {
        result = result + 1;
        result = result * 2;
    }
    return result;
}
```
Proof of semantic correctness: the value computed inside the `noirq` block must be observable through `rt_result`. The test does not verify interrupt timing.

Result: `with_noirq(rt_param0)` written to `rt_result`.

**Planned run matrix (3 runs):**

| Run | `rt_param0` | `rt_result` | Rationale       |
| --- | ----------- | ----------- | --------------- |
| 1   | `5`         | `12`        | `(5+1)*2 = 12`  |
| 2   | `0`         | `2`         | `(0+1)*2 = 2`   |
| 3   | `10`        | `22`        | `(10+1)*2 = 22` |

---

## Stage 3 — Type, Storage, And Addressing Families

---

### `hw_enums` _(new file)_

**Covers:** qualified enum members, contextual enum literals (`.value`), open-enum round-trip cast, bare-literal peer comparison.
**Source demonstrators:** `Demonstrators/Types/pass_enums.blade`.

**Fixture design:**
Define two enums:

```
type Color = enum(u32) { red = 1, green = 2, blue = 3 };
type Mode  = enum(u8)  { a = 0, b = 1, ... };  // open
```

Functions:
- `color_to_int(c: Color) -> u32`: switch or if-else returning the backing integer.
- `mode_roundtrip(v: u8) -> u32`: `Mode m = v as Mode; return m as u8;` — open enum round-trip.
- Use contextual literal `.red` in at least one call site.

Result packing: 2-byte pack (`color_result | (mode_result << 8)`).

**Planned run matrix (4 runs):**

| Run | `rt_param0` (Color backing) | `rt_param1` (u8 mode value) | Rationale                     |
| --- | --------------------------- | --------------------------- | ----------------------------- |
| 1   | `1` (red)                   | `0`                         | qualified member              |
| 2   | `2` (green)                 | `1`                         | second member                 |
| 3   | `3` (blue)                  | `42`                        | third member; open enum value |
| 4   | `1` (red)                   | `255`                       | boundary open-enum value      |

---

### `hw_bitfields` _(new file)_

**Covers:** bitfield field read and write, `bitcast` between bitfield and `u32`.
**Source demonstrators:** `Demonstrators/Types/pass_bitfields_codegen.blade`.

**Fixture design:**
Define a simple bitfield:

```
type Flags = bitfield(u32) {
    lo: u8,    // bits 0–7
    mid: u8,   // bits 8–15
    hi: u16,   // bits 16–31
};
```

Function `pack_unpack(a: u32, b: u32) -> u32`:
- `var f: Flags = bitcast(Flags, 0);`
- Write `lo = a as u8`, `mid = b as u8`, `hi = 0`.
- Read back both fields; pack into result.

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_param1` | Rationale                    |
| --- | ----------- | ----------- | ---------------------------- |
| 1   | `0xAB`      | `0xCD`      | general                      |
| 2   | `0`         | `0`         | all-zero                     |
| 3   | `0xFF`      | `0xFF`      | all-ones in both byte fields |
| 4   | `0x01`      | `0x80`      | boundary bytes               |

---

### `hw_pointer_arithmetic` _(new file)_

**Covers:** `[*]reg u32` pointer `+`, `-`, compound `+=`/`-=`, and pointer delta.
**Source demonstrators:** `Demonstrators/Types/pass_pointer_arithmetic.blade`.
**Result packing:** relative deltas and loaded values only; never raw addresses.

**Fixture design:**
Declare a small `reg var` array of `u32`. Take a pointer to it. Advance the pointer by a runtime amount, read the element, return the element value.

```
reg var arr: [4]u32 = [10, 20, 30, 40];

noinline fn read_at(offset: u32) -> u32 {
    var p: [*]reg u32 = &arr;
    p += offset;
    return p.*;
}
```

Result: `read_at(rt_param0)` written to `rt_result`.

**Planned run matrix (4 runs):**

| Run | `rt_param0` (offset) | `rt_result` | Rationale                 |
| --- | -------------------- | ----------- | ------------------------- |
| 1   | `0`                  | `10`        | base pointer — no advance |
| 2   | `1`                  | `20`        | single-element advance    |
| 3   | `2`                  | `30`        | two elements              |
| 4   | `3`                  | `40`        | end of array              |

---

### `hw_struct_single_word` _(new file)_

**Covers:** single-word struct literal construction, field read, field write.
**Source demonstrators:** `Demonstrators/Types/pass_structs.bound.blade`, `pass_typed_struct_literals.blade`.
**Constraint:** only the single-word struct path (`type P = struct { x: u16, y: u16 }`) is proven; full struct lowering is deferred.

**Fixture design:**
```
type P = struct { x: u16, y: u16 };

noinline fn pack_point(a: u32, b: u32) -> u32 {
    var p: P = P { .x = a as u16, .y = b as u16 };
    return (p.x as u32) | ((p.y as u32) << 16);
}
```

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_param1` | Rationale                        |
| --- | ----------- | ----------- | -------------------------------- |
| 1   | `0x1234`    | `0x5678`    | general — verifies field packing |
| 2   | `0`         | `0`         | zero                             |
| 3   | `0xFFFF`    | `0xFFFF`    | saturated fields                 |
| 4   | `1`         | `0`         | asymmetric                       |

---

### `hw_hub_storage` _(new file)_

**Covers:** scalar `hub var` read/write; width-sensitive `u8`, `u16`, `u32` hub access.
**Source demonstrators:** `Demonstrators/Storage/pass_hub_var_u32_codegen.blade`, `pass_hub_var_u16_codegen.blade`, `pass_hub_var_u8_codegen.blade`.

**Fixture design:**
Three hub globals of different widths. Write `rt_param0` into each (with truncation), read back, pack into result:

```
hub var h8:  u8  = 0;
hub var h16: u16 = 0;
hub var h32: u32 = 0;
```

```
h8  = rt_param0 as u8;
h16 = rt_param0 as u16;
h32 = rt_param0;
rt_result = (h8 as u32) | ((h16 as u32) << 8) | ((h32 & 0xFF) << 24);
```

> The result layout should be chosen so that u8 and u16 truncation are both visible and distinguishable.

**Planned run matrix (4 runs):**

| Run | `rt_param0`  | Rationale                                     |
| --- | ------------ | --------------------------------------------- |
| 1   | `0x00001234` | general — u8 truncates to 0x34, u16 to 0x1234 |
| 2   | `0`          | all-zero                                      |
| 3   | `0x0000FFFF` | u8 saturates                                  |
| 4   | `0xDEADBEEF` | full u32; confirms u8/u16 truncation paths    |

---

### `hw_lut_storage` _(new file)_

**Covers:** scalar `lut var` read/write.
**Source demonstrators:** `Demonstrators/Storage/pass_lut_var_codegen.blade`, `pass_lut_var_update.blade`.

**Fixture design:**
Single `lut var`, write and read back:

```
lut var lv: u32 = 0;
lv = rt_param0;
rt_result = lv;
```

**Planned run matrix (4 runs):**

| Run | `rt_param0`  | Rationale           |
| --- | ------------ | ------------------- |
| 1   | `0x12345678` | general             |
| 2   | `0`          | zero                |
| 3   | `0xFFFFFFFF` | all-ones            |
| 4   | `0xA5A5A5A5` | alternating pattern |

---

## Stage 4 — Modules And Inline Assembly

---

### `hw_modules` _(new file)_

**Covers:** qualified import use, module re-execution, imported global persistence across repeated calls.
**Source demonstrators:** `Demonstrators/Modules/pass_imported_global_persists.blade`, `pass_named_module_import.blade`.

**Fixture design:**
Create a companion module file `hw_modules_lib.blade` with:
- A `reg var` counter initialized to 0.
- Top-level code that increments the counter on each execution.
- A function `get_counter() -> u32` that returns the current value.

Root file `hw_modules.blade`:
```
import "./hw_modules_lib.blade" as lib;
lib();          // first execution  → counter = 1
lib();          // second execution → counter = 2
rt_result = lib.get_counter();   // must return 2
```

**Planned run matrix (3 runs):**

| Run | params               | `rt_result` | Rationale                                     |
| --- | -------------------- | ----------- | --------------------------------------------- |
| 1   | `[]`                 | `2`         | counter after two executions                  |
| 2   | `[rt_param0 = 0]`    | `2`         | confirms result is independent of param       |
| 3   | `[rt_param0 = 0xFF]` | `2`         | confirms global persistence, not param-driven |

> Minimal parametrization; this fixture exists to prove module mechanics, not value diversity.

---

### `hw_asm_value` _(new file)_

**Covers:** `asm {}` (optimizable), `asm volatile {}`, `asm fn -> u32`.
**Source demonstrators:** `Demonstrators/Asm/asm_fn_basic.blade`, `io_regular_asm.blade`.

**Fixture design:**
Three separate noinline wrappers:
- `via_asm(a: u32, b: u32) -> u32`: uses `asm { ADD {a}, {b} }` and returns the result.
- `via_asm_volatile(a: u32, b: u32) -> u32`: same using `asm volatile`.
- An `asm fn add_asm(a: u32, b: u32) -> u32` that contains `ADD {a}, {b}` and returns `a`.

Result: `via_asm(rt_param0, rt_param1) + via_asm_volatile(rt_param0, rt_param1) + add_asm(rt_param0, rt_param1)`.

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_param1`  | Rationale     |
| --- | ----------- | ------------ | ------------- |
| 1   | `10`        | `20`         | general       |
| 2   | `0`         | `0`          | zero inputs   |
| 3   | `1`         | `0xFFFFFFFF` | wraparound    |
| 4   | `100`       | `200`        | larger values |

---

### `hw_asm_flag` _(new file)_

**Covers:** `asm fn -> bool@C` (flag-return path), materialization of flag result into a `u32`.
**Source demonstrators:** `Demonstrators/Asm/asm_fn_flag_return.blade`.

**Fixture design:**
```
asm fn cmp_lt(a: u32, b: u32) -> bool@C {
    CMPS {a}, {b} WC   // C set if a < b (signed), or CMP for unsigned — choose consistently
}

noinline fn flag_to_int(a: u32, b: u32) -> u32 {
    const lt: bool = cmp_lt(a, b);
    return if (lt) 1 else 0;
}
```

Result: `flag_to_int(rt_param0, rt_param1)` written to `rt_result`.

**Planned run matrix (4 runs):**

| Run | `rt_param0` | `rt_param1` | `rt_result` | Rationale             |
| --- | ----------- | ----------- | ----------- | --------------------- |
| 1   | `5`         | `10`        | `1`         | `a < b` — flag set    |
| 2   | `10`        | `5`         | `0`         | `a > b` — flag clear  |
| 3   | `7`         | `7`         | `0`         | `a == b` — flag clear |
| 4   | `0`         | `1`         | `1`         | boundary              |

---

## Deferred / Excluded

The following feature families are intentionally **not** planned as HwTest fixtures at this stage:

| Feature                                                                 | Reason for deferral                                                               |
| ----------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `comptime fn`, compile-time assertions                                  | Compile-time only; no runtime observable behavior                                 |
| `sizeof`, `alignof`, `memoryof`                                         | Better covered by non-hardware demonstrators                                      |
| Array literals and spread forms                                         | Still `xfail` in codegen                                                          |
| Array iteration (`for(array)`)                                          | Still `xfail` in codegen                                                          |
| Pointer dereference / pointer indexing final lowering                   | Still `xfail`                                                                     |
| Broad struct / union lowering                                           | Only single-word struct path is proven                                            |
| String-to-array, string-to-const-pointer coercions                      | Bound-only or `xfail`                                                             |
| `coro fn`, `yieldto`                                                    | Non-terminating; incompatible with `pass-hw` runtime model                        |
| `int1`/`int2`/`int3` interrupt handlers                                 | Cannot be invoked from terminating runtime model                                  |
| `rep loop`                                                              | Infinite loop; incompatible with `pass-hw` runtime model                          |
| Fused extended compound shifts/rotates (`<<<=`, `>>>=`, `<%<=`, `>%>=`) | Parser/lowering issue tracked in `Work/Bugs.md`                                   |
| `noinline` and `inline` as standalone families                          | Already exercised transitively in all other families                              |
| `loop`, `break`, `continue`                                             | Not directly parameterisable as standalone hardware fixture; covered incidentally |

---

## Summary Count

| Stage                  | New files         | Total planned RUNS                  |
| ---------------------- | ----------------- | ----------------------------------- |
| Stage 0                | 0 (baseline kept) | —                                   |
| Stage 1 (expansions)   | 0                 | +35 additional RUNS across 11 files |
| Stage 2 (new families) | 8                 | ~32 RUNS                            |
| Stage 3 (type/storage) | 6                 | ~24 RUNS                            |
| Stage 4 (modules/asm)  | 3                 | ~11 RUNS                            |
| **Total**              | **17 new files**  | **~100 RUNS across 29 fixtures**    |
