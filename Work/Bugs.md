# Current known compiler bugs

## Open — discovered 2026-03-31 via hardware tests (continued)

### `rep for(n)` codegen is completely broken

Three separate defects in the `rep for` lowering:

1. **`REP #0` = infinite loop**: The P2 `REP` instruction treats a count of zero as
   "repeat forever". The compiler emits `REP @label, reg` without a zero-check guard,
   so `rep for(0)` (or any runtime value of zero) spins forever.

2. **Initializer placed inside REP block**: For `rep for(n) { sum = sum + 1; }`, the
   generated code places `MOV _r0, #0` (initializing `sum`) inside the repeated block
   instead of before it. Every iteration resets `sum` to 0.

3. **JMP after REP creates infinite loop for non-zero n**: The generated `JMP
   #rep_count_bb1` placed after the REP block refers to a label inside the now-
   completed REP. This causes an infinite outer loop for any n > 0.

The `rep for(n) -> i` (indexed) variant is also broken: no sum accumulation and no
index variable are emitted; the function never returns a meaningful value.

Reproducer: `Demonstrators/HwTest/hw_rep_for.blade` (marked `xfail`).

### Narrowing cast `as u8` does not emit truncation instruction

`var byte_val: u8 = x as u8` generates no `AND` or `ZEROX` instruction. The full
32-bit source value is silently kept, so `0x1FF as u8 as u32` returns `0x1FF`
instead of `0xFF`.

Reproducer: `Demonstrators/HwTest/hw_casts_and_bitcasts.blade` (marked `xfail`).

### `bitcast(i32, x) < 0` emits unsigned comparison instead of sign-bit test

The compiler lowers `signed_var < 0` (where the variable has type `i32`) with
`CMP reg, #0 WC`, which tests unsigned less-than and is never true. The correct
lowering is a sign-bit test (`TESTB reg, #31 WC` or `SAR` to propagate the MSB).

Reproducer: `Demonstrators/HwTest/hw_casts_and_bitcasts.blade` (marked `xfail`).

### Multi-return bool values not captured from C/Z flags; bool→u32 if-else bodies missing

After a `CALLPA … #f_two_ret` that returns `(u32, bool)`, the caller's `bool` return
value lives in the C flag. The compiler correctly emits `BITC _rN, #0` to capture
the first bool return, but:

1. After the second `CALLPA` (discard call), the C/Z flags are overwritten. The
   compiler does not re-capture lt3/eq3 from three_ret's C/Z flags into registers.

2. The if-else branches for `var lt2_u: u32 = if (lt2) 1 else 0` have their
   assignment bodies elided — the generated `TJZ`/`JMP` structure jumps between
   empty basic blocks, so `lt2_u` remains 0 regardless of the bool value.

Result: multi-return bool flags are silently zero in the calling code.

Reproducer: `Demonstrators/HwTest/hw_multi_return.blade` (marked `xfail`).

### SSA phi-node carry for short-circuit `and`/`or` uses wrong register for new variables

When a new variable `var x: u32 = 0` is live across a short-circuit `and`/`or`
conditional, the phi-node carry in the false branch emits `IF_Z MOV _rN, _r0`
(where `_r0` holds a parameter) instead of `IF_Z MOV _rN, #0` or a correct
register hold. The variable silently acquires the parameter value in the false
branch, causing every subsequent computation on that variable to use the parameter
as the initial value instead of 0.

Example: `var b0: u32 = 0; if (a != 0 and b != 0) { b0 = 0x01; }` generates:
```
IF_Z MOV _r12, _r0   ; false branch carry — _r0 = a, should be 0
```
so `b0` = `a` (not 0) when the condition is false.

This affects all variables first defined in a block containing a short-circuit
conditional, whose initial value is not itself a register load.

Reproducer: `Demonstrators/HwTest/hw_bool_logic.blade`.

### `rec fn` codegen: saves `n-1` instead of `n` before recursive call, POPB clobbers result

In a `rec fn`, the generated code for `return n * factorial(n - 1)` does:

1. Computes `n-1` into PB (overwriting n)
2. Pushes `n-1` onto the hub stack (should push `n`)
3. Calls recursively — PB = `factorial(n-1)`
4. Pops `n-1` back into PB, clobbering the recursive result
5. Emits `QMUL PB, PB` — computes `(n-1)²` instead of `n * factorial(n-1)`

Effect: recursive multiplication computes `(n-1)^2` instead of `n!`.
Observed: `factorial(4) = 9` (= 3²), `factorial(6) = 25` (= 5²).

Reproducer: `Demonstrators/HwTest/hw_recursive_fn.blade` (marked `xfail`).

### `WRBYTE`/`RDWORD` return 0 for inputs above a certain magnitude

`WRBYTE` writes the byte and `WRBYTE`/`RDWORD` reads appear to return 0
for hub addresses used as `#label` immediates when the assembled P2 binary
has the hub data section beyond address ~220 in the combined runtime+Blade
binary. Runs with small input values (where byte/word equal the full value)
pass; runs with distinct multi-byte inputs produce wrong sums.

Observed: runs 1 (0x42) and 2 (0x1FF) pass; runs 3 (0x1234) and 4 (0xDEADBEEF)
return `x` (= RDLONG result only, RDBYTE and RDWORD apparently 0).

Root cause not yet identified. May relate to AUGS requirements for hub
addresses beyond 9-bit range, misaligned hub section placement, or a P2
hub-access timing issue.

Reproducer: `Demonstrators/HwTest/hw_hub_storage.blade` (marked `xfail`).

## Open — discovered 2026-03-31 via hardware tests

### Leaf / second-order multi-parameter ABI drops non-transport arguments — FIXED

When a `leaf` function or an auto-tiered second-order function has more than one
parameter, only parameter 0 is modeled in the specialized ABI. `CALLPA` / `CALLPB`
carry the transport argument, but parameters `1..N` are never assigned dedicated
shared storage on either the caller or callee side.

Effect: specialized calls silently drop non-transport arguments, and the callee can
alias all parameters onto the same register, producing `a op a` instead of `a op b`.

Reproducer: `Demonstrators/HwTest/hw_bitwise_u32.blade` (and hw_arithmetic_u32,
hw_shifts_u32, hw_if_expression, hw_function_calls, hw_signed_arithmetic), plus
the focused ASMIR regressions for specialized multi-parameter calls.

Generated code for `bitwise(a, b)` with a=0xAAAA, b=0x5555:

```pasm2
  CALLPA g_a, #f_bitwise   ; only PA loaded — g_b never touched
  ...
  AND PA, PA               ; a & a  (should be a & b via _r1)
  OR _r3, PA               ; a | a
  XOR _r2, PA              ; a ^ a = 0
```

### For loop index increment uses loop bound instead of 1 — FIXED

In `for(n) -> i { ... }`, the index increment step emits `ADD _r2, PA` (i += n)
instead of `ADD _r2, #1`.  The loop therefore executes at most once (0 < n, then
n >= n → exit).

Reproducer: `Demonstrators/HwTest/hw_for_loop.blade` — expected 45, got 0.

Generated code:

```pasm2
  sum_indices_bb3
    ADD _r1, _r2      ; result += i   ← correct
    ADD _r2, PA       ; i += PA (=n)  ← should be ADD _r2, #1
    JMP #sum_indices_bb1
```

## Open — discovered 2026-03-31 during `<<<` regression work

### Documented fused arithmetic-shift assignments are rejected by the parser

The language reference documents fused assignments for the extended shift and
rotate operators:

```blade
a <<<= b;
a >>>= b;
a <%<= b;
a >%>= b;
```

At least `<<<=` currently fails during parsing with `E0102: Expected
expression.` / `E0106: Invalid assignment target.` instead of producing a
compound-assignment node.

Reproducer:

```blade
reg var sink: u32 = 0;
reg var amount: u32 = 4;
sink <<<= amount;
```

## Fixed on 2026-03-30

- General-call return placement was stringing values through synthetic `g_gen_*`
  transport storage and could write the result to one register slot while callers
  read from another.
- `noinline leaf fn` could crash the compiler while tiering/lowering mixed leaf
  and inlining semantics.
- Register-backed call arguments were still lowered through redundant temporaries
  such as `MOV _rN, g_source; CALLPB _rN, ...` instead of using the canonical
  register location directly.

## Fixed on 2026-03-31

- Runtime comparison lowering now annotates branch conditions with the correct
  flag polarity (`==` => `Z`, `!=` => `NZ`, `<`/`>` => `C`, `<=`/`>=` => `NC`),
  so comparison-heavy runtime fixtures such as
  `Demonstrators/HwTest/hw_comparisons_u32.blade` no longer invert `!=`, `<=`,
  or `>=` when lowered to predicated PASM branches.
- Specialized `Leaf` / `SecondOrder` multi-parameter ABI lowering now keeps
  parameter 0 in `PA` / `PB` and assigns parameters `1..N` dedicated shared ABI
  slots, so specialized callers no longer drop later arguments and callees no
  longer alias all parameters onto the transport register.
- Specialized call results are no longer pinned to `PA` / `PB` for their full
  caller-side lifetime, so a `CALLPA` / `CALLPB` result now survives later
  specialized calls in expressions such as `max(a, b) + min(a, b)`.
- Arithmetic left shift lowering now emits `SAL` for `<<<` and `<<<=` instead of
  `SHL`, restoring the documented "shift in LSB" semantics on PASM2.
