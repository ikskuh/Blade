using System.Collections.Generic;
using Blade.Semantics.Bound;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public sealed class ImportedModule
{
    public ImportedModule(
        string sourceName,
        string resolvedFilePath,
        string defaultAlias,
        CompilationUnitSyntax syntax,
        BoundProgram program,
        IReadOnlyDictionary<string, FunctionSymbol> exportedFunctions,
        IReadOnlyDictionary<string, TypeSymbol> exportedTypes,
        IReadOnlyDictionary<string, VariableSymbol> exportedVariables,
        IReadOnlyDictionary<string, ImportedModule> importedModules)
    {
        SourceName = Requires.NotNull(sourceName);
        ResolvedFilePath = Requires.NotNull(resolvedFilePath);
        DefaultAlias = Requires.NotNull(defaultAlias);
        Syntax = Requires.NotNull(syntax);
        Program = Requires.NotNull(program);
        ExportedFunctions = Requires.NotNull(exportedFunctions);
        ExportedTypes = Requires.NotNull(exportedTypes);
        ExportedVariables = Requires.NotNull(exportedVariables);
        ImportedModules = Requires.NotNull(importedModules);
    }

    public string SourceName { get; }
    public string ResolvedFilePath { get; }
    public string DefaultAlias { get; }
    public CompilationUnitSyntax Syntax { get; }
    public BoundProgram Program { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> ExportedFunctions { get; }
    public IReadOnlyDictionary<string, TypeSymbol> ExportedTypes { get; }
    public IReadOnlyDictionary<string, VariableSymbol> ExportedVariables { get; }
    public IReadOnlyDictionary<string, ImportedModule> ImportedModules { get; }
}
