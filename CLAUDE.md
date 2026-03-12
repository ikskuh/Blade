# CLAUDE.md — Blade Compiler Project

## What is Blade?

Blade is a tiny systems programming language that compiles to PASM2 (Propeller 2 assembly).
It targets COG execution mode on the Parallax Propeller 2 microcontroller. Think "C for the
PDP-11" — a high-level assembler with Zig/Rust-inspired syntax where every construct maps to
1–3 PASM2 instructions. See `Docs/blade_language_design_v3.md` for the full language spec.

## Project Structure

```
Blade/                          # Main compiler project (C#)
Blade.Tests/                    # Unit tests (NUnit)
Docs/                           # Reference documentation
  blade_language_design_v3.md   # Language specification
  Propeller 2 Documentation v35 - Rev B_C Silicon.pdf
  Propeller-2-Hardware-Manual-20221101.pdf
  Propeller2-P2X8C4M64P-Datasheet-20221101.pdf
  Parallax Propeller 2 Instructions v35 - Rev B_C Silicon.csv
```

## Host Environment

The following tools are available on the host:

- **`dotnet`** — .NET SDK for building and testing the compiler.
- **`flexspin`** — Parallax FlexSpin compiler (v6.9.10+). Used to validate
  emitted PASM2 output.

### Validating PASM Output with FlexSpin

FlexSpin can assemble and check PASM2 output. Use `-2` for Prop2 mode and `-c`
to compile only DAT sections (pure PASM, no Spin wrapper):

```sh
# Assemble a PASM2 file, check for errors
flexspin -2 -c -q output.spin2

# Produce a listing for inspection
flexspin -2 -c -l -q output.spin2

# Full binary (if needed for integration tests)
flexspin -2 -q -o output.bin output.spin2
```

The emitted PASM must be wrapped in a minimal Spin2/PASM2 DAT block for FlexSpin
to accept it. The codegen should emit something like:

```spin2
DAT
    org 0
    ' --- Blade compiler output ---
    ' (PASM2 instructions here)
```

Use `-q` (quiet) to suppress the banner in CI. FlexSpin's exit code is 0 on success,
nonzero on assembly errors — suitable for test assertions.

## Language & Tooling

- **Language**: C#
- **Test framework**: NUnit
- **Output format**: PASM2 assembly (text, wrapped for FlexSpin)
- **Parser style**: Handrolled recursive descent — no parser generators

## Architecture

The compiler pipeline is roughly:

1. **Lexer** → token stream
2. **Parser** → AST (recursive descent, no backtracking)
3. **Semantic analysis** → typed AST, call-graph analysis, CC tiering
4. **SSA lowering** → SSA IR with backwards register allocation
5. **Codegen** → PASM2 text output

Build incrementally. Each stage should be independently testable.

## Coding Style

- **Explicit style.** Prefer clarity over brevity.
- **Minimal exceptions.** Use result types / error returns for expected failures.
  Exceptions are for programmer errors (bugs), not user input errors.
- **Proper error handling.** Every error path must be handled, not swallowed.
- **No `var` where the type isn't obvious.** Prefer explicit types on declarations
  when the RHS doesn't make the type self-evident.

## Diagnostics

Every diagnostic (error, warning, info) must have:

1. A **numeric code** (e.g., `E1001`, `W2003`)
2. A **human-readable name** (e.g., `StackDepthExceeded`)
3. A **format string** with placeholders for context

Prefix conventions:
- `E` = error (compilation fails)
- `W` = warning (compilation continues)
- `I` = info / note

**Use T4 text templates** (`.tt` files) to project diagnostic codes from a dense
definition table. This allows easy refactoring, renumbering, and ensures the code,
name, and format string stay in sync. Example dense definition:

```
E1001 | StackDepthExceeded  | Call depth {0} exceeds hardware stack limit of 8. Consider 'rec fn'.
E1002 | RegisterOverflow    | Code + data require {0} registers, exceeding the {1} available.
W2001 | UnusedVariable      | Variable '{0}' is declared but never used.
```

The T4 template generates the actual C# diagnostic classes/enums from this table.

## Testing

### Accept/Reject Test Files

Tests use `.blade` source files organized into accept/reject collections:

- **Accept files** (`Tests/Accept/*.blade`): Must compile successfully. May contain
  expected warnings.
- **Reject files** (`Tests/Reject/*.blade`): Must fail compilation with specific errors.

Both accept and reject files encode expected diagnostics as codes in a comment on
the **first line**:

```blade
// E1001, W2003
rec fn deep() -> u32 {
    return deep() + deep();  // E1001: stack depth exceeded
}
reg var unused: u32 = 0;     // W2003: unused variable
```

The test runner:
1. Parses the first-line comment for expected diagnostic codes.
2. Runs the compiler on the file.
3. Asserts that **exactly** the listed diagnostics were emitted (no more, no fewer).
4. For accept files: asserts compilation succeeds (only warnings, no errors).
5. For reject files: asserts compilation fails (at least one error present).

Accept files that produce PASM output can additionally be validated through FlexSpin
to ensure the emitted assembly is syntactically valid.

### Unit Tests

Standard NUnit tests in `Blade.Tests/` for individual compiler components:
lexer token sequences, parser AST shapes, semantic checks, codegen output.

Run with:
```sh
dotnet test
```

## P2 Architecture Quick Reference

Key facts for compiler developers (see docs for full details):

- **COG exec**: code + data share 512 × 32-bit Register RAM ($000–$1FF)
- **Usable registers**: $000–$1EF (496 general purpose)
- **Special registers**: $1F0–$1F7 (dual-purpose: IJMP/IRET/PA/PB), $1F8–$1FF (PTRA/PTRB/DIR/OUT/IN)
- **Hardware stack**: 8 levels deep, stores {C, Z, PC}
- **All functions ≤ 511 instructions** (architectural limit in COG/LUT exec)
- **Instruction CSV**: `Docs/Parallax Propeller 2 Instructions v35 - Rev B_C Silicon.csv`
  has the full ISA with encodings, cycle counts, and flag effects

## Calling Convention Tiering (Compiler Must Implement)

The compiler analyzes the static call graph and auto-assigns:

| Shape                        | Mechanism     | Key constraint                          |
|------------------------------|---------------|-----------------------------------------|
| Calls nothing (leaf)         | CALLPA + RET  | Param in PA, result in PA               |
| Calls only CALLPA-leaves     | CALLPB + RET  | Param in PB; child leaves use PA freely |
| Calls non-leaves             | CALL + RET    | Params in global registers              |
| Recursive (explicit)         | CALLB + RETB  | Hub stack via PTRB                      |
| Coroutine (explicit)         | CALLD + CALLD | Continuation register per coro          |
| Interrupt (hardware-entered) | IJMPn/RETIn   | Not callable from code                  |

Functions not in the same call graph share parameter registers. Single-call
functions are always inlined. Tail calls emit JMP instead of CALL.

## Key Design Decisions to Remember

- `rec fn` uses PTRB (not PTRA) — PTRA is free for user hub pointer ops.
- `intN fn` are **not callable** — hardware-entered only.
- `intN fn` with `yield`: on return, write entry address to IRETn (constant, no save needed).
- REP + branch = **undefined behavior** on final silicon. REP + conditional execution (IF_xx) is fine.
- `noirq { }` = REP with count 1 (interrupt shielding, no repeat).
- RET supports WC, WZ, WCZ independently — functions can preserve/clobber flags selectively.
- Bool returns via C/Z flags — the 34-bit return channel (u32 + bool@C + bool@Z).
- BITx with WCZ = test-and-set in one instruction (read original bit, then modify).
- Top-level code is the entry point — no implicit `main()`.
