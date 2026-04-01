# `Work/HwTestSuite.md` Plan

## Summary

Rework `Demonstrators/HwTest` around the new `RUNS` mechanism instead of adding one fixture per operator or per type. The suite should stay hardware-budget-aware: one fixture per runtime-visible language family, with multiple parameterized runs that cover normal, edge, and asymmetric cases.

The document should explicitly classify coverage into:
- `already covered by current HwTest families`
- `ready to promote from passing non-Hw demonstrators`
- `deferred because not runtime-practical or not end-to-end`

## Fixture Strategy

Use these rules throughout the plan:

- Prefer one HwTest file per feature family, not per operator.
- Prefer `3-6` runs per file; only go higher when one file can replace several otherwise-separate slow hardware tests.
- Use up to 8 runtime parameters to pack multiple subcases into one execution when the family naturally supports it, like the current comparison fixture.
- Use representative runtime types only when semantics differ materially: `u32`, `i32`, `bool`/`bit`, and storage-width-sensitive `u8` / `u16` / `u32`.
- Keep current aggregate HwTests as the model. Expand their `RUNS` coverage before introducing many new files.
- Standardize result packing in the plan:
  - scalar result: direct `rt_result`
  - bool/bit result: `0` / `1`
  - multi-check result: fixed bit packing or byte packing, documented in the fixture note
  - pointer/storage result: relative deltas or loaded values, never raw unstable addresses

## Stage 0 — Baseline And Coverage Map

- Keep `test_runner.blade` as the parameter-injection baseline using all 8 `rt_paramN` values.
- At the top of `Work/HwTestSuite.md`, add a matrix mapping each current HwTest family to the relevant section in [reference.blade](/home/felix/projects/nerdgruppe/blade/Docs/reference.blade).
- Mark each family as `broad smoke already present`, `needs more RUNS`, or `missing family`.

## Stage 1 — Strengthen Existing HwTest Families With Better `RUNS`

Expand the current files instead of splitting them:

- `hw_arithmetic_u32`: add runs for zero/identity behavior, asymmetric operands, wraparound-sensitive unsigned inputs, and divisor patterns that distinguish `/` from `%`.
- `hw_bitwise_u32`: add runs for zero, all-ones, disjoint masks, alternating masks, and self-alias cases.
- `hw_shifts_u32`: add runs for shift amount `0`, `1`, and `31`, plus values that distinguish logical vs arithmetic vs rotate behavior.
- `hw_comparisons_u32`: keep the packed multi-pair style; add runs covering `a<b`, `a>b`, `a==b`, `0`, `1`, and `0xFFFF_FFFF`.
- `hw_unary_i32`: add positive, zero, and negative inputs.
- `hw_signed_arithmetic`: add mixed-sign, both-negative, and zero-crossing cases.
- `hw_compound_assign`: keep this as one fused-assignment family and expand runs across multiple seeds; do not split into one file per operator.
- `hw_if_expression`: add less-than, greater-than, and equal-input runs.
- `hw_function_calls`: add runs that distinguish nested call ordering and argument transport.
- `hw_for_loop`: add `n=0`, `n=1`, and typical-count runs.
- `hw_while_loop`: add `n=0`, `n=1`, and typical-count runs.

## Stage 2 — Add Missing Runtime Families That Benefit From Parameterized Runs

Promote passing non-Hw demonstrators into compact HwTest families:

- `hw_bool_logic`: cover short-circuit `and`, short-circuit `or`, and boolean `!` in one file using packed result bits.
- `hw_casts_and_bitcasts`: cover explicit `as` casts, scalar `bitcast`, and signed/unsigned reinterpretation with multiple input runs.
- `hw_multi_return`: cover 2-value return, 3-value return, discard `_`, and explicit flag annotations in one family with packed outputs.
- `hw_named_args_and_const`: cover named-argument reordering plus local `const` initialized from runtime data in one file.
- `hw_leaf_call`: cover `leaf fn` multi-parameter calls and ABI-sensitive argument transport with several operand pairs.
- `hw_recursive_fn`: cover `rec fn` with small recursion depths including base case and multi-step recursion.
- `hw_rep_for`: cover `rep for(count)` and `rep for(count) -> i` with finite counts.
- `hw_noirq`: cover one observable `noirq` block outcome; the proof is semantic correctness, not timing.

## Stage 3 — Add Type, Storage, And Addressing Families Where Runtime Adds Value

Group by semantic family, not by every width/operator combination:

- `hw_enums`: cover qualified enum members, contextual literals, open-enum round-trip casts, and bare-literal peer comparison.
- `hw_bitfields`: cover aligned field read/write and bitfield round-trip through `bitcast`.
- `hw_pointer_arithmetic`: cover `[*]` pointer `+`, `-`, pointer delta, and pointer compound updates using relative differences only.
- `hw_struct_single_word`: cover the already-proven single-word struct literal / field read / field write path only.
- `hw_hub_storage`: cover scalar and indexed hub access in one file, with runs that distinguish `u8`, `u16`, and `u32` width-sensitive behavior.
- `hw_lut_storage`: cover scalar and indexed LUT access in one file.
- Keep storage families write-then-read driven so they do not depend on array literal lowering.

## Stage 4 — Modules And Inline Assembly Families

Add a small number of high-value runtime families:

- `hw_modules`: cover qualified import use, repeated module execution, and imported global persistence across repeated calls.
- `hw_asm_value`: cover plain `asm`, `asm volatile`, and `asm fn -> u32` through runtime-visible value production.
- `hw_asm_flag`: cover `asm fn -> bool@C` and any required flag-to-value materialization.

These should use parameters to vary operands, not create multiple hardware files for each asm spelling.

## Excluded / Deferred

Do not plan HwTest families yet for:

- `comptime fn`, compile-time assertions, and other compile-time-only evaluation paths
- `sizeof`, `alignof`, and `memoryof` as primary HwTest targets; they are better covered by non-hardware demonstrators
- array literals and spread forms; current coverage is still `xfail`
- array iteration over arrays; current coverage is `xfail`
- pointer dereference / pointer indexing final lowering; current coverage is `xfail`
- broad struct/union lowering beyond the single-word struct path already proven
- string-to-array and string-to-const-pointer coercions; current coverage is bound-only or `xfail`
- `coro fn`, `yieldto`, `int1` / `int2` / `int3`, and `rep loop`; these do not fit the current terminating `pass-hw` runtime model
- fused extended compound shifts/rotates (`<<<=`, `>>>=`, `<%<=`, `>%>=`) until the parser/lowering issue in [Work/Bugs.md](/home/felix/projects/nerdgruppe/blade/Work/Bugs.md) is resolved

## Defaults And Assumptions

- Hardware time is the limiting resource, so the suite should optimize for family coverage per fixture, not syntactic purity per operator.
- A new file is justified only when a feature family needs a different runtime shape, storage model, or result-packing strategy.
- Existing aggregate files remain in place and become the backbone of the suite; the plan should bias toward enriching them with more `RUNS` first.
- The final `Work/HwTestSuite.md` should name the intended fixture family for each reference feature and describe the planned run matrix, not just list feature names.
