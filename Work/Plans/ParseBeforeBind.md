# Short-Circuit Binder On Load/Parse Errors + Diagnostic Source Identity + Circular Imports (Rejected In Binder)

**Summary**
Refactor compilation into an explicit “load/parse/resolve imports” stage that must succeed before binding. If that stage emits any errors (lexer/parser/invalid source/import resolution), return immediately with diagnostics and skip binder + IR. Separately, keep **circular imports allowed in the module graph**, but **reject them during binding** (so we can support cycles later). Implement **REVIEW-C7** by attaching the originating `SourceText` to each diagnostic and rendering diagnostics against that source.

## Key Behavior Changes
1. **No binder execution on load-stage errors**
   - Load stage includes: `SourceFileLoader` validation, lex/parse, and *import resolution failures* (missing file, unknown named module, file-import missing alias, duplicate named-module root ownership).
   - If `diagnostics.HasErrors` after load stage: return `CompilationResult` with:
     - root `Source` + root `Syntax`
     - an **empty** `BoundProgram`
     - `IrBuildResult = null`
     - collected diagnostics (with correct file identity)
2. **Circular imports**
   - Loader builds the module table/graph without erroring on cycles (it must terminate via visited-set).
   - Binder detects circular imports and emits `E0231_CircularImport` at the **import-site span** (prefer `import.Source.Span`), and treats that import as an empty module.
3. **REVIEW-C7: diagnostics know their file**
   - `Diagnostic` carries `SourceText Source`.
   - All rendering (stdio/json/regression harness) uses `diagnostic.Source.GetLocation(diagnostic.Span.Start)`.

## Implementation Changes

### 1) Diagnostics: attach source identity (REVIEW-C7)
- Update `Diagnostic` to include `public SourceText Source { get; }` and take it in the constructor.
- Update `DiagnosticBag` to maintain a scoped “current source”:
  - Add `IDisposable UseSource(SourceText source)` that pushes/restores a `_currentSource`.
  - `Report(...)` asserts `_currentSource != null` and constructs `new Diagnostic(_currentSource, code, span, message)`.
- Ensure all stages set the source context:
  - Update `SourceFileLoader.TryLoad` and `SourceFileLoader.Validate` to wrap any diagnostic emission in `using diagnostics.UseSource(source)`.
  - Update `Parser.Create(SourceText, DiagnosticBag)` to wrap lexing in `using diagnostics.UseSource(source)` so lexer/parser diagnostics always have the correct source.
  - In `CompilerDriver`, wrap the IR stage (`IrPipeline.Build`) in `using diagnostics.UseSource(rootSource)` so backend diagnostics at least default to the root file.

- Update renderers / consumers:
  - `StdioOutputWriter`, `JsonReportBuilder`, and `Blade.Regressions/RegressionRunner` switch from `compilation.Source.GetLocation(...)` to `diagnostic.Source.GetLocation(...)`.

### 2) New load/parse/resolve-imports stage (outside binder)
Introduce an internal loader (e.g. `CompilationModuleLoader`) that produces:
- `LoadedCompilation`:
  - `LoadedModule Root`
  - `IReadOnlyDictionary<string, LoadedModule> ModulesByFullPath`
  - Root token count for metrics
- `LoadedModule` includes:
  - `string FullPath` (absolute)
  - `SourceText Source`
  - `CompilationUnitSyntax Syntax`
  - `int TokenCount`
  - `IReadOnlyList<LoadedImport> Imports` (pre-resolved)
- `LoadedImport` includes:
  - `ImportDeclarationSyntax Syntax` (for spans/tokens)
  - `string Alias` (or empty if missing; still report error)
  - `ImportKind` (`Builtin` or `File`)
  - `string SourceName` (decoded file path or named module identifier)
  - `string? ResolvedFullPath` (null only for builtin)

Loader algorithm (decision-complete):
- Start with root module (already loaded source).
- Parse each module, collect its `ImportDeclarationSyntax` list.
- Resolve each import:
  - File import: decode string literal to UTF-8 string; resolve to `Path.GetFullPath(Path.Combine(importerDir, decoded))`.
    - If alias missing: report `E0228_FileImportAliasRequired` at `import.Source.Span` (still continue loading target if path resolvable).
    - If file missing: report `E0230_ImportFileNotFound` at `import.Source.Span`; do not enqueue.
  - Named module:
    - If `sourceName == "builtin"`: mark builtin, no file.
    - Else look up in `CompilationOptions.NamedModuleRoots`:
      - missing: report `E0229_UnknownNamedModule` at `import.Source.Span`
      - present: resolve full path; if file missing: report `E0230_ImportFileNotFound` at `import.Source.Span`
      - enforce “named module owner” consistency:
        - keep `Dictionary<resolvedPath, ownerName>`; if the same path is claimed by a different module name, report `E...DuplicateNamedModuleRoot` at `import.Source.Span` (use existing `ReportDuplicateNamedModuleRoot`).
- Enqueue/load/parse each resolved module file once (cache by absolute path).
- **Cycle handling in loader**: maintain `visited` set; if import resolves to a path already in `visited`, still record the edge but do not recurse again (no diagnostics).

### 3) CompilerDriver refactor: gate binder on loader errors
In `CompilerDriver.CompileCore`:
1. Load stage: call loader to obtain `LoadedCompilation` (root syntax + module table) and populate `diagnostics`.
2. If `diagnostics.HasErrors`: return early with empty `BoundProgram`, `IrBuildResult = null`.
3. Else: call new binder entrypoint with `LoadedCompilation`.
4. IR stage unchanged except for the `UseSource(rootSource)` scoping noted above.

### 4) Binder refactor: no IO/parse, assumes well-formed syntax
- Replace `Binder.Bind(...)` signature with:
  - `Binder.Bind(LoadedCompilation compilation, DiagnosticBag diagnostics, int comptimeFuel = 250)`
- Binder no longer:
  - calls `SourceFileLoader.TryLoad`
  - creates `Parser`
  - checks `File.Exists`
- Imports binding:
  - Use `LoadedModule.Imports` (already resolved by loader) to bind imports.
  - For `Builtin`: keep current behavior.
  - For file/named-module imports: look up `ResolvedFullPath` in `LoadedCompilation.ModulesByFullPath`.
    - This should always succeed if binder is reached; use `Assert.Invariant` if not.
  - Circular import detection:
    - Keep `_moduleBindingStack` (path set).
    - If importing module path already in stack, emit `ReportCircularImport(import.Syntax.Source.Span, resolvedPath)` and return an empty imported module for that import.
- “Binder assumes syntax correct” cleanups:
  - Remove “skip on empty name” patterns like `if (string.IsNullOrWhiteSpace(syntax.Name.Text)) continue;` and replace with `Assert.Invariant(...)` (since binder is now gated behind an error-free parser).

## Tests / Acceptance Criteria
1. **Binder not executed on syntax errors**
   - New regression or unit test: root file missing identifier in `var` declaration must produce parser diagnostic(s) and must not throw (and must not emit binder-only diagnostics).
2. **Binder not executed on import resolution errors**
   - New test: `import "./missing.blade" as m;` emits `E0230` and compilation returns without binding (no bound program work beyond the empty placeholder result).
3. **Imported-module syntax error stops binder**
   - Fixture: main imports a file containing a parser error. Expect only syntax diagnostics; no binder/IR.
4. **Circular import allowed structurally but rejected in binder**
   - Fixture: `a` imports `b`, `b` imports `a`; parsing succeeds; binder emits `E0231` at the import site span; compilation does not hang.
5. **REVIEW-C7 coverage**
   - New unit test: compile a main file that imports a file with a syntax error; assert the emitted `Diagnostic.Source.FilePath` is the imported file’s path and that the computed line matches the imported file’s line.

## Public API / Data Shape Changes
- `Diagnostic` gains `SourceText Source`.
- `Binder.Bind(...)` signature changes to accept `LoadedCompilation` (internal type) instead of raw root `CompilationUnitSyntax` + `rootFilePath` + `namedModuleRoots`.
- Internally introduce `LoadedCompilation` / `LoadedModule` / `LoadedImport` types.

## Assumptions (locked)
- Binder is gated on **any error during the load stage** (parse + import resolution), matching your preference that “binder should not be reached when an import error has happened.”
- Loader does **not** report circular-import diagnostics; binder does, so future “support cycles” work remains possible.
