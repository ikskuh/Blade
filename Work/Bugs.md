# Current known compiler bugs

## Struct Literal Lowering Is Missing

- Date: 2026-03-20
- Symptom: A regular struct literal that is valid per [Docs/reference.blade](Docs/reference.blade) binds successfully but fails later with `E0401_UnsupportedLowering` for opcode `structlit`.
- Regression fixture: `Demonstrators/Bugs/struct_literal_lowering.blade`
- Notes: This was discovered while trying to use a struct literal in a binder-coverage demonstrator.

## Constant-Folded Extern Addresses Emit Invalid Final Assembly

- Date: 2026-03-20
- Symptom: `extern reg var name: u32 @(comptime-expression);` binds successfully, but some folded address forms currently reach FlexSpin as invalid tokens such as `x1`/`xFF`.
- Regression fixture: `Demonstrators/Binder/pass_constant_int_paths.bound.blade`
- Notes: This came up while exercising Binder constant-int evaluation through `@(...)` clauses for coverage. The current coverage fixture disables FlexSpin so the Binder path can still contribute coverage.
