# Consolidated Blade Code Review Findings

## Correctness findings

### C2. CLI usage text omits `--comptime-fuel`
- **Severity:** Medium
- **Location:** `Blade/Program.cs`
- **Summary:** The option parser accepts `--comptime-fuel=...`, but `PrintUsage()` does not document it.
- **Impact:** Real functionality is present but discoverability is broken.
- **Recommendation:** Document `--comptime-fuel=<positive-int>` in CLI usage text.
- **Source(s):** Run1

### C5. Output write errors are only partially guarded
- **Severity:** Medium
- **Location:** `Blade/Program.cs` (`OutputWriter`)
- **Summary:** Text/JSON output helpers catch only `IOException` and `UnauthorizedAccessException`.
- **Impact:** Other common path-related exceptions can escape and crash the process.
- **Recommendation:** Catch additional expected path exceptions or pre-validate output paths.
- **Source(s):** Run1

### C7. Imported-module diagnostics lose accurate source location
- **Severity:** High
- **Location:** `Blade/Diagnostics/Diagnostic.cs`, `Blade/Program.cs`
- **Summary:** Diagnostics carry only a `TextSpan`, while rendering resolves spans only against the root compilation source.
- **Impact:** Errors originating in imported modules can be reported with the wrong file/line/column.
- **Recommendation:** Attach source identity to diagnostics and resolve locations against the originating source.
- **Source(s):** Run2

### C8. Import diagnostics use synthetic `(0,0)` spans
- **Severity:** High
- **Location:** `Blade/Semantics/Binder.cs` (`LoadAndBindModule`)
- **Summary:** Some import errors are reported using `new TextSpan(0, 0)` instead of the span of the `import` site.
- **Impact:** Even when the right file is known, the diagnostic points to the beginning of the file rather than the offending import.
- **Recommendation:** Thread the caller/import-site span into `LoadAndBindModule` and report against it.
- **Source(s):** Run2

### C9. Duplicate import aliases overwrite the module table even after a declaration failure
- **Severity:** Medium
- **Location:** `Blade/Semantics/Binder.cs` (`BindImports`)
- **Summary:** `_importedModules[alias] = imported` happens before the declaration conflict check.
- **Impact:** Internal state can diverge from the declared symbol table after an alias collision.
- **Recommendation:** Only write `_importedModules` after successful declaration.
- **Source(s):** Run2


### C11. `HasErrors` treats any diagnostic as an error
- **Severity:** Medium
- **Location:** `Blade/Diagnostics/DiagnosticBag.cs`
- **Summary:** `HasErrors` is implemented as `_diagnostics.Count > 0`.
- **Impact:** Once warnings/info exist, pipelines may abort too aggressively.
- **Recommendation:** Make `HasErrors` severity-aware.
- **Source(s):** Run3

### C12. Release-build fallback can silently choose the wrong return-slot placement
- **Severity:** High
- **Location:** `Blade/IR/Lir/LirLowerer.cs`
- **Summary:** `GetExtraResultPlacement` uses `Debug.Fail(...)` and then returns `ReturnPlacement.FlagC`.
- **Impact:** Release builds may silently miscompile invalid IR states.
- **Recommendation:** Replace the fallback with an invariant-enforcing failure (`throw`, hard assert, or unrepresentable state design).
- **Source(s):** Run3

### C13. Unexpected callee tier only triggers `Debug.Fail` and then continues
- **Severity:** High
- **Location:** `Blade/IR/Asm/AsmLowerer.cs`
- **Summary:** An unexpected `CallingConventionTier` hits `Debug.Fail` and then `break`s without a fatal compiler path.
- **Impact:** Release builds may emit incomplete or incorrect assembly rather than failing deterministically.
- **Recommendation:** Convert the branch into an explicit diagnostic or exception path.
- **Source(s):** Run3

### C16. Flag annotation parsing is inconsistent between return specs and asm output bindings
- **Severity:** Medium
- **Location:** parser logic around return-item flags vs asm output binding flags
- **Summary:** One path accepts `SyntaxFacts.IsIdentifierLike(...)`; the other requires a strict identifier token.
- **Impact:** Similar grammar constructs accept different token sets, causing inconsistent user-facing syntax.
- **Recommendation:** Use a shared helper for flag-name parsing.
- **Source(s):** Run4

### C17. `IsIdentifierLike` is broader than its documented intent
- **Severity:** Medium
- **Location:** `Blade/Syntax/SyntaxFacts.cs`
- **Summary:** `IsIdentifierLike` accepts identifiers plus any keyword in the keyword table.
- **Impact:** Contextual-name positions may admit unintended keywords, increasing ambiguity and weakening diagnostics.
- **Recommendation:** Narrow it to explicit allowlists or split it into context-specific predicates.
- **Source(s):** Run4

---

## Unnecessary complexity / maintainability findings

### X1. `Program.cs` mixes too many concerns
- **Severity:** Low to Medium
- **Location:** `Blade/Program.cs`
- **Summary:** CLI parsing, orchestration, reporting, serialization, and output plumbing are bundled together.
- **Impact:** Small changes in output or command-line behavior risk touching core compile flow.
- **Recommendation:** Split CLI parsing, orchestration, and report/output shaping into separate units.
- **Source(s):** Run1, Run3

### X2. `AsmLowerer` is very large and contains placeholder or TODO lowering paths
- **Severity:** Medium
- **Location:** `Blade/IR/Asm/AsmLowerer.cs`
- **Summary:** The lowering surface is large and includes placeholder paths such as comment-emission handling for unsupported operations.
- **Impact:** Harder to reason about invariants and future regressions.
- **Recommendation:** Partition by concern and replace placeholder lowering with explicit diagnostics plus tests.
- **Source(s):** Run1

### X3. Binder is oversized / “god class” territory
- **Severity:** Medium
- **Location:** `Blade/Semantics/Binder.cs`
- **Summary:** Binding, symbol registration, type rules, imports, comptime rules, and diagnostics are concentrated in one large class.
- **Impact:** High cognitive load and increased cross-feature regression risk.
- **Recommendation:** Extract declaration, expression, statement, import, and conversion/type-policy concerns into focused binders or helpers.
- **Source(s):** Run1, Run3

### X4. CLI option parsing has repeated boilerplate error-gating
- **Severity:** Medium
- **Location:** `Blade/CompilationOptionsCommandLine.cs`
- **Summary:** Option parsing repeatedly follows the same match/error/continue pattern.
- **Impact:** Harder to audit and easier to introduce inconsistent behavior.
- **Recommendation:** Refactor parsing into a staged dispatcher or result object model.
- **Source(s):** Run2

### X5. Parser size and branching complexity are high
- **Severity:** Medium
- **Location:** `Blade/Syntax/Parser.cs`
- **Summary:** The parser centralizes many branch-heavy grammar and recovery paths in one file.
- **Impact:** Grammar evolution and error recovery are harder to reason about safely.
- **Recommendation:** Extract declarations, statements, expressions, and type grammar into focused units.
- **Source(s):** Run3

### X6. `DiagnosticBag` has a large repetitive reporting surface
- **Severity:** Low
- **Location:** `Blade/Diagnostics/DiagnosticBag.cs`
- **Summary:** There are many near-identical reporting methods.
- **Impact:** Boilerplate drift risk and extra maintenance cost.
- **Recommendation:** Centralize descriptors/templates and keep typed wrappers only where they add real value.
- **Source(s):** Run3

### X7. Multiple “impossible default” branches rely on debug-only checks
- **Severity:** Medium
- **Location:** e.g. `Blade/IR/Asm/AsmLowerer.cs`, `Blade/Semantics/Binder.cs`, `Blade/Semantics/ComptimeEvaluation.cs`
- **Summary:** Some default branches rely on `Debug.Fail`, comments, or fallback behavior instead of deterministic failures.
- **Impact:** Invalid states can be masked in release builds.
- **Recommendation:** Replace debug-only fallbacks with explicit invariants and deterministic compiler failures.
- **Source(s):** Run3

### X8. `hasEscapeErrors` in `ReadString` is dead state
- **Severity:** Low
- **Location:** `Blade/Syntax/Lexer.cs`
- **Summary:** A variable is set but never used for behavior.
- **Impact:** Adds noise in a performance-sensitive code path and suggests missing behavior.
- **Recommendation:** Remove it or wire it into real control flow.
- **Source(s):** Run4

### X9. Raw-asm body capture logic is duplicated in two parser methods
- **Severity:** Low
- **Location:** parser logic for asm function declarations and asm block statements
- **Summary:** Two methods implement near-identical brace-depth raw-body capture loops.
- **Impact:** Fixes can drift between the two implementations.
- **Recommendation:** Extract a shared helper.
- **Source(s):** Run4

