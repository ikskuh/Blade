# Difference between Implementation and Docs/reference.blade

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

### CS-9d: String-to-pointer coercion

- `reference.blade` shows `str = "hello";`.
- `reference.blade` shows `str = z"hello";`.
- `IsAssignable`: allow `string` → `[*]<storage> const u8`
- Forbid assignment to mutable pointer `string` → `[*]<storage> u8`.
- Tests:
  - positive:
    - `var hstr: [*]hub const u8 = "hub string";`
    - `var lstr: [*]lut const u8 = "lut string";`
    - `var rstr: [*]reg const u8 = "reg string";`
  - negative (requires new diagnostic "string cannot be assigned to non-const pointer")
    - `var hstr: [*]hub u8 = "hub string";`
    - `var lstr: [*]lut u8 = "lut string";`
    - `var rstr: [*]reg u8 = "reg string";`

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

## CS-18: external variables

`reference.blade` shows `extern reg var ext_var;`

TODO: Write task description

---

## CS-19: "builtin" module

- Global module `builtin` which is always available
- Forbidden to name your own module `builtin` (yields command line error)
- Compiler provides a default implementation for this module
- Contains:
  - `MemorySpace` enumeration (see `type MemorySpace = enum` in `reference.blade`)

---

## CS-20: Query operators sizeof, alignof, memoryof

Implement the three new operators:

- `sizeof`: Returns the storage size of a `var` or `const` declaration or of a type.
- `alignof`: Returns the memory alignment of a `var` or `const` declaration or of a type.
- `memoryof`: Returns the memory space of a `var` or `const` declaration.

The legal forms are

- `sizeof(decl)`: Returns the storage size of a declaration relative to its memory space.
- `sizeof(T, builtin.MemorySpace)`: Returns the size of a type if stored in the given memory space.
- `alignof(decl)`: Returns the memory alignment of a declaration relative to its memory space.
- `alignof(T, builtin.MemorySpace)`: Returns the memory alignment of a type if stored in the given memory space.
- `memoryof(decl)`: Returns the memory space of a declaration.

Important things to keep in mind:

- `memoryof` doesn't make sense for types, only for declarations.
- `sizeof` and `alignof` depend on the memory space:
  - Registers and LUT only allow 32-bit addressing, so everything <= 32 bit has size 1 and alignment 1.
    - `sizeof(u8, .reg) == 1`, `align(u8, .reg) == 1`
    - `sizeof(u16, .reg) == 1`, `align(u16, .reg) == 1`
    - `sizeof(u32, .reg) == 1`, `align(u32, .reg) == 1`
  - Hub is 8-bit addressed, so this is a more typical memory model with byte-alignments:
    - `sizeof(u8, .hub) == 1`, `align(u8, .hub) == 1`
    - `sizeof(u16, .hub) == 2`, `align(u16, .hub) == 2`
    - `sizeof(u32, .hub) == 4`, `align(u32, .hub) == 4`

---

## CS-21: "assert" statement

Implement the `assert` statement which performs compile-time assertions.

Check `reference.blade` chapter "Assertions" to see how to use it.

Two forms are legal:

- `assert <condition>;`: Fails with a generic "assertion <condition> failed" compiler diagnostic
- `assert <condition>, <message>;`: Fails with a specific "assertion <condition> failed: <message>" compiler diagnostic.

Both forms use the same diagnostic, just with a different message.

- `<condition>` must be a boolean compile-time evaluated value
- `<message>` must be a string literal (it cannot be a variable reference)

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
