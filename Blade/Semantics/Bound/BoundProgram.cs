using System.Collections.Generic;
using Blade;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

public sealed class BoundProgram(
    BoundModule rootModule,
    IReadOnlyList<BoundModule> modules,
    IReadOnlyList<GlobalVariableSymbol> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions) : BoundNode(BoundNodeKind.Program, ComputeSpan(rootModule))
{
    public BoundModule RootModule { get; } = Requires.NotNull(rootModule);
    public IReadOnlyList<BoundModule> Modules { get; } = Requires.NotNull(modules);
    public IReadOnlyList<GlobalVariableSymbol> GlobalVariables { get; } = Requires.NotNull(globalVariables);
    public IReadOnlyList<BoundFunctionMember> Functions { get; } = Requires.NotNull(functions);

    public string ResolvedFilePath => RootModule.ResolvedFilePath;
    public CompilationUnitSyntax Syntax => RootModule.Syntax;
    public IReadOnlyList<BoundStatement> TopLevelStatements => RootModule.TopLevelStatements;

    private static TextSpan ComputeSpan(BoundModule rootModule)
    {
        Requires.NotNull(rootModule);
        return rootModule.TopLevelStatements.Count > 0
            ? TextSpan.FromBounds(rootModule.TopLevelStatements[0].Span.Start, rootModule.TopLevelStatements[^1].Span.End)
            : new TextSpan(0, 0);
    }
}
