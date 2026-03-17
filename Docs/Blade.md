# Blade As Implemented

## Snapshot

- Scope: this file records the current `Blade/` implementation, not the full target language from `Docs/blade_language_design_v3.md`.
- Bias: prefer "what the compiler actually does today" over "what the design doc intends".
- Useful reading order in code:
  - `Blade/Program.cs` -> CLI
  - `Blade/CompilerDriver.cs` -> parse -> bind -> IR pipeline
  - `Blade/Syntax/*` -> lexer/parser/AST
  - `Blade/Semantics/*` -> binder, symbols, types, inline asm validation
  - `Blade/IR/Mir/*` -> SSA-ish MIR + optimizer + inliner
  - `Blade/IR/Lir/*` -> virtual-register LIR
  - `Blade/IR/Asm/*` -> instruction selection, call graph tiering, reg alloc, legalization, final PASM2 text
  - `Blade/Diagnostics/*` -> error inventory
  - `Blade/P2InstructionMetadata.g.cs` -> generated P2 instruction metadata used by inline asm and backend logic

## CLI And Dumps

- Input model:
  - exactly one source file
  - file is read as raw text
  - diagnostics print as `path:line:col: E####: message`
- Compilation pipeline:
  - lex
  - parse
  - bind
  - build MIR/LIR/ASM only if diagnostics are empty
- Dump controls:
  - `--dump-bound`
  - `--dump-mir-preopt`
  - `--dump-mir`
  - `--dump-lir-preopt`
  - `--dump-lir`
  - `--dump-asmir-preopt`
  - `--dump-asmir`
  - `--dump-final-asm`
  - `--dump-all`
  - `--dump-dir <path>`
  - optimizer toggles:
    - `-fmir-opt=<csv>` / `-fno-mir-opt=<csv>`
    - `-flir-opt=<csv>` / `-fno-lir-opt=<csv>`
    - `-fasmir-opt=<csv>` / `-fno-asmir-opt=<csv>`

```sh
# Current dev loop
dotnet run --project Blade -- sample.blade --dump-all
```

## Surface Syntax

### Lexing

- Tokens exist for:
  - identifiers
  - integer literals: decimal, `0x...`, `0b...`, `_` separators
  - string literals: `"text"` with no escape handling
  - comments:
    - line: `// ...`
    - block: `/* ... */`
    - nested block comments are supported
- Operators and punctuation:
  - arithmetic / bitwise: `+ - * / & | ^`
  - compare: `== != < <= > >=`
  - shifts: `<< >>`
  - assignment: `= += -= &= |= ^= <<= >>=`
  - postfix: `++ --`
  - range: `..`
  - misc: `. @ # ->`
- Keywords are broad enough for the design surface, but several are only parsed or only carried as markers.

```blade
0
123_456
0xFF_00
0b1010_0101
"hello"        // tokenized as a string literal
/* outer
   /* inner */
*/
```

### Top-Level Forms

- Accepted top-level members:
  - `import "path" as alias;`
  - storage declarations
  - `const Name = packed struct { ... };`
  - function declarations
  - top-level statements
- Actual meaning:
  - `import` is parsed, then ignored by binding/codegen
  - top-level `reg` declarations become real global register storage
  - bare top-level `var` declarations become runtime statements inside synthetic entrypoint `$top`
  - top-level `const Name = packed struct { ... };` is the real type-alias path
  - top-level `const Name = expr;` is not a real value declaration today

```blade
import "pins.blade" as pins;          // parses; binder ignores it

reg var g: u32 = 1;                   // real global storage
var x: u32 = g;                       // top-level runtime local inside `$top`

const PinCfg = packed struct {        // real type alias
    mode: nib,
    enabled: bool,
};

const Broken = comptime { };          // parses, but does not become a usable const value
```

### Function Declarations

- Parsed function kind prefixes:
  - `leaf`
  - `inline`
  - `rec`
  - `coro`
  - `comptime`
  - `int1`
  - `int2`
  - `int3`
- Return syntax accepted in two forms:
  - `fn f(...) -> u32 { ... }`
  - `fn f(...) u32 { ... }`
- Multi-return syntax is parsed:
  - `fn f() -> u32, bool { ... }`
  - `fn f() -> value: u32, ok: bool @C { ... }`
- Actual meaning:
  - return item names are ignored after parse
  - return `@Flag` annotations are ignored after parse
  - binder tracks multiple return types
  - backend effectively only handles the first return value

```blade
leaf fn add1(x: u32) -> u32 {
    return x + 1;
}

fn bare_return_syntax(x: u32) u32 {
    return x;
}

fn parsed_but_not_fully_emitted() -> value: u32, ok: bool @C {
    return 1, true;                   // binder knows there are 2 return values
}
```

### Types

- Primitive keywords accepted:
  - `bool`
  - `bit`
  - `nit`
  - `nib`
  - `u8`
  - `i8`
  - `u16`
  - `i16`
  - `u32`
  - `i32`
  - `void`
- Generic-width syntax accepted:
  - `uint(expr)`
  - `int(expr)`
- Composite syntax accepted:
  - arrays: `[expr]T`
  - single pointers: `*storage [const] [volatile] [align(expr)] T`
  - multi-pointers: `[*]storage [const] [volatile] [align(expr)] T`
  - packed structs: `packed struct { field: T, ... }`
  - unions: `union { field: T, ... }`
  - enums: `enum (T) { member = value, ... }`
  - bitfields: `bitfield (T) { field: T, ... }`
  - named types: `MyAlias`
- Actual type information preserved by binder:
  - primitive identity
  - pointer family, storage class, pointee type, `const`, `volatile`, and `align(...)`
  - array element type and constant length when available
  - struct field map
  - union field map
  - enum backing type, members, and open/closed state
  - bitfield backing type plus computed member bit offsets and widths
- Actual type information lost by binder:
  - `uint(5)` vs `uint(12)` vs `uint(31)` all collapse to plain `uint`
  - `int(5)` vs `int(12)` vs `int(31)` all collapse to plain `int`
  - non-constant `align(...)` expressions still fold to `null`
  - aggregate lowering is still incomplete for general member/index/deref codegen

```blade
var a: uint(5) = 0;                   // width parsed; semantic type is plain `uint`
var b: [16]u8 = undefined;            // constant array length is preserved
var p: *hub const volatile align(4) u32 = undefined;
var many: [*]reg u8 = undefined;

const Pair = packed struct {
    lo: u16,
    hi: u16,
};

type Header = union {
    pair: Pair,
    raw: u32,
};

type Mode = enum (u8) {
    Off = 0,
    On,
};

type Flags = bitfield (u32) {
    low: nib,
    high: nib,
};
```

- Special note:
  - string literals exist and bind as an internal `string` type
  - there is no `string` keyword in the parser
  - user code cannot name `string` as a declaration type today

### Statements

- Parsed statement forms:
  - blocks
  - variable declarations
  - expression statements
  - assignments
  - `if` / `else`
  - `while (...) { ... }`
  - `for (ident) { ... }`
  - `loop { ... }`
  - `rep loop (count) { ... }`
  - `rep for (ident in start..end) { ... }`
  - `noirq { ... }`
  - `return ...;`
  - `break;`
  - `continue;`
  - `yield;`
  - `yieldto target(...);`
  - `asm ... { ... };`
- Current `for` shape is not a C-style loop and not the design-doc range loop.
- Current `for (ident)` binds as "loop while the existing symbol `ident` is truthy".

```blade
while (flag) {
    counter += 1;
}

for (flag) {                          // current parser shape
    counter += 1;                     // binder reads `flag` as the loop condition variable
}

rep loop (8) {                        // binds
    x += 1;                           // backend does not lower this end-to-end yet
}

rep for (i in 0..8) {                 // binds
    x += i;                           // backend does not lower this end-to-end yet
}
```

### Expressions

- Parsed expressions:
  - literals
  - names
  - parenthesized expressions
  - unary: `!expr`, `-expr`, `*expr`
  - binary: `+ - * / & | ^ << >> == != < <= > >=`
  - postfix: `expr++`, `expr--`
  - member access: `expr.field`
  - pointer deref postfix form: `expr.*`
  - indexing: `expr[index]`
  - calls: `callee(args...)`
  - intrinsic calls: `@name(args...)`
  - struct literals: `.{ .field = expr, ... }`
  - `comptime { ... }`
  - `if (cond) thenExpr else elseExpr`
  - range expressions: `start..end`
- Not present in the parser:
  - logical `&&`
  - logical `||`
  - assignment expressions
  - ternary `?:`
  - prefix `++x` / `--x`

```blade
@pinwrite(pin, 1)                     // intrinsic call, always typed as `u32`
ptr.*                                 // deref allowed for `*T`
many[0]                               // indexing allowed for arrays and `[*]T`
if (x == 0) 1 else 2                  // real expression form
.{ .lo = 1, .hi = 2 }                 // struct literal
```

## Semantic Model

### Scopes And Symbols

- Scopes:
  - global scope
  - top-level runtime scope (`$top`)
  - per-function nested scopes
- Symbols:
  - type aliases
  - variables
  - parameters
  - functions
- No symbol is created for `import`.
- Top-level statements do not become globals; they become statements in synthetic function `$top`.

### Variable Storage

- Storage classes in syntax:
  - `reg`
  - `lut`
  - `hub`
  - omitted -> automatic
- Actual support:
  - top-level `reg` storage works
  - top-level `lut` and `hub` are rejected with `E0218`
  - local `reg` / `lut` / `hub` are rejected with `E0215`
  - parameter storage classes are rejected with `E0217`
  - `extern` is only allowed on top-level storage declarations; otherwise `E0216`
- Global storage places created by MIR:
  - allocatable global register
  - fixed register alias
  - external alias
- `@(...)` and `align(...)` are parsed and constant-folded if possible.
- If `@(...)` or `align(...)` is not a simple constant integer, the value just becomes `null`; no dedicated diagnostic is emitted.

```blade
reg var data: u32 = 1;                // real allocatable global register
extern reg var OUTA: u32 @(0x1FC);    // real fixed alias shape
hub var buf: [16]u32 = undefined;     // parses, then E0218
```

### Functions And Calling-Convention Markers

- Function kinds stored on symbols:
  - `Default`
  - `Leaf`
  - `Inline`
  - `Rec`
  - `Coro`
  - `Comptime`
  - `Int1`
  - `Int2`
  - `Int3`
- Call graph tiering in `CallGraphAnalyzer`:
  - entrypoint -> `EntryPoint`
  - explicit `rec` -> `Recursive`
  - explicit `coro` -> `Coroutine`
  - explicit `int1/int2/int3` -> `Interrupt`
  - explicit `leaf` -> `Leaf`
  - default with no callees -> `Leaf`
  - default calling only leaves -> `SecondOrder`
  - everything else -> `General`
- Practical caveats:
  - `inline fn` only forces MIR inlining; it is not a separate backend convention
  - `comptime fn` is only a kind marker today
  - explicit `leaf` overrides auto-tiering even if the body actually calls something

### Returns

- Binder checks return arity against declared return spec.
- `void` as the only declared return type is normalized to zero returns.
- MIR/LIR can carry multiple returned values.
- ASM lowering only uses the first returned value.
- Return item names and flag annotations do not affect semantics yet.

```blade
fn ok() void {                        // normalized to 0 return values
    return;
}

fn pair() -> u32, bool {
    return 1, true;                   // tracked in binder/MIR, not fully emitted by backend
}
```

### Type Rules

- Integer literals start as special type `<int-literal>`.
- Integer-to-integer assignment/conversion is broadly allowed.
- `undefined` is assignable to most targets, including pointers.
- Struct assignment is structural:
  - same field count
  - same field names
  - recursively assignable field types
- Union assignment is structural using the same field-name/type matching rule.
- Enum assignment allows only the same enum type.
- Open enums can be explicitly cast to and from their backing integer type.
- Closed enums require `bitcast` to cross the enum/integer boundary.
- Bitfield assignment allows only the same bitfield type; integer conversion uses `bitcast`.
- Pointer assignability tracks:
  - pointer family (`*T` vs `[*]T`)
  - storage class
  - pointee type
  - qualifier-safe `const` / `volatile`
  - minimum alignment
- Array indexing allows:
  - arrays
  - multi-pointers
- Pointer dereference allows:
  - single pointers only
- Member access allows:
  - structs
  - unions
  - bitfields
- Enum literals:
  - `.member` requires expected enum context
  - `TypeName.member` resolves through type aliases without entering value scope
- Intrinsic calls always bind as result type `u32`.

```blade
var source: *reg u32 = undefined;
var sink: *reg const volatile u32 = source;

var x: uint(5) = 1;
var y: uint(31) = x;                 // both are plain `uint` semantically

type Mode = enum (u8) { Off = 0, On = 1, };
var mode: Mode = .On;                // contextual enum literal

type Flags = bitfield (u32) { low: nib, high: nib, };
var flags: Flags = undefined;
flags.high = 3;                      // lowers as bitfield insert
```

### Control-Flow Checks

- `break` / `continue` require an enclosing loop.
- `break` inside `rep` is rejected with `E0209`.
- `continue` inside `rep` is allowed by binder.
- `yield` is only accepted inside `int1` / `int2` / `int3` functions.
- `yieldto` is only accepted:
  - at top level
  - inside `coro` functions
- `yieldto` target must name a `coro` function.

### Inline Assembly

- Syntax shape:
  - `asm { ... };`
  - `asm volatile { ... };`
  - `asm -> @C { ... };`
  - `asm -> @Z { ... };`
- Validator checks:
  - instruction mnemonic exists in generated P2 metadata
  - optional condition prefix exists
  - optional trailing flag effect is valid
  - `{name}` bindings refer to names visible in scope
- Binding access analysis:
  - infers read/write/read-write for typed inline asm when possible
  - falls back to read-write for volatile blocks, flag-output blocks, or unsupported operand shapes
- Final lowering modes:
  - typed lowering -> parsed instruction nodes
  - raw fallback -> emit original text verbatim as `AsmInlineTextNode`
  - volatile always uses raw fallback
  - flag-output always uses raw fallback
- Blade `//` comments are rewritten to PASM `'` comments in raw inline asm output.

```blade
asm {
    mov {dst}, {src}                 // typed lowering path if operands are simple
    add {dst}, #1
};

asm volatile -> @C {
    cmp {lhs}, {rhs} wz              // raw text fallback; flag output not modeled in typed form
};
```

## Diagnostics Snapshot

- Lexer:
  - `E0001` unexpected character
  - `E0002` unterminated string
  - `E0003` invalid number literal
  - `E0004` unterminated block comment
- Parser:
  - `E0101` unexpected token
  - `E0102` expected expression
  - `E0104` expected type name
  - `E0106` invalid assignment target
- Semantics:
  - `E0201` duplicate symbol
  - `E0202` undefined name
  - `E0203` undefined type
  - `E0205` type mismatch
  - `E0206` not callable
  - `E0207` argument count mismatch
  - `E0208` invalid loop control
  - `E0209` invalid `break` in `rep`
  - `E0210` invalid `yield`
  - `E0211` invalid `yieldto`
  - `E0212` return arity mismatch
  - `E0213` `return` outside function
  - `E0214` invalid `yieldto` target
  - `E0215` invalid local storage class
  - `E0216` invalid `extern` scope
  - `E0217` invalid parameter storage class
  - `E0218` unsupported storage class
- Inline asm:
  - `E0301` unknown instruction
  - `E0302` undefined inline asm binding
  - `E0303` empty inline asm instruction
  - `E0304` invalid flag output

## Lowering Pipeline

### MIR

- MIR shape:
  - SSA-ish values (`%vN`)
  - block parameters for phi-like merges
  - synthetic `$top` entrypoint function
  - global register storage modeled as `StoragePlace`
- MIR instructions cover:
  - constants
  - symbol/place loads
  - copies
  - unary/binary ops
  - selects
  - calls
  - intrinsic calls
  - generic ops by string opcode
  - stores / store-place / update-place
  - inline asm bindings
- Lowering details:
  - top-level global runtime initializers happen in `$top`
  - compile-time constant global initializers can become static `LONG`s instead
  - `if` statements and `if` expressions lower to real control flow
  - `while`, `for`, `loop` lower to CFG with environment threading
  - `rep`, `rep for`, `noirq`, `yield`, `yieldto` lower to string opcodes such as `rep.setup`, `noirq.begin`, `yieldto:name`

### MIR Optimizations

- Order:
  - mandatory `inline fn` inlining
  - optional single-callsite inlining
  - iterative MIR optimization
- MIR optimization passes:
  - cost-based inlining
  - constant propagation
  - copy propagation
  - control-flow simplification
  - dead-code elimination
- Current default cost threshold is small enough that tiny helper functions disappear quickly.
- Practical effect:
  - many examples optimize down to only `$top`
  - a lot of missing backend call handling is masked by early inlining

### LIR

- LIR is mostly MIR with:
  - virtual registers (`%rN`) instead of MIR values
  - explicit operands (`register`, `immediate`, `symbol`, `place`)
  - same block/terminator structure
- LIR optimization passes:
  - copy propagation
  - control-flow simplification
  - dead-code elimination

### ASMIR Instruction Selection

- Real lowering is implemented for:
  - `const` -> `MOV dest, #imm`
  - `mov`
  - `load.place`
  - `convert` -> plain `MOV`
  - scalar unary ops
  - scalar binary ops
  - scalar `select`
  - `intrinsic` -> direct uppercase mnemonic
  - aligned bitfield extract/insert ops:
    - `TESTB` / `WRC`
    - `GETNIB`, `GETBYTE`, `GETWORD`
    - `BITC`, `SETNIB`, `SETBYTE`, `SETWORD`
  - `store.place`
  - `update.place.<op>`
  - branches / gotos / returns
- Comparison lowering uses flags plus writes to boolean registers:
  - `==` -> `CMP ... WZ` + `WRZ`
  - `!=` -> `CMP ... WZ` + `WRNZ`
  - `<` / `>` / `<=` / `>=` -> `CMP ... WC` + `WRC` / `WRNC`
- Multiply/divide lowering:
  - `QMUL` / `QDIV`
  - `GETQX`

```blade
fn cmp(a: u32, b: u32) -> bool {
    return a == b;
}

// lowers roughly as:
//   CMP a, b WZ
//   WRZ dest
```

### Calling Convention Lowering

- Leaf tier:
  - first arg moved into `PA`
  - call emitted as `CALLPA PA, #target`
  - first result copied back from `PA`
- Second-order tier:
  - first arg moved into `PB`
  - call emitted as `CALLPB PB, #target`
  - first result copied back from `PB`
- General tier:
  - call emitted as `CALL #target`
  - args/results are currently comments only
- Recursive tier:
  - intended by comments as special handling
  - actual emitted call is still plain `CALL`
  - actual emitted return is still plain `RET`
- Coroutine tier:
  - calls/yields are not lowered to working `CALLD` machinery
- Interrupt tier:
  - returns emit `RETI1` / `RETI2` / `RETI3`

```blade
leaf fn add1(x: u32) -> u32 {
    return x + 1;
}

// leaf call lowering shape:
//   MOV PA, arg0
//   CALLPA PA, #add1
//   MOV dest, PA
```

### Register Allocation And Legalization

- Register allocation is whole-program.
- Pipeline:
  - per-function liveness
  - interference graph coloring with MOV coalescing
  - bottom-up packing through reconstructed call graph
  - rewrite virtual registers to physical/register-symbol operands
- Dead functions are removed before final codegen.
- Reachability roots:
  - entrypoint `$top`
  - interrupt handlers
- Important caveat:
  - `yieldto` does not participate in call-graph reachability
  - a coroutine referenced only by `yieldto` can still be treated as dead
- Legalization:
  - 9-bit immediates kept inline
  - larger immediates:
    - `AUGS` / `AUGD` when used once
    - shared constant register labels when reused

### Final PASM2 Text

- Output shape:
  - optional `CON` aliases for fixed registers
  - `DAT`
  - `org 0`
  - compiler banner comment
  - one label per function
  - register-file data block appended in the chosen function
- Entry-point exit behavior:
  - endless halt loop
  - emitted as `REP #1, #0` + `NOP`

```spin2
DAT
    org 0
    ' --- Blade compiler output ---

    ' function $top (EntryPoint)
  l_top
  l_top_bb0
    ' halt: endless loop
    REP #1, #0
    NOP
```

## End-To-End Status

### Features That Basically Work Today

- Lexing/parsing for the current surface grammar.
- Scalar integer and boolean expressions.
- `if`, `while`, `loop`, and the current `for (ident)` CFG lowering.
- Top-level `$top` entrypoint generation.
- Top-level `reg` globals, including constant static initialization.
- Packed-struct type aliases as type names.
- Union, enum, and bitfield type aliases in the binder.
- Contextual and qualified enum literals.
- Pointer storage/volatile/align metadata in the type system.
- MIR/LIR optimization and aggressive inlining.
- Register allocation, immediate legalization, and final PASM2 text emission.
- Typed inline asm when the operand shapes stay simple enough.

### Features That Exist Semantically But Are Not End-To-End Complete

- Arrays.
- Pointers.
- Struct and union values/member loads/stores.
- Bitfields:
  - semantic model is complete
  - aligned extract/insert cases are instruction-selected
  - generic unaligned fallback codegen is still incomplete
- Multi-value returns.
- Generic-width integer syntax.
- Alignment/fixed-address metadata.
- `extern` aliases.
- Interrupt function markers.
- Coroutine markers.

### Features That Are Parsed Or Marked But Still Stubby/Broken

- `import` is ignored after parse.
- `const Name = expr;` is not a real constant declaration path.
- `lut` / `hub` storage are rejected.
- `comptime { ... }` does not execute at compile time.
- `comptime fn` is only a tag.
- return item `@C` / `@Z` annotations are ignored.
- `rep`, `rep for`, and `noirq` lower to placeholder opcodes/comments, not working PASM2.
- `yield` / `yieldto` lower to TODO comments, not working coroutine machinery.
- general call lowering does not move arguments/results.
- recursive tier does not yet emit `CALLB` / `RETB`.
- general member/index/deref/range/struct-literal ops are not fully instruction-selected.
- non-constant `@(...)` / `align(...)` silently drop to `null`.
- coroutine reachability through `yieldto` is not modeled by dead-function analysis.
