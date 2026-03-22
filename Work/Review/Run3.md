# Blade/ Code Review Findings (2026-03-22)

Scope: manual review of `Blade/` with emphasis on correctness and unnecessary complexity.

## Executive summary

I found **9 actionable findings**:

- **4 correctness findings** (2 high confidence bugs, 1 silent-failure risk, 1 ambiguous correctness/documentation mismatch).
- **5 unnecessary-complexity findings** (mostly large multi-responsibility files and boilerplate patterns that increase defect risk).

---

## Findings

## C1 — Diagnostic code prefix is hardcoded to `E` (incorrect for warnings/info)
- **Severity:** High
- **Confidence:** High
- **Location:** `Blade/Diagnostics/Diagnostic.cs:21-25`, `Blade/Diagnostics/DiagnosticCode.cs:4-6`
- **What I found:** `Diagnostic.FormatCode()` always formats codes with `E####` regardless of diagnostic class.
- **Why this matters:** The enum documentation explicitly states `E/W/I` prefixes are supported. Current formatting will mislabel warnings/info as errors in user output and any tooling that parses codes.
- **Recommendation:** Derive prefix from code metadata (or naming convention), then format as `{prefix}{numericCode:D4}`.

## C2 — `HasErrors` treats any diagnostic as an error
- **Severity:** Medium
- **Confidence:** High
- **Location:** `Blade/Diagnostics/DiagnosticBag.cs:16`
- **What I found:** `HasErrors` is implemented as `_diagnostics.Count > 0`.
- **Why this matters:** If warning/info diagnostics are introduced, this property semantics become wrong and can cause premature pipeline aborts in future usage.
- **Recommendation:** Implement severity-aware check (`Any(d => d.IsError)` or equivalent), even if all current diagnostics are errors.

## C3 — Release-build fallback can silently pick wrong return-slot placement
- **Severity:** High
- **Confidence:** High
- **Location:** `Blade/IR/Lir/LirLowerer.cs:345-357`
- **What I found:** `GetExtraResultPlacement` calls `Debug.Fail(...)` when lookup fails, then returns `ReturnPlacement.FlagC`.
- **Why this matters:** In release builds, `Debug.Fail` does not stop execution; invalid IR state can silently compile with incorrect ABI behavior.
- **Recommendation:** Replace fallback with an invariant-enforcing hard failure (`throw`/`Assert`) or redesign so impossible states are unrepresentable.

## C4 — Unexpected callee tier only triggers `Debug.Fail` and continues
- **Severity:** High
- **Confidence:** High
- **Location:** `Blade/IR/Asm/AsmLowerer.cs:1240-1242`
- **What I found:** Unexpected `CallingConventionTier` hits `Debug.Fail` and then `break`, emitting no call sequence.
- **Why this matters:** In release builds this can produce incomplete/wrong assembly without a fatal compiler error.
- **Recommendation:** Convert to explicit compiler diagnostic or exception path; do not continue codegen after invariant break.

## C5 — Recursive-call emission comment conflicts with emitted instruction
- **Severity:** Medium
- **Confidence:** Medium
- **Location:** `Blade/IR/Asm/AsmLowerer.cs:1231-1236`
- **What I found:** Comment says recursive tier should use `CALLB`, but implementation emits `CALL`.
- **Why this matters:** Either documentation is stale or implementation is wrong. In either case, this is a correctness-maintenance hazard.
- **Recommendation:** Resolve intent and align comment/code/tests. If `CALL` is correct, update comment; if not, fix lowering and add regression.

---

## X1 — Binder is a high-risk “god class”
- **Severity:** Medium
- **Confidence:** High
- **Location:** `Blade/Semantics/Binder.cs` (~3259 LOC)
- **What I found:** Binding, symbol registration, type rules, comptime support checks, import handling, query semantics, and diagnostics are tightly concentrated.
- **Why this matters:** Extremely high cognitive load, poor change isolation, and elevated risk of cross-feature regressions.
- **Recommendation:** Incrementally split by concern (declarations, expressions, statements, type-conversion rules, comptime policy).

## X2 — Parser size/branching complexity is high
- **Severity:** Medium
- **Confidence:** High
- **Location:** `Blade/Syntax/Parser.cs` (~1372 LOC)
- **What I found:** Large switch-driven parser with many branch points and recovery paths in one file.
- **Why this matters:** Hard to reason about grammar evolution and error recovery interactions.
- **Recommendation:** Extract focused parse units (declarations, statements, expressions, type grammar) with narrow helper APIs.

## X3 — `Program.cs` bundles CLI parsing, compilation orchestration, reporting, and serialization
- **Severity:** Low
- **Confidence:** High
- **Location:** `Blade/Program.cs` (~545 LOC)
- **What I found:** Multiple concerns live in one compilation unit (`Program`, `CommandLineOptions`, `OutputWriter`, JSON report model/builder).
- **Why this matters:** Increases incidental coupling; simple output/CLI changes risk touching compile flow code.
- **Recommendation:** Move to small services/files: CLI parse, run orchestration, text output, JSON report shaping.

## X4 — DiagnosticBag has large repetitive surface area
- **Severity:** Low
- **Confidence:** High
- **Location:** `Blade/Diagnostics/DiagnosticBag.cs` (~464 LOC)
- **What I found:** Hundreds of near-identical reporting methods.
- **Why this matters:** Boilerplate cost and easy drift between code/message/format rules.
- **Recommendation:** Centralize diagnostic descriptors (code + template) and keep typed wrappers only where they add value.

## X5 — Multiple “impossible default” branches remain in core lowering/binding paths
- **Severity:** Medium
- **Confidence:** Medium
- **Location:** e.g., `Blade/IR/Asm/AsmLowerer.cs`, `Blade/Semantics/Binder.cs`, `Blade/Semantics/ComptimeEvaluation.cs`
- **What I found:** Many default branches rely on debug-only checks or comments instead of hard invariants.
- **Why this matters:** Hidden release-mode behavior when invariants break can produce silent miscompilation.
- **Recommendation:** Continue replacing fallback defaults with explicit invariants + deterministic compiler failures.

---

## Priority order (fastest risk reduction first)

1. **Fix C3 + C4** (silent release-mode miscompile risks).
2. **Fix C1 + C2** (diagnostic contract correctness).
3. **Resolve C5** (comment vs implementation mismatch).
4. Start structural decomposition from **X1** and **X2**.
5. Tackle **X3/X4/X5** opportunistically during nearby feature work.
