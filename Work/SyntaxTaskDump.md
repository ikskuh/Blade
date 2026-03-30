You are implementing a set of language changes to the Blade compiler located at /home/felix/projects/nerdgruppe/blade/. Here is the complete list of changes to make, organized by file.

## Overview of changes

**Tier 1: Add E0259 - non-call expression statements are invalid**
**Tier 2: Remove postfix `++`/`--` entirely**
**Tier 3: Remove prefix `*` dereference from expressions**
**Tier 4: Replace range operator `..` with `..=` (inclusive) and `..<` (exclusive); add range support to regular `for` loops**

The user wants DELETION-FIRST changes. Remove things, fix all callsites, don't add shims.

---

## File-by-file changes

### 1. `Blade/Syntax/TokenKind.cs`
- Remove `PlusPlus, // ++` and `MinusMinus, // --`
- Add `DotDotEqual, // ..=` and `DotDotLess, // ..<` (near DotDot)

### 2. `Blade/Syntax/Lexer.cs`
For `case '+'`: Remove the line `if (Current == '+') { Advance(); return MakeToken(TokenKind.PlusPlus); }`
For `case '-'`: Remove the line `if (Current == '-') { Advance(); return MakeToken(TokenKind.MinusMinus); }`
For `case '.'`: After checking for `...`, add handlers for `=` and `<` before the plain DotDot fallback:
```csharp
if (Current == '.') { Advance(); return MakeToken(TokenKind.MinusMinus); }  // REMOVE THIS
```
The dot case should become:
```csharp
case '.':
    Advance();
    if (Current == '.')
    {
        Advance();
        if (Current == '.') { Advance(); return MakeToken(TokenKind.DotDotDot); }
        if (Current == '=') { Advance(); return MakeToken(TokenKind.DotDotEqual); }
        if (Current == '<') { Advance(); return MakeToken(TokenKind.DotDotLess); }
        return MakeToken(TokenKind.DotDot);
    }
    return MakeToken(TokenKind.Dot);
```
(DotDot remains for error recovery but is no longer a range operator)

### 3. `Blade/Syntax/SyntaxFacts.cs`
- In `TokenKindToText`: Remove `TokenKind.PlusPlus => "++"` and `TokenKind.MinusMinus => "--"`. Add `TokenKind.DotDotEqual => "..="` and `TokenKind.DotDotLess => "..<"`.
- In `GetUnaryOperatorPrecedence`: Remove `TokenKind.Star` from the list.
- Keep `DotDot => ".."` in text representations for error reporting.

### 4. `Blade/Syntax/Nodes/ExpressionSyntax.cs`
- Remove the entire `PostfixUnaryExpressionSyntax` class.
- In `RangeExpressionSyntax`: Add `public bool IsInclusive { get; }` property and update the constructor to take a `bool isInclusive` parameter. Store it.

### 5. `Blade/Syntax/Parser.cs`
- In `ParsePostfixExpression`: Remove the `case TokenKind.PlusPlus or TokenKind.MinusMinus:` branch.
- In `ParseBinaryExpression`: Change the range expression check from `TokenKind.DotDot` to handle both `DotDotEqual` and `DotDotLess`. Pass `isInclusive = (Current.Kind == TokenKind.DotDotEqual)` to the new `RangeExpressionSyntax` constructor. Example:
```csharp
if (Current.Kind is TokenKind.DotDotEqual or TokenKind.DotDotLess && parentPrecedence == 0)
{
    bool isInclusive = Current.Kind == TokenKind.DotDotEqual;
    Token dotDot = NextToken();
    ExpressionSyntax right = ParseBinaryExpression(1);
    left = new RangeExpressionSyntax(left, dotDot, right, isInclusive);
    continue;
}
```

### 6. `Blade/Diagnostics/DiagnosticCode.cs`
Add:
```csharp
E0259_ExpressionNotAStatement = 259,
E0260_RangeIterationRequiresBinding = 260,
```

### 7. `Blade/Diagnostics/DiagnosticBag.cs`
Add two methods:
```csharp
public void ReportExpressionNotAStatement(TextSpan span)
{
    Report(DiagnosticCode.E0259_ExpressionNotAStatement, span, "An expression is not a statement. Only function calls are allowed as standalone statements.");
}

public void ReportRangeIterationRequiresBinding(TextSpan span)
{
    Report(DiagnosticCode.E0260_RangeIterationRequiresBinding, span, "Range iteration requires an index binding '-> index'.");
}
```

### 8. `Blade/Semantics/Bound/BoundExpressions.cs`
- In `BoundUnaryOperatorKind` enum: Remove `PostIncrement` and `PostDecrement`.
- In `BoundUnaryOperator.Bind()`: Remove `TokenKind.PlusPlus` and `TokenKind.MinusMinus` cases.
- In `BoundRangeExpression`: Add `public bool IsInclusive { get; }` and update constructor to take and store `bool isInclusive`.

### 9. `Blade/Semantics/Binder.cs`

**In `BindStatementNullable`, case `ExpressionStatementSyntax`:**
After binding the expression, check if it's a `BoundCallExpression`. If not, emit E0259:
```csharp
case ExpressionStatementSyntax expressionStatement:
{
    BoundExpression expr = BindExpression(expressionStatement.Expression);
    if (expr is not BoundCallExpression && expr is not BoundErrorExpression)
        _diagnostics.ReportExpressionNotAStatement(expressionStatement.Expression.Span);
    return new BoundExpressionStatement(expr, expressionStatement.Span);
}
```

**Remove the `case PostfixUnaryExpressionSyntax postfixUnary:` dispatch in `BindExpressionCore` (or wherever the switch is).**

**Remove the `BindPostfixUnaryExpression` method entirely.**

**In `BindUnaryExpression`:**
- Remove the `if (unary.Operator.Kind == TokenKind.Star)` block (prefix `*` dereference - Tier 3).
- Replace the final `Assert.Invariant(...)` / `return new BoundErrorExpression(...)` with `Assert.Unreachable()`.

**In `BindRangeExpression`:**
- Pass `IsInclusive` from the syntax to `BoundRangeExpression`.

**In the `case RepForStatementSyntax repFor:` block (around line 720):**
- When the iterable is a range: check `IsInclusive`. For inclusive ranges, normalize `end` to `end + 1` at binder level:
```csharp
if (boundRange.IsInclusive)
{
    // Normalize inclusive end to exclusive: end + 1
    end = new BoundBinaryExpression(
        end,
        BoundBinaryOperator.Bind(TokenKind.Plus)!,
        new BoundLiteralExpression(1L, range.End.Span, BuiltinTypes.U32),
        range.End.Span,
        end.Type);
}
```

**In `BindForStatement`:**
Add range iteration handling. After binding `iterable`, add:
```csharp
bool isRangeIteration = iterable is BoundRangeExpression;
```
In the binding logic:
- If `isRangeIteration` and `binding is null`: emit `ReportRangeIterationRequiresBinding` and fall through with null index.
- If `isRangeIteration` and `binding is not null`: create an `indexVariable` from `binding.ItemName.Text` (similar to `isIntegerIteration`). Range iteration does not support item variables, only index.
- Update the type check condition: `isIntegerIteration || isArrayIteration` → also allow `isRangeIteration`.

**Update the two `PostIncrement or PostDecrement` conditions** (around lines 2170 and 337 in ComptimeEvaluation). In Binder.cs there's one at ~line 2170: change it to just `BoundUnaryOperatorKind.AddressOf` (remove the postfix cases).

### 10. `Blade/Semantics/ComptimeEvaluation.cs`
Two places reference `PostIncrement or PostDecrement`:
- `AnalyzeExpression` unary case (~line 337): Change `AddressOf or PostIncrement or PostDecrement` to just `AddressOf`.
- `TryEvaluateExpression` unary case (~line 945): Same change.

### 11. `Blade/Semantics/Bound/BoundTreeWriter.cs`
- In the `case BoundRepForStatement:`: keep as-is (no change needed).
- In the range expression writer: update to show inclusive/exclusive in the output if wanted (optional but nice). The current code does `AppendLine(sb, indent, $"Range<{range.Type.Name}>")`. Update to `AppendLine(sb, indent, $"Range<{range.Type.Name}> {(range.IsInclusive ? "inclusive" : "exclusive")}")`.

### 12. `Blade/IR/Mir/MirLowerer.cs`

**In `LowerUnaryExpression`:** Remove the `PostIncrement or PostDecrement` block (lines 1255-1270).

**In `CollectAddressTakenSymbols`** (~line 266): Remove the `BoundRangeExpression range` case (or keep it but update for the new range). Actually keep it: `CollectAddressTakenSymbols(range.Start, symbols); CollectAddressTakenSymbols(range.End, symbols);` - this is fine.

**In `LowerForStatement`:** Add range iteration support. After getting `iterableValue`, add:
```csharp
if (forStatement.Iterable is BoundRangeExpression rangeExpr)
{
    // Range iteration: init index to range.Start, loop while index < range.End (exclusive, already normalized)
    MirValueId startVal = LowerExpression(rangeExpr.Start);
    MirValueId endVal = LowerExpression(rangeExpr.End);
    // ... use startVal as initial index, endVal as limit
    // inclusive ranges were already normalized to exclusive at binder level, so always use <
}
```
The range case: initialize index to `rangeExpr.Start`, loop condition is `index < rangeExpr.End` (exclusive semantics, since inclusive was normalized).

Note: `forStatement.IndexVariable` is guaranteed non-null for range iteration (enforced by binder).

The full updated `LowerForStatement` should handle three cases:
1. Array iteration (existing)
2. Integer count iteration (existing)  
3. Range iteration (new): init index to start, loop while index < end

**In `LowerRepForStatement`** (~line 962): No change needed (the binder already normalized inclusive to exclusive).

**In `LowerExpression` switch** (~line 1137): Update the `BoundRangeExpression` case if it exists.

---

## Test file changes

### `Blade.Tests/LexerTests.cs`
Remove the two `TestCase` lines:
```csharp
[TestCase("++", TokenKind.PlusPlus)]
[TestCase("--", TokenKind.MinusMinus)]
```

### `Blade.Tests/ParserTests.cs`
1. In `CallIndexAndPostfixExpressions_ParseCorrectly`: Remove `counter++` and `other--` from the input. Remove assertions for `PostfixUnaryExpressionSyntax`. Rename the test to `CallAndIndexExpressions_ParseCorrectly`. The test should only check index and call expressions.

2. Update any other tests that used `ExpressionStatementSyntax` with non-call expressions as "no diagnostics" - since now they get E0259. (Search for `AssertNoDiagnostics(diag)` near `ExpressionStatementSyntax` usage with non-call expressions.)

### `Blade.Tests/SyntaxFactsTests.cs`
Remove `Assert.That(SyntaxFacts.GetUnaryOperatorPrecedence(TokenKind.Star), Is.EqualTo(10));`

### `Blade.Tests/BoundOperatorTests.cs`
Remove the two lines:
```csharp
[TokenKind.PlusPlus] = BoundUnaryOperatorKind.PostIncrement,
[TokenKind.MinusMinus] = BoundUnaryOperatorKind.PostDecrement,
```

### `Blade.Tests/BinderTests.cs`
1. Delete entirely: `BitfieldPostIncrement_BindsThroughBitfieldTargetReadPath`
2. Delete entirely: `PostfixMemberAndInvalidTargets_CoverRemainingBranches`
3. Delete entirely: `PostfixIncrement_CoversIndexAndDerefTargetsAndRejectsBool`
4. In `ArrayLiteral_BindsFromElementInference`: Change the test to put `[1, 2, 3]` in assignment context instead:
```csharp
fn demo() -> [3]u32 {
    return [1, 2, 3];
}
```
Then check the function's return statement's expression is `BoundArrayLiteralExpression`.

---

## Demonstrator changes

### `Demonstrators/Binder/pass_postfix_targets.bound.blade`
Rewrite using `+= 1`/`-= 1` instead of `++`/`--`:
```blade
// EXPECT: pass
// STAGE: bound
// CONTAINS:
// - Member<u32> .value
// - Index<u32>
// - Deref<u32>
// NOTE:
//   Covers fusion assignment paths for member, bitfield, index, and dereference targets.

type Pair = struct {
    value: u32,
};

type Flags = bitfield (u32) {
    low: nib,
    high: nib,
};

reg var pair: Pair = undefined;

reg var flags: Flags = undefined;

fn demo(ptr: *reg u32) {
    var values: [2]u32 = undefined;

    pair.value -= 1;
    flags.high += 1;
    values[0] += 1;
    ptr.* += 1;
}
```

### `Demonstrators/Binder/pass_pointer_array_addressing.bound.blade`
Change `alias[0]++;` to `alias[0] += 1;`

### `Demonstrators/Binder/fail_operator_and_call_edges.blade`
The file currently has standalone expressions like `!1;`, `-flag;`, `~flag;`, `+flag;`, `flag++;`, `pair == values[0];` inside `fn demo()`. Since `++`/`--` are removed, `flag++;` becomes a parse error (two `+` tokens), which is different from E0259. 

After removing `++`/`--`, this file needs to be updated:
- Replace `flag++;` with something else that gives an error, e.g. just remove it since we're removing `++`. Or replace with another invalid expression statement like `1 + 1;`
- Add `E0259` to `// DIAGNOSTICS:` header for the expression statement errors
- Current DIAGNOSTICS: `E0205, E0206, E0207, E0212`
- After change: `E0205, E0206, E0207, E0212, E0259` (for `!1;`, `-flag;`, `~flag;`, `+flag;`, any other non-call expression, `pair == values[0];`)

Count the E0259 occurrences: `!1;` (E0205 type error + E0259), `-flag;` (E0205 + E0259), `~flag;` (E0205 + E0259), `+flag;` (E0205 + E0259), `pair == values[0];` (E0206? or E0205? + E0259), `1 && 2;` (E0205 + E0259). So multiple E0259 occurrences. The DIAGNOSTICS header just lists which codes appear, not how many.

Replace `flag++;` with `1 + 1;` or just remove it (since `++` is gone). Let me replace it with `flag + flag;` to test another expression type.

Updated file:
```blade
// EXPECT: fail
// DIAGNOSTICS: E0205, E0206, E0207, E0212, E0259
// NOTE:
//   Covers unary/binary type errors, not-callable expressions, call-shape edge cases,
//   and expression-not-a-statement errors.

type Pair = struct {
    value: u32,
};

fn pair_sum(x: u32, y: u32) -> u32 {
    return x + y;
}

fn multi() -> u32, bool {
    return 1, false;
}

fn demo(flag: bool, values: [2]u32, pair: Pair) {
    !1;
    -flag;
    ~flag;
    +flag;
    flag + flag;
    pair == values[0];
    1 && 2;
    values[false];
}

var extra_positional: u32 = pair_sum(1, 2, 3, y=4);
var too_many_args: u32 = pair_sum(1, 2, 3);
var not_callable: u32 = 1();
var multi_value: u32 = multi();

fn too_many_returns() -> u32 {
    return 1, 2;
}
```

Wait - `pair == values[0];` - is this an E0206 error? No, that would be E0205 (type mismatch between Pair and u32). And E0259 since it's a non-call expression statement. The `E0206` comes from `1()` (not callable). Let me keep the structure consistent.

### `Demonstrators/Binder/fail_if_range_and_bitcast_edges.blade`
Add E0259 to DIAGNOSTICS header. The `false..true;` line: with the new syntax, `..` is no longer a range operator, so this is actually a parse error now (unexpected `..` token). We need to use the new syntax instead: `false..<true;` or some other range expression used as statement.

Actually wait - `false..true;` after removing range-as-statement and changing `..` to `..=`/`..<`: the line `false..true;` would parse differently. After the lexer change, `..` remains as `DotDot` token. The parser no longer produces range expressions from `DotDot`, so `false..true` would be parsed as `false` (expression statement) then `..true` which is confusing.

Let me replace `false..true;` with `false..<true;` to keep testing the range-as-statement path with E0259.

Updated file:
```blade
// EXPECT: fail
// DIAGNOSTICS: E0205, E0259
// NOTE:
//   Covers the remaining if-expression and standalone range-expression type checks.

fn demo(flag: bool, ptr: *reg u32) {
    var chosen: *reg u32 = if (flag) undefined else ptr;
    var mismatch: u32 = if (flag) 1 else false;
    false..<true;
}
```

### New: `Demonstrators/Language/pass_call_expression_statement.blade`
```blade
// EXPECT: pass
// NOTE:
//   Verifies that a function call as a standalone statement is valid.

fn noop() {
}

fn demo() {
    noop();
}
```

### New: `Demonstrators/Language/fail_non_call_expression_statement.blade`
```blade
// EXPECT: fail
// DIAGNOSTICS: E0259
// NOTE:
//   Verifies that non-call expressions as standalone statements are rejected.

fn demo(x: u32, y: u32) -> u32 {
    x;
    x + y;
    return 0;
}
```

### `Demonstrators/Binder/fail_statement_binding_edges.blade`
The line `rep for(false..true) {` uses `..`. Since `..` is no longer a range operator but remains as a lexer token, this would cause a parse error, not the intended semantic error. Update to use `..<`:
`rep for(false..<true) {`

But wait - does `rep for(false..<true)` without binding still work (it uses a synthetic `__rep_index`)? Yes, per the current binder code. And `false..<true` would be an exclusive range with bool start/end, which should fail type checking (booleans aren't integers). So the diagnostic should still be E0205. Update the file accordingly.

### `Demonstrators/Binder/pass_loop_binding_shapes.bound.blade`
The line `rep for(1..count) -> i {` uses `..`. Change to `rep for(1..<count) -> i {` (exclusive range, same semantics as before).

---

## IMPORTANT notes

1. The `BindForStatement` range case: when the iterable is a `BoundRangeExpression` and binding is not null, create `indexVariable` from `binding.ItemName.Text`. Don't allow `itemIsMutable` for range (ranges don't have an item). If `binding.Ampersand is not null`, emit a diagnostic.

2. For the range `for` in `LowerForStatement`: Use `startVal` as initial index value instead of 0. Use `rangeExpr.End` as the loop limit. Since inclusive ranges were normalized at binder level, always use `<` comparison.

3. When calling `LowerExpression` for a range inside `LowerForStatement`: The range expression's `Start` and `End` will be lowered separately. You don't call `LowerExpression(forStatement.Iterable)` for the range case - instead access `rangeExpr.Start` and `rangeExpr.End` directly.

4. In `BoundTreeWriter.cs`, the `BoundRangeExpression` case: update the display to show inclusive/exclusive marker.

5. Make sure `CollectAddressTakenSymbols` in `MirLowerer.cs` is updated to handle the case where `forStatement.Iterable is BoundRangeExpression` (collect symbols from range start/end).

Please implement ALL of these changes now. Read each file before modifying it. Make precise, minimal edits. After all changes are done, run `just accept-changes` to verify.

When you're done, please provide a summary of all files changed.