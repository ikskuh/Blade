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

## Comptime Bool Normalization Can Throw in `TryNormalizeValue`

- Date: 2026-03-22
- Symptom: `ComptimeTypeFacts.TryNormalizeValue` converts arbitrary `IConvertible` values to `Int64` without guarding conversion exceptions when target type is `bool`.
- Location: `Blade/Semantics/ComptimeEvaluation.cs`.
- Impact: Overflow/format conversion issues can escape as runtime exceptions instead of cleanly returning `false` and producing diagnostics.

## Diagnostic formatter hardcodes `E` prefix for all severities

- Date: 2026-03-22
- Symptom: `Diagnostic.FormatCode()` always returns `E####`, regardless of whether a diagnostic is an error, warning, or info.
- Location: `Blade/Diagnostics/Diagnostic.cs` (`FormatCode`).
- Impact: warning/info diagnostics will be mislabeled as errors in CLI and JSON consumers if non-error diagnostics are added.

## Release fallback can silently force extra call result into FlagC

- Date: 2026-03-22
- Symptom: `LirLowerer.GetExtraResultPlacement` returns `ReturnPlacement.FlagC` after `Debug.Fail` when an extra result lookup fails.
- Location: `Blade/IR/Lir/LirLowerer.cs`.
- Impact: in release builds this can silently produce incorrect ABI lowering for extra return values.

## Unicode Escape Can Crash Lexer Instead of Reporting Diagnostic

- Date: 2026-03-22
- Symptom: A string containing `\u{...}` with an out-of-range Unicode scalar value (e.g. `\u{110000}`) can throw during lexing.
- Location: `Blade/Syntax/Lexer.cs` — `ReadEscapeSequence` returns unchecked codepoints; `ReadString` forwards them to `char.ConvertFromUtf32`.
- Impact: Compiler can terminate with an exception rather than emit `E0006_InvalidEscapeSequence`.

## Duplicate Variable Clauses Are Silently Accepted With Last-One-Wins Semantics

- Date: 2026-03-22
- Symptom: Parser accepts repeated `@(addr)`, `align(n)`, or `= initializer` clauses in one variable declaration and silently overwrites prior clause values.
- Location: `Blade/Syntax/Parser.cs` — `ParseVariableDeclaration` loop updates single `atClause` / `alignClause` / `initializer` slots with no duplicate diagnostics.
- Impact: User mistakes are masked and declarations can be interpreted differently than intended.

## Fuzz Crash Fixtures Still Trigger Compiler Exceptions

- Date: 2026-03-22
- Symptom: the new `.blade.crash` regression-fixture path exposes several existing compiler crashes in `RegressionTests/Fuzzing/issue-00001.blade.crash` through `issue-00005.blade.crash`.
- Observed failures:
  `issue-00001`: `Debug.Fail` in assignment target handling (`assignment.Target is BoundSymbolAssignmentTarget`).
  `issue-00002`: `The input string '\u0000' was not in a correct format.`
  `issue-00003`: `Debug.Fail` in top-level variable registration (`DeclareTopLevelVariables must register every top-level variable before binding.`).
  `issue-00004`: same top-level variable registration assertion as `issue-00003`.
  `issue-00005`: `Instruction '_AITX' operand 1 does not support immediate syntax.`
- Impact: regression harness now correctly reports these as failures, which means the compiler still crashes on some fuzz-discovered inputs instead of degrading to diagnostics.
