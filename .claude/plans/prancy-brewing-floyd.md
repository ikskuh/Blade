# Backend Validation Report & Implementation Plan

## Context

The Blade compiler has a well-structured 4-stage IR pipeline (MIR → LIR → ASMIR → PASM2 text). The backend currently emits abstract pseudo-assembly with virtual registers and invented opcodes. FlexSpin would reject all current output.

## Validation Findings

### 1. NO REAL PASM2 INSTRUCTIONS ARE EMITTED

| What the backend emits | What P2 needs |
|------------------------|---------------|
| `CONST %r0, 42` | `MOV %r0, #42` |
| `BINARY_ADD %r2, %r0, %r1` | `MOV %r2, %r0` + `ADD %r2, %r1` |
| `BINARY_MUL %r3, %r0, %r1` | `QMUL %r0, %r1` + `GETQX %r3` |
| `UNARY_NEGATE %r1, %r0` | `NEG %r1, %r0` |
| `BRANCH %r0, label_t, label_f` | `TJZ %r0, #label_f` (fall through to true) or `CMP` + `IF_xx JMP` |
| `JMP label (args)` | MOVs for φ-args + `JMP #label` |
| `RET (%r3)` | Result placement + `RET` |
| `PARAM %r0 u32 x` | Eliminated (register assignment, not an instruction) |
| `TRAP` | Infinite loop: `JMP #$` or `WAITX #0` loop |

### 2. VIRTUAL REGISTERS — Acceptable at ASMIR level
ASMIR keeps virtual registers (`%r0`, `%r1`, ...) — this is correct for an abstract assembly IR. Physical register allocation happens later.

### 3. CALLING CONVENTIONS — NOT LOWERED
`FunctionKind` flows through but is never acted on. No CALLPA/CALLPB/CALL/CALLB emission.

### 4. FLAG EFFECTS (WC/WZ/WCZ) — HARDCODED TO FALSE
Every LIR instruction has `writesC: false, writesZ: false`. Comparisons, bool returns via C/Z, and `IF_xx` predication can't work.

### 5. INSTRUCTION FORMAT — 3-OPERAND vs. P2's 2-OPERAND
IR emits 3-operand (`ADD dest, src1, src2`). P2 is 2-operand destructive (`ADD D, S`). ASMIR should use real P2 form: `MOV %rd, %rs1` + `ADD %rd, %rs2`.

### 6. IMMEDIATE VALUE HANDLING — Deferred to legalization pass (acceptable)

### 7. CONTROL FLOW — BRANCH/PARAM are abstract pseudo-ops, not real instructions

### 8. WHAT IS CORRECTLY IMPLEMENTED
- MIR SSA form is clean and correct
- MIR optimizer (constant prop, copy prop, DCE, inlining) is sound
- Function kind propagation through IR layers works
- Pipeline shape (MIR → LIR → ASMIR → text) is right
- DAT/org wrapper for FlexSpin is correct

---

## Architecture Decision

**ASMIR is the right place for instruction selection.** It should use real P2 mnemonics with virtual registers and abstract jumps (labels, not addresses). This enables peephole optimization on ASMIR before final emission.

The pipeline becomes:

```
LIR (SSA, virtual regs, high-level ops)
  ↓ Instruction Selection (LIR → ASMIR)
ASMIR (P2 mnemonics, virtual regs, label-based jumps, unbounded immediates)
  ↓ Peephole Optimization
ASMIR (optimized)
  ↓ Register Allocation
ASMIR (physical regs, label-based jumps)
  ↓ Legalize (immediates, AUGS/AUGD or constant regs)
ASMIR (legal P2 instructions)
  ↓ Final Emission
PASM2 text (FlexSpin-ready)
```

Immediate handling: ASMIR can freely use any immediate value. A **legalize pass** converts non-9-bit immediates to either `AUGS`/`AUGD` prefix before the instruction, or a global constant register.

---

## Implementation Steps

### Step 1: Instruction Selection — Rewrite AsmLowerer

Rewrite [AsmLowerer.cs](Blade/IR/Asm/AsmLowerer.cs) to emit real P2 mnemonics with virtual registers.

**ASMIR instruction model changes:**
- `AsmInstructionNode` gains optional `WC`/`WZ`/`WCZ` flag effect fields
- `AsmInstructionNode` keeps `predicate` field for `IF_xx` conditions
- Operands are typed: virtual register (`%rN`), immediate (`#value`), or label (`#label`)

**Instruction mappings:**

| LIR opcode | ASMIR output | Notes |
|---|---|---|
| `const` | `MOV %rd, #imm` | Any immediate allowed at ASMIR level |
| `mov` | `MOV %rd, %rs` | |
| `load.sym` | `MOV %rd, %rsym` | Symbol resolved to register later |
| `binary.Add` | `MOV %rd, %rleft` + `ADD %rd, %rright` | 2-operand destructive |
| `binary.Sub` | `MOV %rd, %rleft` + `SUB %rd, %rright` | |
| `binary.Mul` | `QMUL %rleft, %rright` + `GETQX %rd` | CORDIC pipeline |
| `binary.Div` | `QDIV %rleft, %rright` + `GETQX %rd` | |
| `binary.Mod` | `QDIV %rleft, %rright` + `GETQY %rd` | Remainder in QY |
| `binary.And` | `MOV %rd, %rleft` + `AND %rd, %rright` | |
| `binary.Or` | `MOV %rd, %rleft` + `OR %rd, %rright` | |
| `binary.Xor` | `MOV %rd, %rleft` + `XOR %rd, %rright` | |
| `binary.Shl` | `MOV %rd, %rleft` + `SHL %rd, %rright` | |
| `binary.Shr` | `MOV %rd, %rleft` + `SHR %rd, %rright` | Unsigned |
| `binary.Sar` | `MOV %rd, %rleft` + `SAR %rd, %rright` | Signed |
| `binary.Eq` | `CMP %rleft, %rright WZ` + `WRZ %rd` | Z=1 if equal |
| `binary.Ne` | `CMP %rleft, %rright WZ` + `WRNZ %rd` | |
| `binary.Lt` | `CMP %rleft, %rright WC` + `WRC %rd` | Unsigned: C=borrow |
| `binary.LtSigned` | `CMPS %rleft, %rright WC` + `WRC %rd` | Signed compare |
| `binary.Le` | `CMP %rright, %rleft WC` + `WRNC %rd` | !(right < left) |
| `binary.Gt` | `CMP %rright, %rleft WC` + `WRC %rd` | Reversed operands |
| `binary.Ge` | `CMP %rleft, %rright WC` + `WRNC %rd` | !borrow |
| `unary.Negate` | `NEG %rd, %rs` | P2 NEG is 2-operand non-destructive |
| `unary.Not` | `NOT %rd, %rs` | Bitwise NOT |
| `unary.LogicalNot` | `CMP %rs, #0 WZ` + `WRZ %rd` | Bool inversion via flags |
| `select` | `CMP %rcond, #0 WZ` + `MOV %rd, %rfalse` + `IF_NZ MOV %rd, %rtrue` | Predicated select |
| `call` | (see Step 3 — CC lowering) | |
| `store.*` | `MOV %rtarget, %rvalue` | For reg vars |
| `pseudo.rep.setup` | `REP #count, #iters` | |
| `pseudo.noirq.begin` | `REP #bodylen, #1` | |
| BRANCH terminator | `CMP %rcond, #0 WZ` + φ-MOVs + `IF_Z JMP #false_label` + φ-MOVs + (fall through or JMP to true) | |
| GOTO terminator | φ-MOVs + `JMP #label` | |
| RET terminator | Result MOVs + `RET` | |
| UNREACHABLE | `JMP #$` | Infinite loop trap |

**Key files to modify:**
- [AsmLowerer.cs](Blade/IR/Asm/AsmLowerer.cs) — complete rewrite
- [AsmModel.cs](Blade/IR/Asm/AsmModel.cs) — add flag effects, typed operands

### Step 2: Calling Convention Lowering

Integrated into instruction selection (Step 1) for `call` opcodes and `RET` terminators.

**Per FunctionKind:**

| Kind | Call sequence | Return sequence |
|---|---|---|
| Leaf | `MOV PA, %rparam` + `CALLPA PA, #target` | `MOV PA, %rresult` + `RET` |
| 2nd-order | `MOV PB, %rparam` + `CALLPB PB, #target` | `MOV PB, %rresult` + `RET` |
| Default (general) | MOV params to assigned regs + `CALL #target` | MOV result to assigned reg + `RET` |
| Rec | `PUSHB %rlocals` (each) + `CALLB #target` | `POPB %rlocals` (each) + `RETB` |
| Coro | `MOV PA, %rparam` + `CALLD %rcont, %rtarget` | `CALLD %rcont, %rcaller` (yield) |
| Int1/2/3 | Prologue: save clobbered regs; Epilogue: restore + `RETI1`/`RETI2`/`RETI3` | Entry addr written to `IJMP1`/`IJMP2`/`IJMP3` at startup |

**Call graph analysis** for auto-tiering `Default` functions:
- Build call graph from MIR/LIR
- Leaves (no callees) → CALLPA tier
- Calls only CALLPA leaves → CALLPB tier
- Everything else → CALL tier
- Add this as a pass before instruction selection, annotating each function with its resolved CC tier

### Step 3: Flag & Predication System

- Update `LirOpInstruction` and `AsmInstructionNode` to carry `WC`/`WZ`/`WCZ` effect annotations
- Instruction selection (Step 1) sets these based on the operation
- Comparisons naturally produce flags; the flag result feeds into subsequent `IF_xx` predicated instructions or `WRC`/`WRZ` materializations
- Bool return values: place result in C/Z via `CMP`/`TESTB` before `RET WC`/`RET WZ`

### Step 4: Register Allocation

After instruction selection, allocate virtual registers to physical COG registers.

- Linear scan allocator (functions are small, ≤511 instructions)
- Reserved: PA($1F6), PB($1F7), PTRA($1F8), PTRB($1F9), DIRA-INB($1FA-$1FF)
- Allocate from $000 upward; code occupies low addresses, data/regs from top down (or vice versa — TBD based on layout strategy)
- Coalesce MOV pairs where src and dest can share a register
- Spill to hub via PTRA if register pressure exceeds budget (unlikely for COG-targeted code)

**Key file:** New `RegisterAllocator.cs` in `Blade/IR/Asm/`

### Step 5: Legalize Pass

Post-register-allocation pass over ASMIR:

1. **Immediate range check**: For each `#imm` operand, if value > 511 (9-bit unsigned):
   - If fits in 32 bits with AUGS: insert `AUGS #(value >> 9)` before the instruction
   - For D-field immediates (rare): `AUGD`
   - If the immediate is used multiple times: allocate a constant register instead and replace `#imm` with the register
2. **Function size check**: Count instructions per function, error if > 511
3. **Label resolution**: Replace `#label` with computed addresses (or leave symbolic for FlexSpin to resolve)

**Key file:** New `AsmLegalizer.cs` in `Blade/IR/Asm/`

### Step 6: Final Emission Update

Update [FinalAssemblyWriter.cs](Blade/IR/Asm/FinalAssemblyWriter.cs) to:
- Emit physical register names (or symbolic labels for FlexSpin)
- Emit `WC`/`WZ`/`WCZ` suffixes
- Emit `IF_xx` condition prefixes
- Emit `AUGS`/`AUGD` prefixes from legalization
- Emit proper P2 syntax: `opcode D, {#}S {WC/WZ/WCZ}`

### Step 7: Validation & Testing

- `dotnet test` — existing tests still pass
- `flexspin -2 -c -q output.spin2` — emitted PASM2 assembles
- `flexspin -2 -c -l -q output.spin2` — listing shows correct encodings
- Add codegen snapshot tests for each instruction mapping
- Test each CC tier with hand-verified reference programs

## Critical Files

| File | Action |
|---|---|
| [AsmModel.cs](Blade/IR/Asm/AsmModel.cs) | Extend with flag effects, typed operands |
| [AsmLowerer.cs](Blade/IR/Asm/AsmLowerer.cs) | Rewrite for P2 instruction selection |
| [AsmOptimizer.cs](Blade/IR/Asm/AsmOptimizer.cs) | Extend peephole opts for P2 patterns |
| [FinalAssemblyWriter.cs](Blade/IR/Asm/FinalAssemblyWriter.cs) | Update for real P2 syntax |
| New: `AsmLegalizer.cs` | Immediate legalization pass |
| New: `RegisterAllocator.cs` | Virtual → physical register mapping |
| New: `CallGraphAnalyzer.cs` | CC tier auto-assignment |
| [LirLowerer.cs](Blade/IR/Lir/LirLowerer.cs) | Propagate flag write info |
| [IrPipeline.cs](Blade/IR/IrPipeline.cs) | Wire new passes into pipeline |
