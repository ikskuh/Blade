using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blade.Diagnostics;
using Blade.Semantics;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade;

internal static class CompilationModuleLoader
{
    public static LoadedCompilation Load(
        SourceText rootSource,
        SourceText runtimeLauncherSource,
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<string, string> namedModuleRoots)
    {
        Requires.NotNull(rootSource);
        Requires.NotNull(runtimeLauncherSource);
        Requires.NotNull(diagnostics);
        Requires.NotNull(namedModuleRoots);

        Dictionary<string, LoadedModule> modulesByFullPath = new(PathIdentity.Comparer);
        Dictionary<string, string> namedModuleOwners = new(PathIdentity.Comparer);
        Queue<string> work = new();

        LoadedModule root = LoadModuleFromSource(
            rootSource,
            diagnostics,
            namedModuleRoots,
            namedModuleOwners,
            out List<string> newlyDiscoveredModules);
        modulesByFullPath[root.FullPath] = root;

        foreach (string modulePath in newlyDiscoveredModules)
            work.Enqueue(modulePath);

        LoadedModule runtimeLauncherModule = LoadModuleFromSource(
            runtimeLauncherSource,
            diagnostics,
            namedModuleRoots,
            namedModuleOwners,
            out newlyDiscoveredModules);
        modulesByFullPath[runtimeLauncherModule.FullPath] = runtimeLauncherModule;

        foreach (string modulePath in newlyDiscoveredModules)
            work.Enqueue(modulePath);

        while (work.Count > 0)
        {
            string modulePath = work.Dequeue();
            if (modulesByFullPath.ContainsKey(modulePath))
                continue;

            bool loaded = SourceFileLoader.TryLoad(modulePath, diagnostics, out SourceText source);
            _ = loaded; // Diagnostics were already reported; we still attempt to parse the file to surface syntax issues.

            LoadedModule module = LoadModuleFromSource(
                source,
                diagnostics,
                namedModuleRoots,
                namedModuleOwners,
                out newlyDiscoveredModules);
            modulesByFullPath[module.FullPath] = module;

            foreach (string discovered in newlyDiscoveredModules)
                work.Enqueue(discovered);
        }

        return new LoadedCompilation(root, runtimeLauncherModule, modulesByFullPath);
    }

    private static LoadedModule LoadModuleFromSource(
        SourceText source,
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<string, string> namedModuleRoots,
        Dictionary<string, string> namedModuleOwners,
        out List<string> newlyDiscoveredModules)
    {
        Requires.NotNull(source);
        Requires.NotNull(diagnostics);
        Requires.NotNull(namedModuleRoots);
        Requires.NotNull(namedModuleOwners);

        string fullPath = Path.GetFullPath(source.FilePath);

        Parser parser = Parser.Create(source, diagnostics);
        CompilationUnitSyntax unit = parser.ParseCompilationUnit();

        List<LoadedImport> imports = [];
        newlyDiscoveredModules = [];

        foreach (ImportDeclarationSyntax import in unit.Members.OfType<ImportDeclarationSyntax>())
        {
            using IDisposable _ = diagnostics.UseSource(source);

            if (import.IsFileImport && import.Alias is null)
                diagnostics.Report(new FileImportAliasRequiredError(diagnostics.CurrentSource, import.Source.Span));

            string alias = import.Alias?.Text ?? import.Source.Text;
            string sourceName = import.IsFileImport
                ? DecodeUtf8Literal(import.Source)
                : import.Source.Text;

            if (!import.IsFileImport && string.Equals(sourceName, "builtin", StringComparison.Ordinal))
            {
                imports.Add(new LoadedImport(import, alias, LoadedImportKind.Builtin, ResolvedFullPath: null));
                continue;
            }

            string? resolvedFullPath = TryResolveImportPath(
                import,
                fullPath,
                sourceName,
                namedModuleRoots,
                namedModuleOwners,
                diagnostics);
            imports.Add(new LoadedImport(import, alias, LoadedImportKind.File, resolvedFullPath));

            if (resolvedFullPath is not null)
                newlyDiscoveredModules.Add(resolvedFullPath);
        }

        return new LoadedModule(fullPath, source, unit, parser.TokenCount, imports);
    }

    private static string? TryResolveImportPath(
        ImportDeclarationSyntax import,
        string importerFullPath,
        string sourceName,
        IReadOnlyDictionary<string, string> namedModuleRoots,
        Dictionary<string, string> namedModuleOwners,
        DiagnosticBag diagnostics)
    {
        Requires.NotNull(import);
        Requires.NotNull(importerFullPath);
        Requires.NotNull(sourceName);
        Requires.NotNull(namedModuleRoots);
        Requires.NotNull(namedModuleOwners);
        Requires.NotNull(diagnostics);

        string resolvedFullPath;
        if (import.IsFileImport)
        {
            string? importerDir = Path.GetDirectoryName(importerFullPath);
            Assert.Invariant(importerDir is not null, "Imported file paths must have a containing directory.");
            resolvedFullPath = Path.GetFullPath(Path.Combine(importerDir, sourceName));
        }
        else
        {
            if (!namedModuleRoots.TryGetValue(sourceName, out string? namedModulePath))
            {
                diagnostics.Report(new UnknownNamedModuleError(diagnostics.CurrentSource, import.Source.Span, sourceName));
                return null;
            }

            resolvedFullPath = Path.GetFullPath(namedModulePath);
            if (namedModuleOwners.TryGetValue(resolvedFullPath, out string? ownerName))
            {
                if (!string.Equals(ownerName, sourceName, StringComparison.Ordinal))
                {
                    diagnostics.Report(new DuplicateNamedModuleRootError(diagnostics.CurrentSource, import.Source.Span, ownerName, sourceName, resolvedFullPath));
                    return null;
                }
            }
            else
            {
                namedModuleOwners[resolvedFullPath] = sourceName;
            }
        }

        if (!File.Exists(resolvedFullPath))
        {
            diagnostics.Report(new ImportFileNotFoundError(diagnostics.CurrentSource, import.Source.Span, resolvedFullPath));
            return null;
        }

        return resolvedFullPath;
    }

    private static string DecodeUtf8Literal(Token token)
    {
        if (token.Value is BladeValue value
            && value.TryGetU8Array(out byte[] bytes))
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        return Assert.UnreachableValue<string>($"Token '{token.Kind}' must carry a UTF-8 byte-array literal value."); // pragma: force-coverage
    }
}

internal enum LoadedImportKind
{
    File,
    Builtin,
}

internal sealed class LoadedCompilation(LoadedModule rootModule, LoadedModule runtimeLauncherModule, IReadOnlyDictionary<string, LoadedModule> modulesByFullPath)
{
    public LoadedModule RootModule { get; } = Requires.NotNull(rootModule);
    public LoadedModule RuntimeLauncherModule { get; } = Requires.NotNull(runtimeLauncherModule);
    public IReadOnlyDictionary<string, LoadedModule> ModulesByFullPath { get; } = Requires.NotNull(modulesByFullPath);
}

internal sealed class LoadedModule(
    string fullPath,
    SourceText source,
    CompilationUnitSyntax syntax,
    int tokenCount,
    IReadOnlyList<LoadedImport> imports)
{
    public string FullPath { get; } = Requires.NotNull(fullPath);
    public SourceText Source { get; } = Requires.NotNull(source);
    public CompilationUnitSyntax Syntax { get; } = Requires.NotNull(syntax);
    public int TokenCount { get; } = Requires.NonNegative(tokenCount);
    public IReadOnlyList<LoadedImport> Imports { get; } = Requires.NotNull(imports);
}

internal readonly record struct LoadedImport(
    ImportDeclarationSyntax Syntax,
    string Alias,
    LoadedImportKind Kind,
    string? ResolvedFullPath);
