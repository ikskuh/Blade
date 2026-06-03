# Reference Update

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


