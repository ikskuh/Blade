using System.Collections.Generic;
using System.Linq;
using Blade.IR.Asm;
using Blade.Diagnostics;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR;

public static class IrPipeline
{
    /// <summary>
    /// Builds the backend stages for one bound program, storing each completed stage in the supplied output container.
    /// </summary>
    public static void Build(BoundProgram boundProgram, CompilationStageOutput output, IrPipelineOptions? options = null, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(boundProgram);
        Requires.NotNull(output);

        options ??= new IrPipelineOptions();

        ImagePlan imagePlan = ImagePlanner.Build(boundProgram);
        output.ImagePlan = imagePlan;
        ImagePlacement imagePlacement = ImagePlacer.Place(imagePlan);
        output.ImagePlacement = imagePlacement;
        LayoutSolution layoutSolution = LayoutSolver.SolveStableLayouts(boundProgram, imagePlacement, diagnostics);
        output.LayoutSolution = layoutSolution;
        ReportMissingRuntimeInitMemory(boundProgram, imagePlan, layoutSolution, diagnostics);
        List<MirModule> mirModules = MirLowerer.Lower(boundProgram, imagePlan, layoutSolution).ToList();

        bool enableSingleCallsiteInlining = options.EnableSingleCallsiteInlining
            && options.EnabledMirOptimizations.Contains(OptimizationRegistry.SingleCallsiteInlineMirOptimization);

        for (int i = 0; i < mirModules.Count; i++)
        {
            mirModules[i] = MirInliner.InlineMandatoryAndSingleCallsite(
                mirModules[i],
                enableSingleCallsiteInlining);
        }
        IReadOnlyList<MirModule> preOptimizationMirModules = mirModules.ToList();
        output.PreOptimizationMirModules = preOptimizationMirModules;

        if (options.EnableMirOptimizations)
        {
            for (int i = 0; i < mirModules.Count; i++)
            {
                mirModules[i] = MirOptimizer.Optimize(
                    mirModules[i],
                    options.MaxOptimizationIterations,
                    options.EnabledMirOptimizations);
            }
        }
        output.MirModules = mirModules.ToList();

        List<LirModule> lirModules = mirModules.ConvertAll(LirLowerer.Lower);
        IReadOnlyList<LirModule> preOptimizationLirModules = lirModules.ToList();
        output.PreOptimizationLirModules = preOptimizationLirModules;
        if (options.EnableLirOptimizations)
        {
            for (int i = 0; i < lirModules.Count; i++)
            {
                lirModules[i] = LirOptimizer.Optimize(
                    lirModules[i],
                    options.MaxOptimizationIterations,
                    options.EnabledLirOptimizations);
            }
        }
        output.LirModules = lirModules.ToList();

        List<AsmModule> asmModules = lirModules.ConvertAll(module => AsmLowerer.Lower(module, imagePlan, diagnostics));
        IReadOnlyList<AsmModule> preOptimizationAsmModules = asmModules.ToList();
        output.PreOptimizationAsmModules = preOptimizationAsmModules;
        output.AsmModules = asmModules.ToList();

        CogResourceLayout placeholderEntryLayout = new(imagePlacement.EntryImage, 0, [], []);
        CogResourceLayoutSet placeholderCogResourceLayouts = new(
            [placeholderEntryLayout],
            placeholderEntryLayout,
            new Dictionary<IAsmSymbol, MemoryAddress>(),
            new Dictionary<ImageDescriptor, CogResourceLayout>(),
            new Dictionary<StoragePlace, CogResourceLayout>(),
            0);
        output.CogResourceLayouts = placeholderCogResourceLayouts;

        CodegenPipeline.Emit(output, new EmitOptions
        {
            EnabledAsmirOptimizations = options.EnabledAsmirOptimizations,
        }, diagnostics);
    }

    private static void ReportMissingRuntimeInitMemory(
        BoundProgram boundProgram,
        ImagePlan imagePlan,
        LayoutSolution layoutSolution,
        DiagnosticBag? diagnostics)
    {
        if (diagnostics is null
            || boundProgram.RuntimeInitMemoryFunction is null
            || LauncherCallsRuntimeInitMemory(
                boundProgram.LauncherEntryPointFunction.Body,
                boundProgram.RuntimeInitMemoryFunction))
        {
            return;
        }

        IReadOnlyList<GlobalVariableSymbol> globalsRequiringGeneratedInitialization =
            MirLowerer.CollectEntryImageGlobalsRequiringGeneratedInitialization(boundProgram, imagePlan, layoutSolution);
        if (globalsRequiringGeneratedInitialization.Count == 0)
            return;

        diagnostics.Report(new RuntimeLauncherMissingInitMemoryWarning(
            boundProgram.LauncherEntryPointFunction.Symbol.SourceSpan.Source,
            boundProgram.LauncherEntryPointFunction.Body.Span));
    }

    private static bool LauncherCallsRuntimeInitMemory(BoundStatement statement, FunctionSymbol runtimeInitMemoryFunction)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                return block.Statements.Any(nested => LauncherCallsRuntimeInitMemory(nested, runtimeInitMemoryFunction));

            case BoundVariableDeclarationStatement variableDeclaration:
                return variableDeclaration.Initializer is not null
                    && LauncherCallsRuntimeInitMemory(variableDeclaration.Initializer, runtimeInitMemoryFunction);

            case BoundAssignmentStatement assignment:
                return LauncherCallsRuntimeInitMemory(assignment.Value, runtimeInitMemoryFunction);

            case BoundMultiAssignmentStatement multiAssignment:
                return LauncherCallsRuntimeInitMemory(multiAssignment.Producer, runtimeInitMemoryFunction);

            case BoundExpressionStatement expressionStatement:
                return LauncherCallsRuntimeInitMemory(expressionStatement.Expression, runtimeInitMemoryFunction);

            case BoundIfStatement ifStatement:
                return LauncherCallsRuntimeInitMemory(ifStatement.Condition, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(ifStatement.ThenBody, runtimeInitMemoryFunction)
                    || (ifStatement.ElseBody is not null
                        && LauncherCallsRuntimeInitMemory(ifStatement.ElseBody, runtimeInitMemoryFunction));

            case BoundWhileStatement whileStatement:
                return LauncherCallsRuntimeInitMemory(whileStatement.Condition, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(whileStatement.Body, runtimeInitMemoryFunction);

            case BoundForStatement forStatement:
                return LauncherCallsRuntimeInitMemory(forStatement.Iterable, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(forStatement.Body, runtimeInitMemoryFunction);

            case BoundLoopStatement loopStatement:
                return LauncherCallsRuntimeInitMemory(loopStatement.Body, runtimeInitMemoryFunction);

            case BoundRepLoopStatement repLoopStatement:
                return LauncherCallsRuntimeInitMemory(repLoopStatement.Body, runtimeInitMemoryFunction);

            case BoundRepForStatement repForStatement:
                return LauncherCallsRuntimeInitMemory(repForStatement.Start, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(repForStatement.End, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(repForStatement.Body, runtimeInitMemoryFunction);

            case BoundNoirqStatement noirqStatement:
                return LauncherCallsRuntimeInitMemory(noirqStatement.Body, runtimeInitMemoryFunction);

            case BoundReturnStatement returnStatement:
                return returnStatement.Values.Any(value => LauncherCallsRuntimeInitMemory(value, runtimeInitMemoryFunction));

            case BoundYieldtoStatement yieldtoStatement:
                return yieldtoStatement.Arguments.Any(argument => LauncherCallsRuntimeInitMemory(argument, runtimeInitMemoryFunction));

            case BoundAsmStatement:
            case BoundBreakStatement:
            case BoundContinueStatement:
            case BoundYieldStatement:
            case BoundErrorStatement:
                return false;

            default:
                return Assert.UnreachableValue<bool>($"Unexpected bound statement '{statement.GetType().Name}'."); // pragma: force-coverage
        }
    }

    private static bool LauncherCallsRuntimeInitMemory(BoundExpression expression, FunctionSymbol runtimeInitMemoryFunction)
    {
        switch (expression)
        {
            case BoundLiteralExpression:
            case BoundSymbolExpression:
            case BoundEnumLiteralExpression:
            case BoundErrorExpression:
                return false;

            case BoundUnaryExpression unary:
                return LauncherCallsRuntimeInitMemory(unary.Operand, runtimeInitMemoryFunction);

            case BoundBinaryExpression binary:
                return LauncherCallsRuntimeInitMemory(binary.Left, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(binary.Right, runtimeInitMemoryFunction);

            case BoundCallExpression call:
                return ReferenceEquals(call.Function, runtimeInitMemoryFunction)
                    || call.Arguments.Any(argument => LauncherCallsRuntimeInitMemory(argument, runtimeInitMemoryFunction));

            case BoundSpawnExpression spawn:
                return spawn.Arguments.Any(argument => LauncherCallsRuntimeInitMemory(argument, runtimeInitMemoryFunction));

            case BoundIntrinsicCallExpression intrinsic:
                return intrinsic.Arguments.Any(argument => LauncherCallsRuntimeInitMemory(argument, runtimeInitMemoryFunction));

            case BoundArrayLiteralExpression arrayLiteral:
                return arrayLiteral.Elements.Any(element => LauncherCallsRuntimeInitMemory(element, runtimeInitMemoryFunction));

            case BoundMemberAccessExpression memberAccess:
                return LauncherCallsRuntimeInitMemory(memberAccess.Receiver, runtimeInitMemoryFunction);

            case BoundIndexExpression index:
                return LauncherCallsRuntimeInitMemory(index.Expression, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(index.Index, runtimeInitMemoryFunction);

            case BoundPointerDerefExpression pointerDeref:
                return LauncherCallsRuntimeInitMemory(pointerDeref.Expression, runtimeInitMemoryFunction);

            case BoundIfExpression ifExpression:
                return LauncherCallsRuntimeInitMemory(ifExpression.Condition, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(ifExpression.ThenExpression, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(ifExpression.ElseExpression, runtimeInitMemoryFunction);

            case BoundRangeExpression range:
                return LauncherCallsRuntimeInitMemory(range.Start, runtimeInitMemoryFunction)
                    || LauncherCallsRuntimeInitMemory(range.End, runtimeInitMemoryFunction);

            case BoundStructLiteralExpression structLiteral:
                return structLiteral.Fields.Any(field => LauncherCallsRuntimeInitMemory(field.Value, runtimeInitMemoryFunction));

            case BoundConversionExpression conversion:
                return LauncherCallsRuntimeInitMemory(conversion.Expression, runtimeInitMemoryFunction);

            case BoundCastExpression cast:
                return LauncherCallsRuntimeInitMemory(cast.Expression, runtimeInitMemoryFunction);

            case BoundBitcastExpression bitcast:
                return LauncherCallsRuntimeInitMemory(bitcast.Expression, runtimeInitMemoryFunction);

            default:
                return Assert.UnreachableValue<bool>($"Unexpected bound expression '{expression.GetType().Name}'."); // pragma: force-coverage
        }
    }
}
