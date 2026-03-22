# Blade/ Code Review Findings (2026-03-22)

Scope: review of files under `Blade/` with focus on correctness and unnecessary complexity.

## Executive Summary

- **High-priority correctness issues found:** 3
- **Medium-priority correctness issues found:** 2
- **Complexity / maintainability findings:** 3

---

## Correctness Findings

### 1) `--dump-final-asm` is ignored in JSON dump payloads
- **Severity:** High
- **Location:** `Blade/Program.cs`
- **Evidence:** `CommandLineOptions` has `DumpFinalAsm`, and parser sets it; however `BuildJsonDumps` does not allocate or populate a `final-asm` key.
- **Why this is a problem:** JSON callers cannot request final-asm in the same way as other dump channels, despite CLI exposing `--dump-final-asm`.
- **Details:**
  - `DumpFinalAsm` is parsed and stored (`CommandLineOptions`).
  - `BuildJsonDumps` only emits: `bound`, `mir-preopt`, `mir`, `lir-preopt`, `lir`, `asmir-preopt`, `asmir`.
- **Recommendation:** Add `final-asm` to JSON dump keys and wire to `buildResult.AssemblyText` when `options.DumpFinalAsm` is true.

### 2) CLI usage text omits `--comptime-fuel`
- **Severity:** Medium
- **Location:** `Blade/Program.cs`
- **Evidence:** `CompilationOptionsCommandLine` parses `--comptime-fuel=...`, but `PrintUsage()` does not document it.
- **Why this is a problem:** Valid functionality is discoverability-hidden, causing false assumption that feature is unavailable.
- **Recommendation:** Add one usage line documenting `--comptime-fuel=<positive-int>`.

### 3) Potential uncaught exception in compile-time bool normalization
- **Severity:** High
- **Location:** `Blade/Semantics/ComptimeEvaluation.cs`
- **Evidence:** `ComptimeTypeFacts.TryNormalizeValue` converts any `IConvertible` to `Int64` for bool target via `Convert.ToInt64(convertible, ...)` with no exception handling.
- **Why this is a problem:** Overflow or invalid conversions can throw (`OverflowException`, `FormatException`, etc.), violating the apparent `Try*` contract and potentially aborting compilation instead of producing diagnostics.
- **Recommendation:** Use a non-throwing conversion path or catch conversion exceptions and return `false`.

### 4) `\u{...}` escape accepts code points outside valid Unicode scalar range
- **Severity:** High
- **Location:** `Blade/Syntax/Lexer.cs`
- **Evidence:** Escape parser accepts 1–6 hex digits and directly returns parsed value; no check for `> 0x10FFFF` or UTF-16 surrogate range `0xD800..0xDFFF`.
- **Why this is a problem:** Invalid scalar values can enter token payloads and later stages, creating inconsistent behavior and possibly invalid emitted output.
- **Recommendation:** Validate scalar range and reject invalid code points with `ReportInvalidEscapeSequence`.

### 5) Output write errors are only partially guarded
- **Severity:** Medium
- **Location:** `Blade/Program.cs` (`OutputWriter`)
- **Evidence:** `TryWriteText` / `TryWriteJson` catch only `IOException` and `UnauthorizedAccessException`.
- **Why this is a problem:** Path-related exceptions like `ArgumentException`, `NotSupportedException`, or `PathTooLongException` can escape and crash the process.
- **Recommendation:** Expand catch filter to include other expected path errors or pre-validate output paths.

---

## Unnecessary Complexity Findings

### 6) `Program.cs` combines CLI parse, report serialization, and output plumbing
- **Severity:** Medium
- **Location:** `Blade/Program.cs` (545 lines)
- **Why this is a problem:** Multiple concerns in one file/type cluster increase cognitive load and regression risk for small changes.
- **Recommendation:** Split into focused files/classes (argument parsing, report model, report writing).

### 7) `AsmLowerer` is very large and includes placeholder lowering paths
- **Severity:** Medium
- **Location:** `Blade/IR/Asm/AsmLowerer.cs` (1812 lines)
- **Evidence:** Giant lowering surface plus `TODO` comment-emit paths for `yield`/`yieldto`.
- **Why this is a problem:** Harder to reason about invariants and control-flow-specific correctness; TODO pathways are easy to miss during future refactors.
- **Recommendation:** Partition by concern (terminators, pseudo-ops, calls, arithmetic lowering) and promote unsupported ops into explicit diagnostics + tests.

### 8) `Binder` size suggests high branch complexity concentration
- **Severity:** Medium
- **Location:** `Blade/Semantics/Binder.cs` (3259 lines)
- **Why this is a problem:** Large semantic front-end class makes localized changes risky and raises chance of subtle interactions between binding paths.
- **Recommendation:** Extract focused binders (types, statements, expressions, symbols/imports) and keep shared state in a small context object.

---

## Suggested Fix Order

1. Fix JSON `final-asm` dump parity.
2. Harden compile-time value normalization (`TryNormalizeValue`) against throwing conversions.
3. Validate Unicode scalar range for `\u{...}` escapes.
4. Improve CLI help (`--comptime-fuel`) and output-path exception handling.
5. Stage structural refactors for `Program.cs`, `AsmLowerer`, `Binder` once correctness fixes are merged.
