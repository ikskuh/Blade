# Open Problems and Unimplemented Things

## Type System

### T-01: No signedness tracking in the type system
The design doc distinguishes signed vs unsigned types (e.g., `u8` vs `i8`, `u32` vs `i32`)
and specifies different codegen: `ADD`/`SUB` for unsigned, `ADDS`/`SUBS`/`CMPS`/`SAR` for
signed, and `MUL` vs `MULS`. The `PrimitiveTypeSymbol` only has `IsInteger: bool` — there is
no `IsSigned`/`IsUnsigned` flag, no bit-width tracking, and the codegen always emits unsigned
instructions (e.g., `SHR` instead of `SAR` for signed shift-right). All integer types are
treated identically after binding.

**Files**: `Semantics/TypeSymbol.cs`, `IR/Asm/AsmLowerer.cs`

### T-02: `uint(N)` / `int(N)` generic-width types are parsed but not semantically supported
The parser creates `GenericWidthTypeSyntax` nodes, but the binder maps `uint` and `int` to
singleton `BuiltinTypes.Uint` / `BuiltinTypes.Int` without evaluating the width expression.
The spec says `u8` = `uint(8)` etc. and that the compiler should insert `ZEROX`/`SIGNX`
after arithmetic on sub-32-bit values. None of this happens.

**Files**: `Semantics/Binder.cs` (BindType), `Semantics/TypeSymbol.cs`

### T-03: Array types lack a size component
`ArrayTypeSymbol` stores only `ElementType` — the array size expression is parsed but not
persisted into the type. This means the compiler can't enforce bounds, compute register
allocation requirements for packed arrays, or determine the number of registers needed.

**Files**: `Semantics/TypeSymbol.cs` (ArrayTypeSymbol), `Semantics/Binder.cs`

### T-04: Packed struct field layout is not computed
`StructTypeSymbol` stores fields as a name-to-type dictionary, but does not compute bit
offsets, total width, or validate that the struct fits in a register long. The spec requires
packed structs to use `GETNIB`/`GETBYTE`/`TESTB` with compile-time-known bit positions.

**Files**: `Semantics/TypeSymbol.cs` (StructTypeSymbol)

### T-05: Packed array sub-register codegen is not implemented
The spec details how sub-long types (`bit`, `nit`, `nib`, `u8`, `u16`) pack into registers
and use `ALTGx`/`ALTSx` for indexed access. The compiler does not compute packing layouts
or emit these instructions.

**Files**: `IR/Asm/AsmLowerer.cs`

---

## Calling Conventions

### T-06: `rec fn` does not emit CALLB/RETB or hub stack operations
The `CallingConventionTier.Recursive` tier is identified, but the AsmLowerer emits a plain
`CALL` instruction with a comment. No `CALLB`/`RETB`, no PTRB-based push/pop of locals and
parameters, no hub stack frame management.

**Files**: `IR/Asm/AsmLowerer.cs` (LowerCall, LowerReturn)

### T-07: `coro fn` / `yieldto` codegen is a TODO stub
`yieldto` and `yield` in the AsmLowerer emit only `AsmCommentNode("TODO: CALLD ...")`.
No `CALLD` emission, no continuation register allocation, no initialization of continuation
addresses at startup.

**Files**: `IR/Asm/AsmLowerer.cs` (LowerYield)

### T-08: Hardware stack depth verification is not implemented
The spec mandates static verification that no call chain exceeds 8 levels for hardware-stack
calling conventions, with a compile error suggesting `rec fn` on overflow. There is no such
check anywhere in the compiler.

**Files**: `IR/Asm/CallGraphAnalyzer.cs`

### T-09: Tail call optimization is not implemented
The spec says when a function's last action is calling another function, the compiler should
emit `JMP` instead of `CALL`. No tail-call detection or optimization exists.

**Files**: `IR/Asm/AsmLowerer.cs`, `IR/Asm/CallGraphAnalyzer.cs`

### T-10: Multi-parameter calling conventions are not implemented
CALLPA/CALLPB pass only one parameter (in PA/PB). The spec describes multi-parameter
functions using global registers for the CALL tier, but the AsmLowerer only passes the first
argument and ignores the rest.

**Files**: `IR/Asm/AsmLowerer.cs` (LowerCall)

### T-11: `leaf fn` constraint is not enforced
Declaring `leaf fn` sets `FunctionKind.Leaf` and the call graph assigns `CallingConventionTier.Leaf`,
but there is no validation that the function body actually contains no calls. The spec says
any call in a `leaf fn` body is a compile error.

**Files**: `Semantics/Binder.cs`, `IR/Asm/CallGraphAnalyzer.cs`

### T-12: Return value flag annotations (@C/@Z) are parsed but not used
`ReturnItemSyntax` stores `FlagAnnotationSyntax`, and the binder's `ResolveFunctionSignatures`
reads the return type but discards the flag annotation. No `RET WC/WZ/WCZ` selection based on
the 34-bit return channel (u32 + bool@C + bool@Z). Bool returns always go through a register,
never through C/Z flags.

**Files**: `Semantics/Binder.cs` (ResolveFunctionSignatures), `IR/Asm/AsmLowerer.cs` (LowerReturn)

### T-13: `intN fn` interrupt handlers don't emit IJMP setup or yield (RESI) correctly
The AsmLowerer emits `RETI1`/`RETI2`/`RETI3` for return, but: (a) there is no code to write
the handler's entry address into `IJMPn` at startup, (b) `yield` inside `intN fn` should emit
`RESIn` not the TODO stub, (c) on final return after exit from the handler loop, the spec
requires writing the entry address back into `IRETn`.

**Files**: `IR/Asm/AsmLowerer.cs` (LowerReturn, LowerYield)

---

## Semantic Analysis

### T-14: `comptime` expressions are bound but produce BoundErrorExpression
`BindComptimeExpression` binds the body block and then returns `BoundErrorExpression`. No
compile-time evaluation is performed. `comptime fn` is parsed as a function kind but there
is no evaluator.

**Files**: `Semantics/Binder.cs` (BindComptimeExpression)

### T-15: No validation that `intN fn` are not called from code
The spec says attempting to call an `intN fn` from Blade code is a compile error. The binder
allows calling any function; no such check exists.

**Files**: `Semantics/Binder.cs` (BindCallExpression)

### T-16: `import` declarations are parsed but not implemented
`ImportDeclarationSyntax` is created but silently ignored in `BindCompilationUnit`. There is
no module system, no file resolution, no multi-file compilation.

**Files**: `Semantics/Binder.cs` (BindCompilationUnit)

### T-17: `exec_mode` declaration is not parsed or used
The spec shows `const exec_mode = .cog;` as a module-level declaration that selects COG vs
LUT exec mode. The parser handles `const Name = ...` as a type alias or comptime const,
but there is no special handling for `exec_mode` and no enum literal syntax for `.cog`.

**Files**: `Syntax/Parser.cs`, `Semantics/Binder.cs`

### T-18: Intrinsic return types are always u32
`BindIntrinsicCallExpression` returns `BuiltinTypes.U32` for all intrinsics regardless of
the actual intrinsic. For example, `@locktry` should return `bool`, `@locknew` returns a
lock ID, `@getrnd` returns `u32`, etc. No intrinsic signature table exists.

**Files**: `Semantics/Binder.cs` (BindIntrinsicCallExpression)

### T-19: No validation of intrinsic names or argument counts
Any `@foo(...)` is accepted as a valid intrinsic call. There is no table of known intrinsics,
no arity checking, and no semantic validation.

**Files**: `Semantics/Binder.cs` (BindIntrinsicCallExpression)

### T-20: Parameter storage class on function params is rejected but spec allows it
The spec example `fn read_struct(hub ptr: *MyStruct)` shows storage class on parameters
(indicating the pointer targets hub memory). The binder reports
`ReportInvalidParameterStorageClass` for any storage class on params.

**Files**: `Semantics/Binder.cs` (ResolveFunctionSignatures)

---

## Pin and Event APIs

### T-21: `pin.*` and `event.*` APIs are not implemented
The spec defines `pin.high()`, `pin.low()`, `pin.toggle()`, `pin.read()`, `pin.mode()`,
`pin.drvflag()`, `event.ct1.set()`, `event.ct1.wait()`, etc. None of these are recognized
by the compiler. They would be parsed as member-access + call expressions but there is no
binding to PASM2 instructions (`DRVH`, `DRVL`, `TESTP`, `WRPIN`, `WAITCT1`, etc.).

**Files**: `Semantics/Binder.cs`, `IR/Asm/AsmLowerer.cs`

---

## Code Generation

### T-22: Predication (cost-based) is not implemented
The spec describes cost-based analysis to choose predicated execution over branching for
if/else. The compiler always lowers if/else via branching (TJZ + JMP). No predicated IF_xx
instructions are generated from Blade-level if statements (only from inline asm or phi moves).

**Files**: `IR/Asm/AsmLowerer.cs` (LowerBranch)

### T-23: REP body length is always 0 (placeholder)
The `rep loop` and `noirq` lowering emits `REP #0, count` with a comment "body length TBD".
The actual instruction count of the body is not computed, making the emitted REP incorrect.

**Files**: `IR/Asm/AsmLowerer.cs` (LowerPseudo)

### T-24: `for (count)` lowering does not use DJNZ
The spec says `for (count) { ... }` should compile to a `DJNZ count, #loop` pattern.
The bound `BoundForStatement` takes a symbol for the variable but there is no codegen path
that emits DJNZ — it's lowered as a generic loop.

**Files**: `IR/Mir/MirLowerer.cs`, `IR/Asm/AsmLowerer.cs`

### T-25: SSA / backwards-working register allocation is not fully implemented
The spec describes an "SSA-based register allocation" with a "backwards-working register
allocator" that places parameter values directly into PA/PB/global param registers, eliding
MOVs. The MIR layer uses SSA-like value IDs, but the register allocator
(`RegisterAllocator.cs`) is a simple graph-coloring allocator that does not do backwards
parameter placement or MOV elimination.

**Files**: `IR/Asm/RegisterAllocator.cs`

### T-26: Register budget and overflow checking is not implemented
The spec says: "The compiler enforces [the 511-instruction limit] per-function and reports
overflow with a register budget." Also: "The compiler must statically partition $000..$1EF
between code and data." No such checks exist.

**Files**: `IR/Asm/RegisterAllocator.cs`, `IR/Asm/FinalAssemblyWriter.cs`

---

## Operators and Syntax

### T-27: Missing `*=` and `/=` compound assignment operators
The spec mentions multiplication and division operators, and the parser defines `*` and `/`
as binary operators, but there are no `StarEqual` or `SlashEqual` token kinds. `*=` and `/=`
are not parseable.

**Files**: `Syntax/TokenKind.cs`, `Syntax/SyntaxFacts.cs`, `Syntax/Lexer.cs`

### T-28: Tilde (`~`) bitwise NOT operator is missing
The spec's type system mentions bitwise operations and the P2 has NOT/BITNOT instructions,
but there is no `~` (bitwise complement) unary operator in the lexer or parser.

**Files**: `Syntax/TokenKind.cs`, `Syntax/SyntaxFacts.cs`

### T-29: Logical AND (`&&`) and OR (`||`) operators are missing
The parser only has bitwise `&`, `|`, `^`. There are no short-circuit logical operators.
The spec's "operators deferred to next iteration" note may cover this, but it's a gap.

**Files**: `Syntax/TokenKind.cs`, `Syntax/SyntaxFacts.cs`

---

## Diagnostics

### T-30: Diagnostics do not use T4 templates as specified in CLAUDE.md
The CLAUDE.md coding style requires diagnostics be projected from a dense definition table
via T4 text templates. Currently `DiagnosticCode.cs` is a hand-written enum and
`DiagnosticBag.cs` has hand-written report methods. No `.tt` files exist.

**Files**: `Diagnostics/DiagnosticCode.cs`, `Diagnostics/DiagnosticBag.cs`

### T-31: `HasErrors` always returns true if any diagnostic exists (including warnings)
`DiagnosticBag.HasErrors` returns `_diagnostics.Count > 0`, but there is no severity level
on `Diagnostic`. There's also no `DiagnosticSeverity` enum. All diagnostics are treated as
errors, which means warnings would block compilation once they are added.

**Files**: `Diagnostics/DiagnosticBag.cs`, `Diagnostics/Diagnostic.cs`

---

## Miscellaneous

### T-32: `const` at top level without storage class — parsing workaround
`ParseTypeAliasOrConstDeclaration` handles `const Name = expr;` (e.g., `const BIT_TICKS = comptime { ... }`)
by fabricating a `TypeAliasDeclarationSyntax` with a fake `"auto"` named type. This means
top-level constants without a storage class are misrepresented in the AST.

**Files**: `Syntax/Parser.cs` (ParseTypeAliasOrConstDeclaration)

### T-33: Enum literal syntax (`.cog`, `.lut`) is not supported
The spec uses `.cog` as a value: `const exec_mode = .cog;`. The parser has no support for
dot-prefixed enum literals outside of struct literals (`.{ ... }`).

**Files**: `Syntax/Parser.cs`

### T-34: No lut/hub storage codegen
Variables declared with `lut` or `hub` storage class are bound (and `hub`/`lut` reported as
`UnsupportedStorageClass` in some paths), but no RDLUT/WRLUT or RDBYTE/RDWORD/RDLONG
instructions are generated.

**Files**: `IR/Asm/AsmLowerer.cs`, `Semantics/Binder.cs`

### T-35: `align(N)` is parsed and stored but never used
The `VariableSymbol.Alignment` property is populated from the parser's `AlignClauseSyntax`,
but the register allocator and codegen never consult it.

**Files**: `IR/Asm/RegisterAllocator.cs`, `IR/StoragePlace.cs`

### T-36: `@(addr)` fixed address placement is partially implemented
Fixed-address registers (`@(0x1FA)` for DIRA, etc.) are passed through to `StoragePlace` with
`FixedAddress` and `StoragePlaceKind.FixedRegisterAlias`. However, the register allocator
does not reserve these addresses or prevent conflicts.

**Files**: `IR/Asm/RegisterAllocator.cs`

### T-37: Pointer dereference codegen does nothing useful
`ptr.*` is parsed and bound as `BoundPointerDerefExpression`, but the IR lowering and codegen
have no special handling for hub/lut pointer loads (`RDLONG`/`RDWORD`/`RDBYTE`). A dereference
would need to know the storage class of the pointer target.

**Files**: `IR/Mir/MirLowerer.cs`, `IR/Asm/AsmLowerer.cs`

### T-38: Register sharing across non-overlapping call graphs is not implemented
The spec says "Functions not in the same call graph share parameter registers." The register
allocator allocates independently per function but does not share slots across functions that
are never active simultaneously.

**Files**: `IR/Asm/RegisterAllocator.cs`

### T-39: `inline fn` inlining at multiple call sites is not implemented
`MirInliner.InlineMandatoryAndSingleCallsite` handles mandatory (`inline fn`) and single-call
inlining, but the "mandatory" path in the inliner only applies when a function has a single
call site. True forced inlining at multiple call sites for `inline fn` is not yet working.

**Files**: `IR/Mir/MirInliner.cs`
