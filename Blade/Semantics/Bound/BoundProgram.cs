using System.Collections.Generic;
using Blade;
using Blade.Source;
using Blade.Syntax.Nodes;

namespace Blade.Semantics.Bound;

/// <summary>
/// Represents a fully bound program rooted at one source module and an explicit entrypoint task.
/// </summary>
public sealed class BoundProgram(
    BoundModule rootModule,
    TaskSymbol entryPoint,
    BoundFunctionMember entryPointFunction,
    TaskSymbol launcherEntryPoint,
    BoundFunctionMember launcherEntryPointFunction,
    IReadOnlyList<BoundModule> modules,
    IReadOnlyList<GlobalVariableSymbol> globalVariables,
    IReadOnlyList<BoundFunctionMember> functions) : BoundNode(BoundNodeKind.Program, ComputeSpan(entryPointFunction))
{
    /// <summary>
    /// Gets the root module for the compilation.
    /// </summary>
    public BoundModule RootModule { get; } = Requires.NotNull(rootModule);

    /// <summary>
    /// Gets the task identity that owns the program's root image.
    /// </summary>
    public TaskSymbol EntryPoint { get; } = Requires.NotNull(entryPoint);

    /// <summary>
    /// Gets the bound function member that starts executing in the emitted root image.
    /// </summary>
    public BoundFunctionMember EntryPointFunction { get; } = Requires.NotNull(entryPointFunction);

    /// <summary>
    /// Gets the runtime launcher task resolved during binding.
    /// </summary>
    public TaskSymbol LauncherEntryPoint { get; } = Requires.NotNull(launcherEntryPoint);

    /// <summary>
    /// Gets the bound function member that executes the runtime launcher body.
    /// </summary>
    public BoundFunctionMember LauncherEntryPointFunction { get; } = Requires.NotNull(launcherEntryPointFunction);

    /// <summary>
    /// Gets every bound module participating in the compilation.
    /// </summary>
    public IReadOnlyList<BoundModule> Modules { get; } = Requires.NotNull(modules);

    /// <summary>
    /// Gets all global storage variables reachable from the bound modules.
    /// </summary>
    public IReadOnlyList<GlobalVariableSymbol> GlobalVariables { get; } = Requires.NotNull(globalVariables);

    /// <summary>
    /// Gets all bound functions, including the selected entrypoint.
    /// </summary>
    public IReadOnlyList<BoundFunctionMember> Functions { get; } = Requires.NotNull(functions);

    /// <summary>
    /// Gets the resolved source file path of the root module.
    /// </summary>
    public string ResolvedFilePath => RootModule.ResolvedFilePath;

    /// <summary>
    /// Gets the parsed syntax of the root module.
    /// </summary>
    public CompilationUnitSyntax Syntax => RootModule.Syntax;

    private static TextSpan ComputeSpan(BoundFunctionMember entryPointFunction)
    {
        return Requires.NotNull(entryPointFunction).Body.Span;
    }
}
