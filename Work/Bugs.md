# Current known compiler bugs

All open bug entries were moved into `TASKS.md` (see `Bug Fix Backlog`, `BUG-3` through `BUG-9`) on 2026-04-08.

Newly discovered issues should be logged here first (as a short repro + description), then transported into `TASKS.md`
once they become scheduled work items.

- Inline asm current-address operands (`$` / `#$`) were lowered correctly but emitted as the sanitized label `l_` in final assembly instead of literal `$`. Repro: `Demonstrators/Asm/asm_current_address_rep_loop.blade`.
- `extern var foo: u32;` at module scope was incorrectly treated as a global declaration instead of an automatic top-level variable, so it compiled instead of producing an extern-on-automatic diagnostic. Repro: `Demonstrators/Bugs/illegal_extern_var.blade`.
- Separating the `three_ret(...)->(..., bool, bool)` outputs in `RegressionTests/HwTest/hw_multi_return.blade` into independent runtime result lanes makes the `a == b` lane evaluate as `1` for non-equal inputs, while the original packed expression passes. Repro: temporarily assign `eq3_u` directly to `rt_result2` in that fixture.
- Splitting the packed output in `RegressionTests/HwTest/hw_struct_{hub,lut,reg}_ping_pong.blade` into separate runtime result writes causes the fixture to hang on hardware for input `0xFFFFFFFF`, while the original packed expression passes. Repro: write the snapshot fields to `rt_result`, `rt_result1`, ... instead of packing them into one scalar.
- Flag-backed bool values flowing into aggregate writes are materialized too late in ASM lowering, so `structlit`, `insert.member`, and matching 1-bit bitfield inserts can store the wrong 0/1 value into struct fields. Repro: `RegressionTests/HwTest/hw_struct_literal_lowering.blade` and `RegressionTests/HwTest/hw_struct_{hub,lut,reg}_ping_pong.blade`.
