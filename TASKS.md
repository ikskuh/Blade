# Difference between Implementation and Docs/reference.blade

Each change set is self-contained and ends with a green `just test && just regressions`.
Sets are ordered by dependency — later sets may depend on earlier ones.

## CS-5: Typed struct literals

- `BindExpressionCore`: handle `TypedStructLiteralExpressionSyntax`.
- Resolve the type name to a `StructTypeSymbol`.
- Bind each `.field = value` initializer against the struct's field types.
- Reject: unknown fields, missing fields, duplicate fields.
- Reuse existing `BoundStructLiteralExpression` with the resolved type.
- Tests: `Point { .x = 10, .y = 20 }`, reject unknown field, reject missing field.

---

## CS-7: For-loop semantic rework

The parser now produces `ForStatementSyntax` with `ExpressionSyntax Iterable` and
optional `ForBindingSyntax`. The binder has a backward-compat shim. Replace it.

- `for(count)`: iterable is an integer expression → repeat body `count` times.
  Equivalent to `for(0..count)`.
- `for(count) -> index`: bind `index` as a loop variable counting `0..(count-1)`.
- `for(array) -> item`: iterable is an array → iterate elements. `item` is a
  const alias to the current element.
- `for(array) -> &item`: mutable alias (lvalue).
- `for(array) -> &item, index`: both item reference and index variable.
- Rework `BoundForStatement` to carry: iterable expression, optional item variable
  (with mutability flag), optional index variable.
- MIR lowering: expand to a counted loop with index register. Array iteration:
  compute base + index * element_size.
- Tests: `for(4)`, `for(4) -> i`, `for(arr) -> x`, `for(arr) -> &x, i`.

---

## CS-8: `asm fn` declarations

The parser emits `AsmFunctionDeclarationSyntax` but the binder ignores it.

- `CollectTopLevelFunctions`: also scan for `AsmFunctionDeclarationSyntax`.
  Create a `FunctionSymbol` with the correct kind (leaf-like, no body statements).
- `BindProgram` member switch: handle `AsmFunctionDeclarationSyntax`.
  Produce a `BoundFunctionMember` whose body is a single `BoundAsmStatement`
  containing the raw assembly text.
- Set volatility from `VolatileKeyword`.
- Wire through MIR → LIR → ASM like a regular function but with the body replaced
  by inline assembly text.
- Tests: `asm fn add(a: u32, b: u32) -> u32 { ADD {a}, {b} }`,
  `asm volatile fn nop() { NOP }`.

---

## CS-9: Character literals and string enhancements

### CS-9a: Character literals

The lexer produces `CharLiteral` tokens with a `long` value (the codepoint).
The binder doesn't handle them yet.

- `BindLiteralExpression`: handle `TokenKind.CharLiteral` → produce
  `BoundLiteralExpression` with the codepoint value and type `u32`
  (or inferred integer type from context).
- Tests: `'A'` == 65, `'\n'` == 10, `'\x41'` == 65, `'\u{1F4A9}'`.

### CS-9b: Zero-terminated strings

The lexer already handles `z"..."` — check whether the token carries a
flag or produces a distinct value. The binder should:
- Produce an array type `[N+1]u8` (N chars + NUL terminator).
- Append `\0` to the string value if not already present.
- Tests: `z"hi!"` produces a `[4]u8` with trailing NUL.

### CS-9c: String-to-array coercion

`reference.blade` shows `var a: [4]u8 = "bye!";`.
- `IsAssignable`: allow `string` → `[N]u8` when lengths match.
- Lower string literal to array of bytes.
- Tests: `var a: [4]u8 = "bye!"`, reject length mismatch.

---

## CS-12: Non-packed structs

The parser now accepts `struct { ... }` without `packed`.
The binder's `ResolveType` for `StructTypeSyntax` likely requires `packed`.

- Allow non-packed structs: fields are padded to their natural alignment.
  Compute field offsets with alignment padding.
- `packed struct`: fields are laid out without padding (existing behavior).
- Both produce `StructTypeSymbol`; add `IsPacked` flag.
- Impact on codegen: non-packed struct field access must account for padding
  in offset calculations.
- Tests: `type P = struct { x: u8, y: u32 }` — `y` at offset 4 (aligned),
  vs `type Q = packed struct { x: u8, y: u32 }` — `y` at offset 1.

---

## CS-13: `u8x4` SIMD type

`reference.blade` shows `var v: u8x4 = [1,2,3,4];`.

- Add `BuiltinTypes.U8x4` as a primitive (32-bit, not `IsInteger`).
- `IsAssignable`: `[4]u8` ↔ `u8x4` (implicit coercion both ways).
- Integer literal → `u8x4` only through array literal `[a,b,c,d]`.
- Future: swizzle operations (deferred, not in reference.blade).
- Tests: `var v: u8x4 = [1,2,3,4];`, coerce from/to `[4]u8`.

---

## CS-14: `comptime` expression evaluation

Currently `comptime { ... }` binds the body but returns `BoundErrorExpression`.

- Implement a constant-folding evaluator that walks a `BoundBlockStatement` and
  produces a compile-time value.
- Supported subset: integer arithmetic, boolean logic, if/else, local variables,
  function calls to `comptime fn`.
- Return the evaluated value as a `BoundLiteralExpression`.
- Report diagnostic for non-evaluable constructs (loops, asm, side effects).
- Tests: `comptime { return 1 + 2; }` == 3, reject `comptime { asm { NOP }; }`.

---

## CS-15: Import declarations / module system

`reference.blade` shows two import forms (both now parse correctly):

1. **File import**: `import "./path/to/file.blade" as alias;`
   Path is a string literal, `as alias` is mandatory.
   All symbols from the file become accessible as `alias.SymName`.
2. **Named module import**: `import extmod;` or `import extmod as alias;`
   Module name is an identifier (defined via CLI). `as` rename is optional.
   Symbols accessible as `extmod.SymName` or `alias.SymName`.

**Invoking module top-level code**: `alias();` calls the imported module's
top-level code as if it were a function.

The parser now handles both forms (`ImportDeclarationSyntax` with `Source`
as either `StringLiteral` or `Identifier`, optional `AsKeyword`/`Alias`).
The binder silently ignores imports.

### CS-15a: Binder stub — diagnostic for unsupported imports

- In `BindProgram`, when encountering `ImportDeclarationSyntax`, report
  a new diagnostic `W2xxx_ImportsNotYetSupported` instead of silently
  skipping.
- Tests: verify the warning is emitted for both file and named imports.

### CS-15b: Module resolution and symbol import

This is a large feature with cross-file implications:

- Compile the imported file/module as a separate `CompilationUnitSyntax`.
- Create a `ModuleSymbol` or namespace scope containing its exported
  type aliases and function symbols.
- Bind `alias.SymName` member access against the module's symbol table.
- `alias()` invocation: emit the module's top-level code at the call site
  (inline) or via a synthesized function.
- Each module has its own import table; circular imports must be detected.
- The same file may not appear in two modules.
- Defer detailed design to a separate document.

---

## CS-16: Implicit return type `void`

`reference.blade` shows `fn empty_call() { }` — no `-> void` in the signature.
If the parser already allows omitting `-> returnType` and the binder treats
missing return specs as void, this is done. Verify and add a test if missing.

---

## CS-17: Multiple return values

`reference.blade` shows `fn get_three() -> u32, bool, bit { return 100, false, 1; }`.
The parser supports `SeparatedSyntaxList<ReturnItemSyntax>` for return specs and
`SeparatedSyntaxList<ExpressionSyntax>` for return values.

- Verify binder handles multi-value returns: match count and types of return
  expressions against the return spec.
- `@reg`/`@C`/`@Z` placement annotations on return items: record them on
  `FunctionSymbol` for codegen to use when selecting WC/WZ/WCZ on `RET`.
- Callers receiving multi-value returns: how does `var x, y, z = get_three();`
  work? (May not be in reference.blade — defer if not needed.)
- Tests: 0-value, 1-value, 2-value, 3-value returns with placement annotations.

---

## Non-semantic items already complete (for reference)

These were handled in the syntax frontend refactor and need no further work:

- [x] `~` / `%` / `%=` / `<<<` / `>>>` / `<%<` / `>%>` / `...` tokens
- [x] `and` / `or` / `type` / `union` / `enum` / `bitfield` / `bitcast` / `u8x4` keywords
- [x] Character literals (`'x'`), escape sequences, `z"..."` strings
- [x] Quaternary (`0q`) and octal (`0o`) number literals
- [x] `asm { } -> name: type@Flag;` post-body output binding syntax
- [x] `for(expr) -> [&]item[, index]` binding syntax
- [x] `rep for(expr) -> binding` / `rep loop` (infinite) syntax
- [x] `type Name = ...;` alias declarations
- [x] `asm [volatile] fn name(...) -> ret { body }` declarations
- [x] Union / enum / bitfield / non-packed struct type syntax nodes
- [x] Multi-pointer `[*]` syntax
- [x] Pointer attributes (storage, const, volatile, align) in syntax
- [x] `expr as Type` cast syntax
- [x] `bitcast(Type, expr)` syntax
- [x] `.member` enum literal syntax
- [x] `[expr, expr...]` array literal syntax
- [x] `TypeName { .field = value }` typed struct literal syntax
- [x] Named argument `name = expr` syntax
