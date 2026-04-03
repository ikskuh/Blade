# Full BladeValue Centralization Refactor

## Summary
Refactor the compiler so all compile-time literal values and value operations flow through `BladeValue` as the single semantic value model.

After this refactor:
- lexer tokens carry `BladeValue?` instead of raw `object?`
- binder/comptime/MIR constant folding stop reconstructing raw payloads
- unary, binary, pointer, cast, bitcast, and normalization semantics live on `BladeValue`
- `ComptimeTypeFacts` no longer owns value conversion logic
- MIR/LIR/ASM constant handling consumes `BladeValue` directly
- remaining `object?` payload placeholders are removed from value-related code paths

This refactor explicitly does **not** target parser-internal heterogeneous containers like `SeparatedSyntaxList`’s `object` storage, because those are syntax structure transport, not value payloads.

## Key Changes

### 1. Make `BladeValue` the only semantic value surface
- Keep `BladeValue` as the central typed value class and extend it in `BladeValue.Operators.cs`.
- Add a complete operation API on `BladeValue`:
  - unary: logical not, negate, bitwise not, unary plus
  - binary: arithmetic, bitwise, shifts, rotates, comparisons, logical ops
  - pointer ops: offset and difference
  - conversions: normalize/convert to target type
  - casts: `as`
  - bitcasts: `bitcast`
- Use one consistent return contract everywhere:
  - `EvaluationError TryX(..., out BladeValue result)`
  - never return raw payloads
  - use `UndefinedBehavior` for divide/mod-by-zero and similar invalid evaluations
  - use `TypeMismatch`/`Unsupported` for semantic incompatibility
- Add small typed accessors/helpers on `BladeValue` needed by the operators:
  - `TryGetBool`
  - `TryGetInteger(out long)`
  - `TryGetPointer(out uint)`
  - equality helper that compares semantic payloads, not ad-hoc `object.Equals`
- Make all operator implementations type-driven via `TypeSymbol` subclasses, not `Convert.*` or scattered switch logic.

### 2. Remove raw payload reconstruction from frontend and comptime evaluation
- Change `Token` to carry `BladeValue? Value`.
- Change `Lexer.MakeToken` overloads to accept `BladeValue?` and emit typed literals directly:
  - integer/char literals become `ComptimeBladeValue(IntegerLiteralTypeSymbol, long)`
  - string literals become `ComptimeBladeValue(StringTypeSymbol, string)`
  - `true`/`false` become `RuntimeBladeValue(BoolTypeSymbol, bool)`
  - `undefined` becomes `BladeValue.Undefined` if the lexer emits a value for it, otherwise binder must not synthesize via raw payloads
- Simplify `Binder.BindLiteralExpression` to use `literal.Token.Value` directly, with zero raw `object?` handling.
- Delete value-conversion responsibilities from `ComptimeTypeFacts`.
  - Keep only non-value analysis there if still needed, such as pointer participation checks.
  - If a helper remains, it must not accept or return raw payloads.
- Rewrite `ComptimeEvaluator` to use `BladeValue` operations directly:
  - `TryEvaluateUnary` calls `BladeValue.TryLogicalNot` / `TryNegate` / `TryBitwiseNot` / `TryUnaryPlus`
  - binary evaluation calls `BladeValue.TryBinary`
  - conversion/cast/bitcast paths call `BladeValue.TryConvert` / `TryCast` / `TryBitCast`
- Eliminate any `TryCreateBladeValue(object?, TypeSymbol, ...)` helper entirely.

### 3. Centralize constant folding on `BladeValue`
- Rewrite MIR constant propagation so it folds to `BladeValue` directly:
  - `TryFoldUnary`, `TryFoldBinary`, `TryFoldPointerOffset`, `TryFoldPointerDifference` return `BladeValue`
  - `TryCreateConstantInstruction` takes `BladeValue?`, not `object?`
- Remove all local raw-payload extraction helpers in MIR const-prop except typed `BladeValue` readers that are also candidates to move into `BladeValue`.
- Keep `MirConstantInstruction` as `BladeValue?` only if placeholder constants still exist.
- If placeholder `const null` is still required for aggregate shell construction, make that explicit in MIR as a distinct placeholder concept rather than a fake raw payload.
- `MirLowerer` default-value emission must create typed `BladeValue` instances directly, never raw `0`/`false`.
- `LirImmediateOperand`, ASM lowering, LIR text writing, MIR text writing, and final assembly writing must only inspect `BladeValue`, never `object?`.

### 4. Narrow the legal runtime/comptime payload domain
- Align the type system with the new payload discipline:
  - integer runtime/comptime values use `long`
  - pointer values use `uint`
  - bool uses `bool`
  - string uses `string`
  - arrays use typed `IReadOnlyList<RuntimeBladeValue>`
  - aggregates use typed `IReadOnlyDictionary<string, BladeValue>`
  - `void` / `undefined` use singleton marker objects
- Update `TypeSymbol.IsLegalRuntimeObject` implementations to match the new payload domain exactly.
- Remove stale acceptance of legacy payload forms like `int`, `ushort`, `byte`, `sbyte`, etc. wherever they are no longer part of the canonical domain.
- Update `IntegerLiteralTypeSymbol` so its accepted payload shape matches the canonical integer representation for this refactor.
- Replace any remaining direct `Convert.ToInt64`, `unchecked((uint)... )`, and similar ad-hoc coercions in value-evaluation code with `BladeValue` operations.

## Public API / Type Changes
- `Token`:
  - `Value` changes from `object?` to `BladeValue?`
- `Lexer`:
  - `MakeToken(TokenKind, BladeValue?)`
- `BladeValue`:
  - grows the canonical operator/conversion API in `BladeValue.Operators.cs`
  - becomes the only value-operation authority
- `ComptimeTypeFacts`:
  - no value conversion API remains
- `MirConstantPropagation`:
  - folding helpers and constant construction use `BladeValue`, not raw payloads

## Test Plan
- Lexer/token tests:
  - literal and keyword tokens carry the expected `BladeValue` type/payload
  - non-literal tokens keep `null` value
- Binder/comptime tests:
  - bound literals preserve token `BladeValue` without reconstruction
  - comptime unary/binary/cast/bitcast/conversion behavior matches existing semantics
  - negative cases return the correct diagnostics instead of internal assertions
- MIR optimizer tests:
  - const-prop still folds arithmetic, logical, comparison, rotate, pointer offset, and pointer difference cases
  - folded constants retain correct `BladeValue.Type`
- End-to-end demonstrators:
  - strings, chars, arrays, pointers, unions/structs, multi-return, and hardware-backed bool cases all keep passing
  - add or update demonstrators specifically for:
    - typed token literal flow
    - pointer constant folding
    - cast/bitcast folding via `BladeValue`
- Acceptance:
  - focused unit tests for lexer/binder/comptime/MIR const-prop
  - representative demonstrator compiles for string, aggregate, pointer, and multi-return paths
  - full `just accept-changes`

## Assumptions
- Chosen design: `Token.Value` becomes `BladeValue?`.
- Chosen scope: delete `ComptimeTypeFacts` value-conversion logic in this refactor; it may remain only for non-value semantic analysis if still needed.
- Placeholder `object?` removal applies to value payload transport and evaluation code, not parser-internal heterogeneous node storage.
- Canonical numeric domain for this pass is `long` for integers and `uint` for pointers, matching the current `IntegerTypeSymbol` direction.
