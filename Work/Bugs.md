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

## `@reg` Flag Annotation Fails to Parse on Return Items

- Date: 2026-03-21
- Symptom: `fn foo() -> u32@reg, bool@C, bit@Z` fails with `E0101: Expected 'Identifier', got 'reg'`. The `@C` and `@Z` annotations work because `C`/`Z` are plain identifiers, but `reg` is lexed as `TokenKind.RegKeyword`.
- Location: `Blade/Syntax/Parser.cs:303` — `MatchToken(TokenKind.Identifier)` rejects keyword tokens in flag annotation position.
- Reference: `Docs/reference.blade:54` defines `fn get_three_specific() -> u32@reg, bool@C, bit@Z` as valid syntax.
- Fix: Accept keyword tokens (at minimum `reg`) in the flag annotation position of `ParseReturnItem`.
