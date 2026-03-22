# Blade/ Code Review Findings (Correctness + Unnecessary Complexity)

Date: 2026-03-22  
Scope reviewed: `Blade/` (parser, diagnostics, binder/import pipeline, CLI option parsing)

## Review method

- Manual source inspection focused on correctness risks and avoidable complexity.
- Built compiler project to ensure baseline is healthy (`dotnet build Blade/blade.csproj`, warning-free).

---

## Findings

## 1) **Critical correctness bug**: parser “no progress” guard can skip valid tokens

**Where**: `Blade/Syntax/Parser.cs` (`ParseCompilationUnit`)  
**Evidence**:

```csharp
Token startToken = Current;
MemberSyntax member = ParseMember();
...
if (Current == startToken)
    NextToken();
```

`Token` is a `record struct` (value equality), not a reference type. If parsing consumes one token and lands on a *different token with identical value fields* (`Kind`, `Span`, `Text`, `Value`), `Current == startToken` can still evaluate true and forcibly skip another token.

**Impact**:
- Silent token loss in recovery paths.
- Potential malformed ASTs and misleading follow-on diagnostics.

**Recommendation**:
Track parser progress via `_position` index (e.g., `int start = _position; ... if (_position == start) NextToken();`) instead of token value equality.

---

## 2) **High correctness bug**: imported-module diagnostics lose accurate source location

**Where**:
- `Blade/Diagnostics/Diagnostic.cs` (diagnostic stores only `TextSpan`)  
- `Blade/Program.cs` (diagnostic location resolved only against root compilation source)

**Evidence**:
- `Diagnostic` has `Code`, `Span`, `Message` only, no file/source reference.
- CLI printing path:

```csharp
SourceLocation loc = compilation.Source.GetLocation(diag.Span.Start);
Console.WriteLine($"{loc}: {diag}");
```

Binder reuses one shared `DiagnosticBag` across root + imported modules, so spans from imported files are interpreted relative to the root file.

**Impact**:
- Wrong file/line/column in diagnostics for import errors and nested module parse/bind errors.
- Debugging module issues becomes unreliable.

**Recommendation**:
Attach source identity to each diagnostic (file path or `SourceText`/`SourceId`) at report time, and resolve locations against that source when rendering.

---

## 3) **High correctness bug**: import diagnostics use synthetic `(0,0)` spans

**Where**: `Blade/Semantics/Binder.cs` (`LoadAndBindModule`)  
**Evidence**:

```csharp
_diagnostics.ReportImportFileNotFound(new TextSpan(0, 0), resolvedPath);
...
_diagnostics.ReportCircularImport(new TextSpan(0, 0), resolvedPath);
```

These diagnostics are detached from the actual `import ...` token span.

**Impact**:
- Diagnostics point to start-of-file instead of import site.
- Combined with Finding #2, these errors are particularly hard to trace.

**Recommendation**:
Pass caller/import-site span into `LoadAndBindModule` and report against that span.

---

## 4) **Medium correctness bug**: duplicate import aliases still overwrite module table

**Where**: `Blade/Semantics/Binder.cs` (`BindImports`)  
**Evidence**:

```csharp
_importedModules[alias] = imported;
if (!_globalScope.TryDeclare(new ModuleSymbol(alias, imported)))
    _diagnostics.ReportSymbolAlreadyDeclared(...);
```

and builtin branch similarly writes first, then checks declaration.

If alias collision occurs, scope declaration fails (correctly diagnosed) but `_importedModules[alias]` is still overwritten with later module.

**Impact**:
- Internal compiler state diverges from declared symbol table after error.
- Follow-on binding may observe wrong module under conflicting alias.

**Recommendation**:
Attempt declaration first; only write to `_importedModules` on successful declaration.

---

## 5) **Unnecessary complexity**: CLI option parser has repeated error-gate boilerplate

**Where**: `Blade/CompilationOptionsCommandLine.cs` (`TryParse`)  
**Evidence**: loop repeatedly does:

```csharp
if (TryParseX(..., out ..., out errorMessage)) { ... continue; }
if (errorMessage is not null) { options = new CompilationOptions(); return false; }
```

This pattern is repeated for fuel, optimization directives, and module specs.

**Impact**:
- Harder to audit control flow and future parser changes.
- Easy to introduce inconsistent behavior when adding new options.

**Recommendation**:
Refactor into a small staged dispatcher returning enum/result objects (`Matched`, `NoMatch`, `Error(message)`), then centralize one failure path.

---

## Suggested remediation order

1. Fix parser progress guard (Finding #1) — prevents silent token skips.
2. Fix diagnostic source tracking + import-site spans (Findings #2 + #3).
3. Prevent alias overwrite on duplicate imports (Finding #4).
4. Refactor CLI parse flow for maintainability (Finding #5).

---

## Baseline check run during review

- `dotnet build Blade/blade.csproj` ✅ (0 warnings, 0 errors)
