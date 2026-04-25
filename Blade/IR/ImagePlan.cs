using System.Collections.Generic;
using System.Linq;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR;

/// <summary>
/// Represents the image-level decomposition of one compiled program.
/// The planner starts from the bound entry task, follows normal calls to find the code
/// that belongs to each task, and follows <c>spawn</c>/<c>spawnpair</c> edges to discover
/// additional task images. The result is the program-level source of truth for which
/// images exist, which one is the file entry image, and which task-owned symbols belong
/// to each image.
/// </summary>
public sealed class ImagePlan
{
    public ImagePlan(IReadOnlyList<ImageDescriptor> images, ImageDescriptor entryImage)
    {
        Requires.NotNull(images);
        Requires.NotNull(entryImage);
        Requires.That(images.Contains(entryImage));

        this.Images = images;
        this.EntryImage = entryImage;
    }

    /// <summary>
    /// Gets every image that must be emitted for the program.
    /// </summary>
    public IReadOnlyList<ImageDescriptor> Images { get; }

    /// <summary>
    /// Gets the image that starts at the beginning of the output file and therefore
    /// represents the program's initial execution context.
    /// </summary>
    public ImageDescriptor EntryImage { get; }
}

/// <summary>
/// Describes one concrete task image inside an <see cref="ImagePlan"/>.
/// An image is the compiler's execution unit for a task: it captures which task owns
/// the image, which function starts execution, which execution mode the image uses,
/// whether it is the entry image, and which reachable functions and storage symbols
/// must be present when that task is emitted.
/// </summary>
public sealed class ImageDescriptor(
    TaskSymbol task,
    FunctionSymbol entryFunction,
    VariableStorageClass executionMode,
    bool isEntryImage,
    IReadOnlyList<FunctionSymbol> functions,
    IReadOnlyList<GlobalVariableSymbol> storage)
{
    /// <summary>
    /// Gets the task declaration that owns this image.
    /// </summary>
    public TaskSymbol Task { get; } = Requires.NotNull(task);

    /// <summary>
    /// Gets the lowered entry function that starts executing when the image is launched.
    /// </summary>
    public FunctionSymbol EntryFunction { get; } = Requires.NotNull(entryFunction);

    /// <summary>
    /// Gets the execution mode selected by the task declaration and therefore the mode
    /// in which this image begins running.
    /// </summary>
    public VariableStorageClass ExecutionMode { get; } = executionMode;

    /// <summary>
    /// Gets a value indicating whether this image is emitted at the start of the output
    /// and entered directly by the runtime.
    /// </summary>
    public bool IsEntryImage { get; } = isEntryImage;

    /// <summary>
    /// Gets the functions that are reachable from this image's entry function by normal
    /// call edges and therefore belong to the image's code body.
    /// </summary>
    public IReadOnlyList<FunctionSymbol> Functions { get; } = Requires.NotNull(functions);

    /// <summary>
    /// Gets the storage symbols currently attributed to this image.
    /// At the current refactor stage this is the reachable global/layout-backed storage
    /// discovered during image planning.
    /// </summary>
    public IReadOnlyList<GlobalVariableSymbol> Storage { get; } = Requires.NotNull(storage);
}

/// <summary>
/// Builds image reachability from the bound task and call graph.
/// </summary>
public static class ImagePlanner
{
    /// <summary>
    /// Discovers all images reachable from the program entry task.
    /// </summary>
    public static ImagePlan Build(BoundProgram program)
    {
        Requires.NotNull(program);

        Dictionary<FunctionSymbol, BoundFunctionMember> functionsBySymbol = [];
        foreach (BoundFunctionMember function in program.Functions)
            functionsBySymbol[function.Symbol] = function;

        Dictionary<TaskSymbol, ImageDescriptor> imagesByTask = [];
        Queue<TaskSymbol> pendingTasks = new();
        pendingTasks.Enqueue(program.EntryPoint);

        while (pendingTasks.Count > 0)
        {
            TaskSymbol task = pendingTasks.Dequeue();
            if (imagesByTask.ContainsKey(task))
                continue;

            HashSet<FunctionSymbol> reachableFunctions = [];
            HashSet<GlobalVariableSymbol> reachableStorage = [];
            HashSet<TaskSymbol> spawnedTasks = [];
            CollectReachableFromFunction(
                task.EntryFunction,
                functionsBySymbol,
                reachableFunctions,
                reachableStorage,
                spawnedTasks);

            ImageDescriptor image = new(
                task,
                task.EntryFunction,
                task.StorageClass,
                ReferenceEquals(task, program.EntryPoint),
                reachableFunctions.OrderBy(static function => function.Name, System.StringComparer.Ordinal).ToList(),
                reachableStorage.OrderBy(static storage => storage.Name, System.StringComparer.Ordinal).ToList());
            imagesByTask.Add(task, image);

            foreach (TaskSymbol spawnedTask in spawnedTasks)
                pendingTasks.Enqueue(spawnedTask);
        }

        ImageDescriptor entryImage = imagesByTask[program.EntryPoint];
        return new ImagePlan(imagesByTask.Values.ToList(), entryImage);
    }

    private static void CollectReachableFromFunction(
        FunctionSymbol function,
        IReadOnlyDictionary<FunctionSymbol, BoundFunctionMember> functionsBySymbol,
        ISet<FunctionSymbol> reachableFunctions,
        ISet<GlobalVariableSymbol> reachableStorage,
        ISet<TaskSymbol> spawnedTasks)
    {
        if (!reachableFunctions.Add(function))
            return;

        if (!functionsBySymbol.TryGetValue(function, out BoundFunctionMember? member))
            return;

        CollectReachableFromStatement(member.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
    }

    private static void CollectReachableFromStatement(
        BoundStatement statement,
        IReadOnlyDictionary<FunctionSymbol, BoundFunctionMember> functionsBySymbol,
        ISet<FunctionSymbol> reachableFunctions,
        ISet<GlobalVariableSymbol> reachableStorage,
        ISet<TaskSymbol> spawnedTasks)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement nested in block.Statements)
                    CollectReachableFromStatement(nested, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundVariableDeclarationStatement variableDeclaration:
                if (variableDeclaration.Initializer is not null)
                    CollectReachableFromExpression(variableDeclaration.Initializer, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundAssignmentStatement assignment:
                CollectReachableFromAssignmentTarget(assignment.Target, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(assignment.Value, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundMultiAssignmentStatement multiAssignment:
                foreach (BoundAssignmentTarget target in multiAssignment.Targets)
                    CollectReachableFromAssignmentTarget(target, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(multiAssignment.Producer, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundExpressionStatement expressionStatement:
                CollectReachableFromExpression(expressionStatement.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundIfStatement ifStatement:
                CollectReachableFromExpression(ifStatement.Condition, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromStatement(ifStatement.ThenBody, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                if (ifStatement.ElseBody is not null)
                    CollectReachableFromStatement(ifStatement.ElseBody, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundWhileStatement whileStatement:
                CollectReachableFromExpression(whileStatement.Condition, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromStatement(whileStatement.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundForStatement forStatement:
                CollectReachableFromExpression(forStatement.Iterable, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromStatement(forStatement.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundLoopStatement loopStatement:
                CollectReachableFromStatement(loopStatement.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundRepLoopStatement repLoop:
                CollectReachableFromStatement(repLoop.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundRepForStatement repFor:
                CollectReachableFromExpression(repFor.Start, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(repFor.End, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromStatement(repFor.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundNoirqStatement noirq:
                CollectReachableFromStatement(noirq.Body, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundReturnStatement ret:
                foreach (BoundExpression value in ret.Values)
                    CollectReachableFromExpression(value, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundYieldtoStatement yieldto:
                CollectReachableFromFunction(yieldto.Target, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                foreach (BoundExpression argument in yieldto.Arguments)
                    CollectReachableFromExpression(argument, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
        }
    }

    private static void CollectReachableFromExpression(
        BoundExpression expression,
        IReadOnlyDictionary<FunctionSymbol, BoundFunctionMember> functionsBySymbol,
        ISet<FunctionSymbol> reachableFunctions,
        ISet<GlobalVariableSymbol> reachableStorage,
        ISet<TaskSymbol> spawnedTasks)
    {
        switch (expression)
        {
            case BoundSymbolExpression { Symbol: GlobalVariableSymbol global }:
                reachableStorage.Add(global);
                break;
            case BoundCallExpression call:
                CollectReachableFromFunction(call.Function, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                foreach (BoundExpression argument in call.Arguments)
                    CollectReachableFromExpression(argument, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundSpawnExpression spawn:
                spawnedTasks.Add(spawn.Task);
                foreach (BoundExpression argument in spawn.Arguments)
                    CollectReachableFromExpression(argument, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundUnaryExpression unary:
                CollectReachableFromExpression(unary.Operand, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundBinaryExpression binary:
                CollectReachableFromExpression(binary.Left, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(binary.Right, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundIntrinsicCallExpression intrinsic:
                foreach (BoundExpression argument in intrinsic.Arguments)
                    CollectReachableFromExpression(argument, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundMemberAccessExpression member:
                CollectReachableFromExpression(member.Receiver, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundIndexExpression index:
                CollectReachableFromExpression(index.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(index.Index, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundPointerDerefExpression deref:
                CollectReachableFromExpression(deref.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundIfExpression ifExpression:
                CollectReachableFromExpression(ifExpression.Condition, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(ifExpression.ThenExpression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(ifExpression.ElseExpression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundRangeExpression range:
                CollectReachableFromExpression(range.Start, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(range.End, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundStructLiteralExpression structLiteral:
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                    CollectReachableFromExpression(field.Value, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundArrayLiteralExpression arrayLiteral:
                foreach (BoundExpression element in arrayLiteral.Elements)
                    CollectReachableFromExpression(element, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundConversionExpression conversion:
                CollectReachableFromExpression(conversion.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundCastExpression cast:
                CollectReachableFromExpression(cast.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundBitcastExpression bitcast:
                CollectReachableFromExpression(bitcast.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
        }
    }

    private static void CollectReachableFromAssignmentTarget(
        BoundAssignmentTarget target,
        IReadOnlyDictionary<FunctionSymbol, BoundFunctionMember> functionsBySymbol,
        ISet<FunctionSymbol> reachableFunctions,
        ISet<GlobalVariableSymbol> reachableStorage,
        ISet<TaskSymbol> spawnedTasks)
    {
        switch (target)
        {
            case BoundSymbolAssignmentTarget { Symbol: GlobalVariableSymbol global }:
                reachableStorage.Add(global);
                break;
            case BoundMemberAssignmentTarget member:
                CollectReachableFromExpression(member.Receiver, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundBitfieldAssignmentTarget bitfield:
                CollectReachableFromAssignmentTarget(bitfield.ReceiverTarget, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(bitfield.ReceiverValue, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundIndexAssignmentTarget index:
                CollectReachableFromExpression(index.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                CollectReachableFromExpression(index.Index, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
            case BoundPointerDerefAssignmentTarget deref:
                CollectReachableFromExpression(deref.Expression, functionsBySymbol, reachableFunctions, reachableStorage, spawnedTasks);
                break;
        }
    }
}
