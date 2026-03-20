# Difference between Implementation and Docs/reference.blade

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

`reference.blade` models `extern` as a top-level storage declaration:

- `extern reg var ext_var: u32;`
- `extern lut var ext_lut_var: u32;`
- `extern hub var ext_hub_var: u32;`
- `extern reg var OUTA: u32 @(0x1FC);`
- `extern reg const INA: u32 @(0x1FE);`

Important current-state note: the compiler already has partial support for this.

- The parser already accepts `extern`.
- `VariableSymbol` already records `IsExtern` and `FixedAddress`.
- MIR/ASM already distinguish `ExternalAlias` and `FixedRegisterAlias`.
- There are already IR tests proving `extern reg var FOO: u32;` and
  `extern reg var OUTA: u32 @(0x1FC);` lower correctly.

The remaining task is to make the feature description match the current compiler
and the reference precisely:

- `extern` remains valid only on top-level storage declarations. Local `extern`
  stays rejected with `E0216`.
- For `extern reg`:
  - no storage is allocated in the generated data section;
  - the symbol is emitted as a bare assembler symbol;
  - `@(addr)` turns the declaration into a fixed register alias;
  - `const` still participates in the normal assignment checks, so writes to
    `extern reg const` must fail.
- `extern reg` must keep normal variable semantics in the binder:
  reads, writes, address-of, and type checking should behave like a global
  register variable, only without owned storage allocation.

Full reference parity for `extern lut` / `extern hub` is blocked by the broader
global-storage gap:

- The current compiler still rejects top-level `lut` / `hub` storage with `E0218`.
- Do not describe this task as "just add extern everywhere"; the implementation
  must either:
  - explicitly scope `CS-18` to finishing and locking down `extern reg`, or
  - treat `extern lut` / `extern hub` as dependent on the later work that adds
    non-register global storage to the backend.

Tests to require:

- `extern reg var FOO: u32;` lowers to direct symbol use with no allocated `LONG`.
- `extern reg var OUTA: u32 @(0x1FC);` lowers as a fixed register alias.
- `extern reg const INA: u32 @(0x1FE); INA = 1;` is rejected as assignment to const.
- `fn f() { extern reg var x: u32; }` still reports `E0216`.

---

## CS-19: "builtin" module

`reference.blade` defines a compiler-provided module named `builtin` and uses it
as `builtin.MemorySpace`.

This task is not just "add a module entry". On the current compiler it requires
connecting the existing import/module machinery to compiler-synthesized content.

- The compiler must force the module into existence even when the user does not
  pass `--module=...`.
- However, `builtin` is not automatically visible. Access requires an explicit
  `import builtin;` or `import builtin as alias;`.
- This keeps the module compiler-provided without creating a magic always-in-scope
  global/module name.
- `--module=builtin=...` must be rejected in `CompilationOptionsCommandLine`.
  The name is reserved for the compiler-provided module.
- `import builtin;` and `import builtin as alias;` should bind to the synthesized
  builtin module instead of trying to resolve a filesystem or CLI module entry.

Current implementation detail that matters:

- Named/file imports already bind to `ImportedModule` / `ModuleSymbol`.
- Module member access already resolves exported functions, variables, and nested
  modules.
- The missing piece for `builtin.MemorySpace` is exported **types** and qualified
  **type syntax**. `TypeSyntax` currently only supports a single identifier
  (`NamedTypeSyntax`), so `builtin.MemorySpace` cannot be written in type
  position yet.

Implementation shape:

- Synthesize an `ImportedModule` for `builtin` before or during `BindImports`.
- Give it empty top-level code and no exported functions/variables.
- Export a single type:
  - `MemorySpace = enum(u32) { reg, lut, hub };`
- Extend type parsing/binding so a qualified type name like `builtin.MemorySpace`
  resolves against a module's exported types. A dedicated syntax node such as
  `QualifiedTypeSyntax` is cleaner than trying to reuse expression syntax here.

This task exists mainly to unblock `CS-20`.

Tests:

- `var cls: builtin.MemorySpace = .reg;` fails to bind as "builtin" is not imported.
- `import builtin; var cls: builtin.MemorySpace = .hub;` binds successfully.
- `import builtin as b; var cls: b.MemorySpace = .lut;` binds successfully.
- `--module=builtin=mods/ext.blade` fails with a command-line error.

---

## CS-20: Query operators sizeof, alignof, memoryof

`reference.blade` adds three compile-time query operators:

- `sizeof(decl)`
- `sizeof(T, builtin.MemorySpace.hub)` and `sizeof(T, .hub)`
- `alignof(decl)`
- `alignof(T, builtin.MemorySpace.hub)` and `alignof(T, .hub)`
- `memoryof(decl)`

On the current compiler this is a frontend + binder feature, not a backend one.
The operators should fold directly to constants during binding.

Parser work required:

- Add lexer/parser support for the keywords `sizeof`, `alignof`, and `memoryof`.
- Do not model them as generic function calls. The forms are not ordinary calls
  because the first argument may be a `TypeSyntax`, not an `ExpressionSyntax`.
- Add dedicated syntax nodes, similar in spirit to `BitcastExpressionSyntax`:
  - `sizeof` / `alignof` need two forms:
    - declaration operand
    - type + memory-space operand
  - `memoryof` only accepts a declaration operand

Binder work required:

- `sizeof` and `alignof` return an integer-literal-typed constant, not a forced
  `u32` value. This keeps them usable in normal integer-literal inference
  contexts without extra casts.
- `memoryof` returns `builtin.MemorySpace`, so `CS-19` is a dependency.
- The result should be a `BoundLiteralExpression` (or another trivially constant
  bound node) so later phases see a plain constant.
- The declaration operand should resolve to a declaration with a defined storage
  class. This is important on the current compiler:
  - top-level `reg` / `extern reg` declarations are well-defined;
  - top-level `lut` / `hub` will only work once those storage classes are supported;
  - automatic locals do **not** have a stable source-level memory space and should
    be rejected instead of guessed.
- `memoryof(type)` is invalid by definition and should report a dedicated diagnostic.

Do not reuse `TypeFacts.TryGetSizeBytes` / `TryGetAlignmentBytes` as-is.
Those helpers currently encode the byte-addressed hub model only. This feature
needs memory-space-aware queries, for example:

- register / LUT space are word-addressed:
  - `sizeof(u8, .reg) == 1`
  - `sizeof(u16, .reg) == 1`
  - `sizeof(u32, .reg) == 1`
  - `alignof(u8, .reg) == 1`
  - `alignof(u16, .reg) == 1`
  - `alignof(u32, .reg) == 1`
- hub space is byte-addressed:
  - `sizeof(u8, .hub) == 1`
  - `sizeof(u16, .hub) == 2`
  - `sizeof(u32, .hub) == 4`
  - `alignof(u8, .hub) == 1`
  - `alignof(u16, .hub) == 2`
  - `alignof(u32, .hub) == 4`

Recommended implementation shape:

- Introduce helpers like `TryGetStorageSize(TypeSymbol type, VariableStorageClass storage, out int size)`
  and `TryGetStorageAlignment(...)` rather than overloading the existing byte-based helpers.
- Map `builtin.MemorySpace.reg|lut|hub` to `VariableStorageClass.Reg|Lut|Hub`.
- Keep the operators compile-time only; there is no MIR/LIR/ASM lowering work if
  binding always folds them.

Tests:

- `sizeof(ext_var)` and `alignof(ext_var)` on `extern reg var ext_var: u32;`
- `sizeof(u32, builtin.MemorySpace.hub) == 4`
- `sizeof(u32, .reg) == 1`
- `alignof(u16, .hub) == 2`
- `memoryof(rv_01) == .reg`
- reject `memoryof(u32)`
- reject queries on automatic locals whose memory space is not defined

---

## CS-21: "assert" statement

`reference.blade` defines `assert` as a statement-like compile-time check:

- `assert <condition>;`
- `assert <condition>, "message";`

This needs explicit syntax support. The current lexer/parser do not know the
`assert` keyword and there is no statement node for it.

Syntax/frontend work:

- Add `assert` as a keyword token.
- Add `AssertStatementSyntax`.
- Parse both legal forms in statement position, so `assert` works both at
  top-level and inside function bodies.
- Parse the optional message as a string literal token, not as a general
  expression. The reference explicitly says the message cannot be a variable
  reference, so the syntax should enforce that directly.

Binder/semantic work:

- `assert` is compile-time only. It should not survive into MIR.
- Bind the condition as an expression and evaluate it immediately.
- The condition must fold to a boolean constant.
- Reuse or generalize the current constant-evaluation helpers. `TryEvaluateConstantInt`
  is too narrow for this task; `assert` needs boolean results, and `CS-20` query
  operators should be usable inside assertions.
- When the condition is `false`, emit one diagnostic code for assertion failure:
  - without message: `assertion failed`
  - with message: `assertion failed: <message>`
- Non-constant conditions should report a separate diagnostic instead of silently
  becoming runtime checks.
- Non-boolean conditions should still use normal type checking.

Recommended implementation shape:

- Introduce a bound statement node for `assert` only if it helps the binder
  pipeline; otherwise the binder can consume the syntax and emit either nothing
  or an error immediately.
- Prefer a reusable `TryEvaluateConstantValue(BoundExpression, out object?)`
  helper over baking the logic into the `assert` path. That helper will also be
  useful for future `comptime` work.

Tests:

- `assert true;` produces no diagnostics.
- `assert false;` produces the generic assertion-failed diagnostic.
- `assert false, "must hold";` produces the same diagnostic code with the custom message.
- `assert 1;` reports a type mismatch against `bool`.
- `const msg: [4]u8 = "oops"; assert false, msg;` is rejected because the message is not a string literal.
- `var x: u32 = 1; assert x == 1;` is rejected because the condition is not compile-time constant.

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
