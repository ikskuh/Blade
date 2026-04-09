using System.Collections.Generic;
using Blade.Semantics.Bound;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public sealed class ImportedModule
{
    public ImportedModule(
        string sourceName,
        string resolvedFilePath,
        CompilationUnitSyntax syntax,
        BoundProgram program,
        IReadOnlyDictionary<string, FunctionSymbol> exportedFunctions,
        IReadOnlyDictionary<string, TypeSymbol> exportedTypes,
        IReadOnlyDictionary<string, VariableSymbol> exportedVariables,
        IReadOnlyDictionary<string, ImportedModule> importedModules)
    {
        SourceName = Requires.NotNull(sourceName);
        ResolvedFilePath = Requires.NotNull(resolvedFilePath);
        Syntax = Requires.NotNull(syntax);
        Program = Requires.NotNull(program);
        ExportedFunctions = Requires.NotNull(exportedFunctions);
        ExportedTypes = Requires.NotNull(exportedTypes);
        ExportedVariables = Requires.NotNull(exportedVariables);
        ImportedModules = Requires.NotNull(importedModules);
    }

    public string SourceName { get; }
    public string ResolvedFilePath { get; }
    public CompilationUnitSyntax Syntax { get; }
    public BoundProgram Program { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> ExportedFunctions { get; }
    public IReadOnlyDictionary<string, TypeSymbol> ExportedTypes { get; }
    public IReadOnlyDictionary<string, VariableSymbol> ExportedVariables { get; }
    public IReadOnlyDictionary<string, ImportedModule> ImportedModules { get; }
}

internal sealed class ImportedModuleDefinition
{
    public ImportedModuleDefinition(
        string resolvedFilePath,
        CompilationUnitSyntax syntax,
        BoundProgram program,
        IReadOnlyDictionary<string, FunctionSymbol> exportedFunctions,
        IReadOnlyDictionary<string, TypeSymbol> exportedTypes,
        IReadOnlyDictionary<string, VariableSymbol> exportedVariables,
        IReadOnlyDictionary<string, ImportedModule> importedModules)
    {
        ResolvedFilePath = Requires.NotNull(resolvedFilePath);
        Syntax = Requires.NotNull(syntax);
        Program = Requires.NotNull(program);
        ExportedFunctions = Requires.NotNull(exportedFunctions);
        ExportedTypes = Requires.NotNull(exportedTypes);
        ExportedVariables = Requires.NotNull(exportedVariables);
        ImportedModules = Requires.NotNull(importedModules);
    }

    public string ResolvedFilePath { get; }
    public CompilationUnitSyntax Syntax { get; }
    public BoundProgram Program { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> ExportedFunctions { get; }
    public IReadOnlyDictionary<string, TypeSymbol> ExportedTypes { get; }
    public IReadOnlyDictionary<string, VariableSymbol> ExportedVariables { get; }
    public IReadOnlyDictionary<string, ImportedModule> ImportedModules { get; }

    public ImportedModule CreateImport(string sourceName)
    {
        return new ImportedModule(
            sourceName,
            ResolvedFilePath,
            Syntax,
            Program,
            ExportedFunctions,
            ExportedTypes,
            ExportedVariables,
            ImportedModules);
    }
}
