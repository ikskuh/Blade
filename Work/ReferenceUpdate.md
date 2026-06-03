# Reference Update

## FINDING-1: Nested Block Comments Are Implemented

Description:
The language reference says Blade only supports line comments introduced with `//`, but the lexer still accepts nested block comments `/* ... */` and reports unterminated block comments.

Evidence:
- `Lexer.NextToken` dispatches `/*` to `SkipBlockComment`.
- `Lexer.SkipBlockComment` tracks nesting depth for nested block comments.

Triage: Compiler Bug

Reasoning:
The language never had block comments in the first place, the agents hallucinated this feature and implemented it.

Resolution:
Fully delete block comment lexing and the related unterminated-block-comment diagnostics.
## FINDING-2: Top-Level `assert` Not Supported

Description:
The language reference allows top-level `assert` statements, but the parser still treats them as unexpected top-level input and emits a diagnostic before falling back to statement parsing.

Evidence:
- `Parser.ParseTopLevelMember` has no `AssertKeyword` case and routes non-declarations through `ParseUnexpectedTopLevelMember`.
- `Parser.ParseUnexpectedTopLevelMember` reports `UnexpectedTokenError(..., "top-level declaration", ...)` before delegating to `ParseGlobalStatement`.
- `ParseStatement` still accepts `AssertKeyword`, so top-level `assert` is parsed only after already being diagnosed as invalid top-level syntax.

Triage: Compiler Bug

Reasoning:
Top-level `assert` is part of the language, but the top-level parser entry assumes only declarations are valid and reports an error for `assert` first.

Resolution:
Teach `ParseTopLevelMember` to accept top-level `assert` directly without first reporting an unexpected top-level token.

## FINDING-6: Obsolete Type Spellings Remain Accepted

Description:
The language reference uses omitted return types and `uN` / `iN` integer spellings, but the compiler still accepts obsolete source spellings such as `void`, `bit`, `nib`, `nit`, `uint(N)`, and `int(N)`.

Evidence:
- `SyntaxFacts` still reserves `void`, `bit`, `nit`, `nib`, `uint`, and `int` as source keywords.
- `BuiltinTypes` still exposes and binds `void`, `bit`, `nit`, and `nib` as builtin source-visible types.
- Regression coverage still exercises the obsolete forms, including `fn ... void`, `-> ... bit`, `nib`, `nit`, `uint(5)`, and `int(5)`.

Triage: Compiler Bug

Reasoning:
The reference has already moved to the narrower type surface. The compiler still carries legacy compatibility spellings that keep outdated source forms alive.

Resolution:
Remove the obsolete source spellings from lexing, parsing, builtin type lookup, and regression coverage. Keep `void` only as an internal compiler type, not a user-spellable source type.

## FINDING-7: `used` And `linkname` Attributes Are Not Implemented

Description:
The language reference documents `used` and `linkname("literal")` for functions and stored declarations, but the parser still only implements `layout(...)`, `align(...)`, `@(...)`, and initializers.

Evidence:
- `Parser.ParseFunctionMetadataProperty` only accepts `layout(...)` and `align(...)` and treats every other metadata property as unexpected.
- `Parser.ParseVariableDeclaration` only recognizes `@(...)`, `align(...)`, and `=` clauses after the type.
- No parser token or clause handling exists for source-level `used` or `linkname("literal")` attributes.

Triage: Compiler Bug

Reasoning:
The reference now specifies `used` and `linkname`, but the compiler still parses the older, smaller metadata surface.

Resolution:
Implement parsing and binding for `used` and `linkname("literal")` on functions and stored declarations, then thread them through reachability and emitted-name handling.

## FINDING-11: `rep` Loops Still Bind `continue`

Description:
The language reference says `rep` loops forbid both `break` and `continue`, but the binder only rejects `break` and still binds `continue`.

Evidence:
- `Binder.BindBreakOrContinueStatement` reports `InvalidBreakInRepLoop` only for `break`.
- MIR lowering contains dedicated continue flow for `rep for` loops.

Triage: Compiler Bug

Reasoning:
`REP`-backed loops are specified as non-interruptible by either `break` or `continue`, but the binder still permits `continue` and MIR still contains a dedicated continue path.

Resolution:
Reject `continue` inside `rep loop` and `rep for` bodies the same way `break` is already rejected, and remove the corresponding continue lowering path for REP-backed loops.


