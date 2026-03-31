# Current known compiler bugs

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

- Specialized `Leaf` / `SecondOrder` multi-parameter ABI lowering now keeps
  parameter 0 in `PA` / `PB` and assigns parameters `1..N` dedicated shared ABI
  slots, so specialized callers no longer drop later arguments and callees no
  longer alias all parameters onto the transport register.
