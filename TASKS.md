# Difference between Implementation and Docs/reference.blade

Each change set is self-contained and ends with a green `just test && just regressions`.
Sets are ordered by dependency — later sets may depend on earlier ones.

---

## CS-1: New operators — binder + IR + codegen

The parser and precedence table already handle `~`, `%`, `<<<`, `>>>`, `<%<`, `>%>`,
`and`, `or`, unary `+`, and unary `&`. The binder and IR layers do not recognise them.

### CS-1a: Unary operators `~`, `+`, `&`

- `BoundUnaryOperatorKind`: add `BitwiseNot`, `UnaryPlus`, `AddressOf`.
- `BoundUnaryOperator.Bind()`: map `Tilde` → `BitwiseNot`, `Plus` → `UnaryPlus`,
  `Ampersand` → `AddressOf`.
- `UnaryPlus` is identity on integers.
- `BitwiseNot` (`~x`): invert all bits. Valid on integer types.
- `AddressOf` (`&x`): produces `*<storage> T`. Requires the operand to be an
  addressable lvalue; report a diagnostic otherwise.
  Needs a `PointerTypeSymbol` result whose storage class comes from the variable's
  storage class.
- MIR lowering: `BitwiseNot` → `NOT` pseudo-op, `UnaryPlus` → identity copy,
  `AddressOf` → `LEA` pseudo-op (resolve during ASM emission).
- LIR lowering: `NOT` → P2 `NOT`, `LEA` → immediate address.
- Tests: accept tests for `~x`, `+x`, `&x`; reject test for `&(1+2)`.

### CS-1b: Binary operators `%`, `<<<`, `>>>`, `<%<`, `>%>`, `and`, `or`

- `BoundBinaryOperatorKind`: add `Modulo`, `ArithmeticShiftLeft`,
  `ArithmeticShiftRight`, `RotateLeft`, `RotateRight`,
  `LogicalAnd`, `LogicalOr`.
- `BoundBinaryOperator.Bind()`: map each `TokenKind` to the new kind.
  `Modulo` valid on integer pairs.
  `ArithmeticShift*` / `Rotate*` valid on integer pairs.
  `LogicalAnd` / `LogicalOr` valid on bool pairs (short-circuit in lowering).
- MIR lowering: new `MirBinaryOp` variants. `LogicalAnd`/`LogicalOr` lower to
  branch-based short-circuit (two blocks, phi-like select).
- LIR / codegen:
  `Modulo` → call a helper (P2 has no native MOD; emit a QDIV-based sequence).
  `ArithmeticShiftLeft` → `SHL` (same as logical on P2).
  `ArithmeticShiftRight` → `SAR`.
  `RotateLeft` → `ROL`. `RotateRight` → `ROR`.
- Compound assignment `%=`: already parsed; needs `Modulo` wired through
  `BindCompoundAssignment`.
- Tests: expression-level tests for each new operator.

---

## CS-2: Type system — unions, enums, bitfields

### CS-2a: Union type symbol + binding

- Add `UnionTypeSymbol` (like `StructTypeSymbol`, same offset for all fields,
  size = max member size).
- In `ResolveType`: handle `UnionTypeSyntax` → create `UnionTypeSymbol`.
- `IsAssignable`: union ↔ union by structural match (same as struct rule).
- Member access on unions: same as struct.
- Tests: declare a `type U = union { ... }`, access fields, reject wrong field names.

### CS-2b: Enum type symbol + binding

- Add `EnumTypeSymbol`: backing type, member list (name → value), `IsOpen` flag.
- `ResolveType`: handle `EnumTypeSyntax`. Evaluate member values (must be comptime
  integer constants). Auto-increment from previous + 1 when value omitted.
  `...` member sets `IsOpen = true`.
- Add `BoundEnumLiteralExpression` to the binder: resolve `.member` against an
  expected `EnumTypeSymbol` from context (assignment target type, parameter type).
  Report diagnostic when the member doesn't exist.
- `ClosedEnum.member` (qualified access via `MemberAccessExpressionSyntax` on a
  type name): resolve to the enum constant.
- `IsAssignable`: enum ↔ same enum. Open enum ↔ backing integer only through
  explicit cast. Closed enum cannot convert to/from integer without `bitcast`.
- MIR/LIR: enums lower to their backing integer; member values are immediates.
- Tests: closed enum, open enum, `.member` literal, qualified access, reject
  arithmetic on enums, reject cross-enum assignment.

### CS-2c: Bitfield type symbol + binding

- Add `BitfieldTypeSymbol`: backing type, ordered field list with bit offsets/widths.
  Compute offsets automatically (LSB → MSB, each field occupies its type's bit width).
  Report diagnostic if total bits exceed backing type width.
- `ResolveType`: handle `BitfieldTypeSyntax`.
- Member read: extract bits via shift+mask. Member write: read-modify-write.
- `IsAssignable`: bitfield ↔ same bitfield. Bitfield ↔ backing integer only
  through `bitcast`.
- MIR: field access lowers to shift+mask ops.
- LIR/codegen: use P2 `GETBYTE`/`GETNIB`/`TESTB` where field boundaries align,
  else generic shift+mask.
- Tests: declare bitfield, read/write fields, reject overflow.

---

## CS-3: Explicit conversions — `as` and `bitcast`

### CS-3a: `as` cast expression

- Add `BoundCastExpression` (source expression + target type).
- `BindExpressionCore`: handle `CastExpressionSyntax`.
- Rules: integer → integer (truncate/extend), integer ↔ open enum,
  pointer → pointer (storage must match or be explicit). Reject: struct/union
  casts, closed enum ↔ integer.
- MIR: `Cast` instruction variant (truncate / zero-extend / sign-extend).
- LIR/codegen: `AND` mask for truncation, identity for widening to u32 register.
- Tests: `x as u8`, `x as OpenEnum`, reject `x as ClosedEnum`.

### CS-3b: `bitcast` expression

- Add `BoundBitcastExpression` (source expression + target type).
- `BindExpressionCore`: handle `BitcastExpressionSyntax`.
- Validate source and target have the same bit width. Report diagnostic otherwise.
- Semantically a no-op (reinterpret bits). Lower to identity copy in MIR.
- Tests: `bitcast(Bitfield, x)`, `bitcast(ClosedEnum, x)`, reject size mismatch.

---

## CS-4: Array literals

- Add `BoundArrayLiteralExpression`: element list + optional spread flag on last
  element + result `ArrayTypeSymbol`.
- `BindExpressionCore`: handle `ArrayLiteralExpressionSyntax`.
- Infer element type from context (assignment target) or from first element.
- Validate all elements are assignable to the element type.
- Spread (`elem...`): fill remaining slots with the spread value. Report error if
  spread is not the last element.
- Empty array `[]`: requires context type; fills with `undefined`.
- MIR: sequence of store instructions to array slots.
- Tests: `[1, 2, 3]`, `[0...]`, `[1, 2...]`, reject type mismatch, reject
  spread not last.

---

## CS-5: Typed struct literals

- `BindExpressionCore`: handle `TypedStructLiteralExpressionSyntax`.
- Resolve the type name to a `StructTypeSymbol`.
- Bind each `.field = value` initializer against the struct's field types.
- Reject: unknown fields, missing fields, duplicate fields.
- Reuse existing `BoundStructLiteralExpression` with the resolved type.
- Tests: `Point { .x = 10, .y = 20 }`, reject unknown field, reject missing field.

---

## CS-6: Pointer type enrichment

The parser now produces `PointerTypeSyntax` with optional `StorageClassKeyword`,
`VolatileKeyword`, and `AlignClause`. The type system ignores these.

### CS-6a: Storage class on pointers

- `PointerTypeSymbol`: add `StorageClass` property (Reg/Lut/Hub/None).
- `ResolveType` for `PointerTypeSyntax`: read `StorageClassKeyword` and set it.
- `IsAssignable`: pointer storage classes must match (or source is `undefined`).
- `&x` result type includes the variable's storage class.
- Multi-pointer `[*]storage T` (`MultiPointerTypeSyntax`): add
  `MultiPointerTypeSymbol` with storage class + pointee type. Resolve in binder.
- Tests: `*reg u32`, `[*]hub u8`, reject `*reg` assigned to `*hub`.

### CS-6b: Const / volatile / align on pointers

- `PointerTypeSymbol`: add `IsVolatile`, `Alignment` properties.
- Const already exists; volatile and align are new.
- `IsAssignable`: `*const T` cannot be assigned to `*T` (losing const).
  `*T` can be assigned to `*const T`.
- Volatile: compiler must not elide loads/stores through volatile pointers.
  Mark MIR load/store as volatile when the source pointer is volatile.
- Align: informational; passed through to codegen for potential `RDLONG`
  alignment assumptions.
- Tests: `*reg const u32`, `*lut volatile u32`, `*hub const volatile align(4) u32`.

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

## CS-10: Named arguments

The parser produces `NamedArgumentSyntax` nodes in call argument lists.
The binder's `BindCallExpression` ignores them.

- When binding arguments, check for `NamedArgumentSyntax`. If present:
  - Resolve the name to a parameter by name.
  - Reorder arguments to match parameter order.
  - Report diagnostic for: unknown parameter name, duplicate name,
    positional after named, name conflict with positional.
- Tests: `func_2(x=10, y=20)`, `func_2(10, y=20)`, reject `func_2(y=10, x=20, y=30)`.

---

## CS-11: Local `const` declarations

The parser routes `const name: type = expr;` at statement level to a
`VariableDeclarationStatementSyntax` with `MutabilityKeyword = const`.
The binder already handles `const` globals. Verify that:

- Local `const` variables are bound with `IsConst = true`.
- Assignment to a local const reports `E0207_AssignmentToConst`.
- The initializer may be a runtime expression (unlike global const which
  requires comptime).
- Tests: `const x: u32 = param * 2;`, reject `x = 3;`.

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
