# Current known compiler bugs

## Boolean Keywords Lose Literal Values During Lexing

- Date: 2026-03-21
- Symptom: `true` and `false` were tokenized as keywords without a boolean payload, so later binding produced `BoundLiteralExpression` nodes of type `bool` with a `null` value.
- Location: `Blade/Syntax/Lexer.cs` — `ReadIdentifierOrKeyword` returned `MakeToken(kind)` for all keywords instead of assigning `true`/`false` values to the boolean keywords.
- Impact: Compile-time evaluation of boolean literals could misbehave or report misleading follow-on diagnostics such as `expected 'bool', got 'bool'`.

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

## Function Names That Match PASM Keywords Emit Invalid Labels

- Date: 2026-03-22
- Symptom: A valid Blade function such as `rec fn step() -> u32` lowers to a bare `step` label in final PASM, which FlexSpin tokenizes as the `STEP` instruction instead of an identifier.
- Impact: Final assembly validation fails even though the source program is otherwise valid.
- Notes: This surfaced while adding recursive calling-convention coverage for BUG-4. The demonstrator was renamed, but the label-escaping issue remains open.
