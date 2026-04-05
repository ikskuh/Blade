# Quality Report — Blade Compiler

Generated: 2026-04-05
Coverage before: 95.0% line (19036/20029), 89.1% branch (7891/8856)
Coverage after:  95.6% line (19148/20024), 89.7% branch (7931/8846)

---

## 1. Code Duplicates (Same Semantic Action)

### 1.1 MIR/LIR Optimization Passes — Near-Identical Algorithms

The MIR and LIR optimization layers implement the same algorithms with different IR type parameters.
Only the value types differ (MirValueId vs LirVirtualRegister, MirInstruction vs LirInstruction, etc.);
the algorithmic structure is identical.

| MIR File | LIR File | Lines (MIR/LIR) |
|---|---|---|
| `Blade/IR/Mir/Optimizations/MirCopyPropagation.cs` | `Blade/IR/Lir/Optimizations/LirCopyPropagation.cs` | 52 / 47 |
| `Blade/IR/Mir/Optimizations/MirDeadCodeElimination.cs` | `Blade/IR/Lir/Optimizations/LirDeadCodeElimination.cs` | 58 / 53 |
| `Blade/IR/Mir/Optimizations/MirControlFlowSimplification.cs` | `Blade/IR/Lir/Optimizations/LirControlFlowSimplification.cs` | 173 / 167 |

**Impact:** ~550 lines of near-duplicate code. A generic IR optimization framework (e.g., shared interface
for instructions/blocks/terminators with generic optimization passes) could eliminate this duplication.

### 1.2 MIR/LIR Optimization Helpers — Duplicated Graph Algorithms

| MIR File | LIR File |
|---|---|
| `Blade/IR/Mir/MirOptimizationHelpers.cs` (184 lines) | `Blade/IR/Lir/LirOptimizationHelpers.cs` (402 lines) |

Shared methods: `IsTrivialGotoBlock()`, `ComputePredecessorCounts()`, `EnumerateSuccessors()`,
`ComputeReachableBlocks()` — identical control-flow graph analysis at different IR levels.
LIR has additional methods for instruction rewriting that are LIR-specific.

---

## 2. CLAUDE.md Guideline Violations

### 2.1 InvalidOperationException Instead of UnreachableException/Assert

Per CLAUDE.md: "Unreachable switch cases `throw new UnreachableException()` instead of returning wrong values"
and "Assert.Invariant(cond) for invariants guaranteed by earlier stages."

| File | Line | Issue |
|---|---|---|
| `Blade/IR/Asm/AsmLowerer.cs` | 179 | `throw new InvalidOperationException(...)` for array element type invariant — should be `Assert.UnreachableValue` (line 174 already uses it for the same pattern) |
| `Blade/IR/Asm/AsmLowerer.cs` | 2689 | `throw new InvalidOperationException("expected a destination register")` — invariant violation, should be `Assert.Unreachable()` |
| `Blade/IR/Asm/AsmLowerer.cs` | 2697 | `throw new InvalidOperationException("Expected register operand")` — invariant, should be `Assert.Unreachable()` |
| `Blade/IR/Asm/AsmLowerer.cs` | 2707 | `throw new InvalidOperationException("Unknown operand type")` — unreachable default, should be `Assert.UnreachableValue<AsmOperand>()` |
| `Blade/IR/Asm/RegisterAllocator.cs` | 563 | `throw new InvalidOperationException("Unknown register constraint kind")` — should be `Assert.UnreachableValue` |
| `Blade/IR/Asm/RegisterAllocator.cs` | 842 | `throw new InvalidOperationException("Missing allocated location")` — invariant violation |
| `Blade/IR/Asm/RegisterAllocator.cs` | 852 | `throw new InvalidOperationException("Unknown allocated location kind")` — unreachable default |
| `Blade/IR/Asm/AsmLegalizer.cs` | 150 | `throw new InvalidOperationException(...)` — invariant violation |
| `Blade/IR/Asm/AsmLegalizer.cs` | 168 | `throw new InvalidOperationException(...)` — invariant violation |
| `Blade/IR/Asm/AsmLegalizer.cs` | 248 | `throw new InvalidOperationException(...)` — unreachable default |
| `Blade/IR/OptimizationRegistry.cs` | 190, 201 | `throw new InvalidOperationException(...)` — configuration invariant |
| `Blade/Semantics/TypeSymbol.cs` | 259 | `throw new InvalidOperationException(...)` — should be `Assert.UnreachableValue` |
| `Blade/CompilationOptionsCommandLine.cs` | 176 | `throw new InvalidOperationException(...)` — argument validation, may be legitimate |

**Total: 14 occurrences.** Most should be replaced with `Assert.Invariant`, `Assert.Unreachable()`,
or `Assert.UnreachableValue<T>()`.

### 2.2 Ternary Operator for Control Flow

Per CLAUDE.md: "Don't use ternary operators for control flow."

| File | Line | Code |
|---|---|---|
| `Blade/Semantics/Binder.cs` | 1249 | `return isBreak ? new BoundBreakStatement(...) : new BoundContinueStatement(...);` |
| `Blade/Semantics/Binder.cs` | 407 | `nextFlag = nextFlag == ReturnPlacement.FlagC ? ReturnPlacement.FlagZ : nextFlag;` |

Line 1249 constructs different statement types based on a flag — this is control flow, not value selection.
Should use `if/else` or a local variable with pattern matching.

---

## 3. Coverage Gap Analysis

### 3.1 Summary by Category

993 unique uncovered lines across 99 classes. Top contributors:

| Category | Lines | % of Gap |
|---|---|---|
| ASM lowering (inline asm, edge operands) | ~240 | 24% |
| Binder (error diagnostics, edge cases) | ~150 | 15% |
| BladeValue (formatting, operators, equality) | ~145 | 15% |
| Comptime evaluator | ~67 | 7% |
| MIR lowering/inlining | ~76 | 8% |
| Inline assembly validation | ~31 | 3% |
| CLI/IO/Program | ~30 | 3% |
| Type symbols (ToString, equality) | ~40 | 4% |
| Optimization helpers/passes | ~50 | 5% |
| Remaining (diagnostics, misc model) | ~164 | 16% |

### 3.2 Top 10 Classes by Uncovered Lines

| # | Class | Lines | Primary Gap |
|---|---|---|---|
| 1 | `AsmLowerer` | 164 | Inline asm lowering, pointer ops, special registers, helpers |
| 2 | `Binder` | 150 | For-range binding, bitfield ops, intrinsic calls, type edge cases |
| 3 | `BladeValue` | 145 | ToString/Equals/GetHashCode for aggregates, arrays, pointers; TryCast branches |
| 4 | `ComptimeEvaluator` | 67 | Comptime aggregate/array ops, pointer arithmetic |
| 5 | `MirLowerer.FunctionLoweringContext` | 46 | Struct literal, pointer offset/difference, update-place |
| 6 | `InlineAssemblyValidator` | 31 | Operand validation, special instruction forms |
| 7 | `MirInliner.MutableFunction` | 30 | Inlining block parameter remapping |
| 8 | `ComptimeFunctionSupportAnalyzer` | 23 | Unsupported-construct detection branches |
| 9 | `AsmOptimizationHelpers` | 19 | Peephole matching edge cases |
| 10 | `RegisterAllocator` | 17 | Constraint resolution, spill slots |

---

## 4. Coverage Plan: Path to 100%

### Phase 1: Dead Code Elimination (est. ~100 lines → assertions)

Convert provably unreachable paths to `Assert.Invariant` / `Assert.Unreachable()`:

- **AsmLowerer:2689,2697,2707** — Helper functions `DestReg`, `OpReg`, `LowerOperand` error branches
  are invariants guaranteed by prior LIR validation. Replace with assertions.
- **RegisterAllocator:563,842,852** — Unreachable defaults in exhaustive switches.
- **AsmLegalizer:150,168,248** — Invariants from instruction form metadata.
- **BladeValue** — Equality/hash/ToString methods for types never compared at compile-time
  (aggregates, arrays, pointers). If truly dead, replace with `Assert.Unreachable()`.

### Phase 2: Demonstrator Files for Binder Gaps (~150 lines)

Create Blade programs that exercise uncovered binder paths:

1. **For-range with non-range iteratee** (Binder L1056-1107): Write `Demonstrators/Binder/fail_for_range_mismatch.blade`
2. **Bitfield operations** (Binder L1527-1544): Write `Demonstrators/Binder/bitfield_ops.blade`
3. **Break/continue in REP** (Binder L1249): Already partially covered, add `EXPECT: fail` fixture
4. **Intrinsic call edge cases** (Binder L1852-1878, L2154-2178): Demonstrators for each intrinsic
5. **Type conversion edges** (Binder L2686-2839): Demonstrators for cast between aggregate/pointer/array types
6. **Function call edge cases** (Binder L3041-3097, L3246-3256): Wrong-arity, wrong-type demonstrators

### Phase 3: Demonstrator Files for Comptime Gaps (~67 lines)

1. **Comptime aggregate operations**: Struct literal construction, field access at comptime
2. **Comptime array operations**: Array indexing, slicing at comptime
3. **Comptime pointer arithmetic**: Pointer offset/difference at comptime

### Phase 4: Demonstrator Files for ASM/IR Gaps (~200 lines)

1. **Inline assembly with special operands**: Current-address (`@`), label references, alt placeholders
2. **Pointer operations through full pipeline**: Pointer offset, pointer difference lowered to ASM
3. **Struct literal lowering**: Aggregate construction through MIR → LIR → ASM
4. **Update-place operations**: In-place field updates through the pipeline

### Phase 5: BladeValue Coverage (~145 lines)

Most of BladeValue's uncovered code is `ToString`, `Equals`, `GetHashCode` for complex value kinds
(aggregates, arrays, pointers, undefined). These are either:
- Used in diagnostics (write demonstrators that trigger diagnostic messages containing these values)
- Truly dead (replace with `Assert.Unreachable()`)

Approach: Check each uncovered method — if it's reachable from a diagnostic format string, write a
demonstrator. If not, convert to assertion.

### Phase 6: Optimization Edge Cases (~50 lines)

Write Blade programs that trigger specific optimizer behaviors:
1. **MirConstantPropagation edge cases**: Programs with constant pointer offsets
2. **MirControlFlowSimplification edge cases**: Programs with deeply nested conditionals producing specific CFG shapes
3. **AsmOptimizationHelpers**: Programs producing specific instruction patterns

### Phase 7: CLI/IO & Misc (~60 lines)

- **Program.cs L41-44**: stdin compilation — test via `echo "..." | blade -`
- **RuntimeTemplate L37-40**: Runtime template loading error path
- **StdioOutputWriter L90**: Output edge case
- **JsonReportBuilder L103-141**: JSON report format branches — exercise via `--output-format json`
- **CompilationOptionsCommandLine L120-152,261-263**: CLI option parsing edge cases

### Estimated Effort

| Phase | Lines Covered | Method |
|---|---|---|
| 1. Dead code → assertions | ~100 | Code changes |
| 2. Binder demonstrators | ~150 | ~10 new .blade files |
| 3. Comptime demonstrators | ~67 | ~5 new .blade files |
| 4. ASM/IR demonstrators | ~200 | ~8 new .blade files |
| 5. BladeValue coverage | ~145 | Mix of demonstrators + assertions |
| 6. Optimizer edge cases | ~50 | ~5 new .blade files |
| 7. CLI/IO & misc | ~60 | Unit tests + CLI invocations |
| **Total** | **~772** | |

Remaining ~221 lines are in small classes (1-7 lines each) — mostly `ToString()` methods on
IR model nodes, syntax node `TextSpan` properties, and similar. These will be covered incidentally
by the demonstrator files above or can be individually assessed for dead-code conversion.
