# Current known compiler bugs

## Fixed on 2026-03-30

- General-call return placement was stringing values through synthetic `g_gen_*`
  transport storage and could write the result to one register slot while callers
  read from another.
- `noinline leaf fn` could crash the compiler while tiering/lowering mixed leaf
  and inlining semantics.
- Register-backed call arguments were still lowered through redundant temporaries
  such as `MOV _rN, g_source; CALLPB _rN, ...` instead of using the canonical
  register location directly.
