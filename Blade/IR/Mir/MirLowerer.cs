using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blade;
using Blade.IR;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;

namespace Blade.IR.Mir;

public static class MirLowerer
{
    public static MirModule Lower(BoundProgram program)
    {
        Requires.NotNull(program);

        (List<StoragePlace> storagePlaces, List<StorageDefinition> storageDefinitions) = CollectStoragePlaces(program);
        BackendSymbolNaming.AssignStorageNames(storagePlaces);

        List<MirFunction> functions = new();
        functions.Add(LowerEntryPoint(program, storagePlaces, storageDefinitions));

        foreach (BoundFunctionMember functionMember in program.Functions)
        {
            if (!ReferenceEquals(functionMember, program.EntryPoint))
                functions.Add(LowerFunction(functionMember, storagePlaces, storageDefinitions));
        }

        return new MirModule(storagePlaces, storageDefinitions, functions);
    }

    private static MirFunction LowerEntryPoint(
        BoundProgram program,
        IReadOnlyList<StoragePlace> storagePlaces,
        IReadOnlyList<StorageDefinition> storageDefinitions)
    {
        FunctionLoweringContext context = new(
            program.EntryPoint.Symbol,
            isEntryPoint: true,
            program.EntryPoint.Symbol.ReturnTypes,
            storagePlaces,
            storageDefinitions,
            program.EntryPoint.Symbol.ReturnSlots);
        context.LowerEntryPointBody(program.GlobalVariables, program.EntryPoint.Body);
        return context.Build();
    }

    private static MirFunction LowerFunction(
        BoundFunctionMember functionMember,
        IReadOnlyList<StoragePlace> storagePlaces,
        IReadOnlyList<StorageDefinition> storageDefinitions)
    {
        FunctionSymbol symbol = functionMember.Symbol;
        FunctionLoweringContext context = new(
            symbol,
            isEntryPoint: false,
            symbol.ReturnTypes,
            storagePlaces,
            storageDefinitions,
            symbol.ReturnSlots);
        context.LowerFunctionBody(functionMember.Body, symbol.Parameters);
        return context.Build();
    }

    private static (List<StoragePlace> Places, List<StorageDefinition> Definitions) CollectStoragePlaces(BoundProgram program)
    {
        List<StoragePlace> places = new(program.GlobalVariables.Count);
        List<StorageDefinition> definitions = [];
        HashSet<Symbol> seenSymbols = [];
        CollectStoragePlaces(program.GlobalVariables, places, definitions, seenSymbols);

        foreach (Symbol symbol in CollectAddressTakenSymbols(program))
        {
            if (!seenSymbols.Add(symbol))
                continue;

            if (symbol is GlobalVariableSymbol variable
                && variable.IsConst
                && variable.Initializer is BoundLiteralExpression { Value: RuntimeBladeValue constantValue })
            {
                StoragePlace literalPlace = new(variable, MapStoragePlaceKind(variable), fixedAddress: null);
                places.Add(literalPlace);
                definitions.Add(new StorageDefinition(literalPlace, constantValue));
                continue;
            }

            places.Add(new StoragePlace(symbol, StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null));
        }

        return (places, definitions);
    }

    private static void CollectStoragePlaces(
        IReadOnlyList<GlobalVariableSymbol> globals,
        ICollection<StoragePlace> places,
        ICollection<StorageDefinition> definitions,
        ISet<Symbol> seenSymbols)
    {
        foreach (GlobalVariableSymbol global in globals)
        {
            GlobalVariableSymbol symbol = global;
            if (symbol.StorageClass == VariableStorageClass.Automatic || !seenSymbols.Add(symbol))
                continue;

            StoragePlaceKind kind = MapStoragePlaceKind(symbol);

            RuntimeBladeValue? staticInitializer = null;
            if (kind is StoragePlaceKind.AllocatableGlobalRegister
                    or StoragePlaceKind.AllocatableLutEntry
                    or StoragePlaceKind.AllocatableHubEntry
                && global.Initializer is not null
                && TryEvaluateStaticValue(global.Initializer, symbol.Type, out RuntimeBladeValue? value))
            {
                staticInitializer = value;
            }

            StoragePlace place = new(symbol, kind, symbol.FixedAddress);
            places.Add(place);
            if (staticInitializer is not null)
                definitions.Add(new StorageDefinition(place, staticInitializer));
        }
    }

    private static StoragePlaceKind MapStoragePlaceKind(GlobalVariableSymbol symbol)
    {
        return symbol.StorageClass switch
        {
            VariableStorageClass.Lut when symbol.FixedAddress.HasValue => StoragePlaceKind.FixedLutAlias,
            VariableStorageClass.Lut when symbol.IsExtern => StoragePlaceKind.ExternalLutAlias,
            VariableStorageClass.Lut => StoragePlaceKind.AllocatableLutEntry,
            VariableStorageClass.Hub when symbol.FixedAddress.HasValue => StoragePlaceKind.FixedHubAlias,
            VariableStorageClass.Hub when symbol.IsExtern => StoragePlaceKind.ExternalHubAlias,
            VariableStorageClass.Hub => StoragePlaceKind.AllocatableHubEntry,
            _ when symbol.FixedAddress.HasValue => StoragePlaceKind.FixedRegisterAlias,
            _ when symbol.IsExtern => StoragePlaceKind.ExternalAlias,
            _ => StoragePlaceKind.AllocatableGlobalRegister,
        };
    }

    private static VariableStorageClass GetStorageClass(BladeType type)
    {
        return type switch
        {
            PointerLikeTypeSymbol pointer => pointer.StorageClass,
            _ => VariableStorageClass.Reg,
        };
    }

    private static VariableStorageClass GetStorageClass(BoundExpression expression)
    {
        // For pointer/multi-pointer types, the storage class is on the type itself.
        if (expression.Type is PointerLikeTypeSymbol pointer)
            return pointer.StorageClass;

        // For array variables (e.g. hub var data: [4]u32), the storage class
        // lives on the variable symbol, not on the array type.
        if (expression is BoundSymbolExpression { Symbol: GlobalVariableSymbol variable })
            return variable.StorageClass;

        return VariableStorageClass.Reg;
    }

    private static IReadOnlyList<Symbol> CollectAddressTakenSymbols(BoundProgram program)
    {
        Dictionary<Symbol, Symbol> symbols = [];

        foreach (BoundFunctionMember function in program.Functions)
            CollectAddressTakenSymbols(function.Body, symbols);

        return [.. symbols.Values];
    }

    private static void CollectAddressTakenSymbols(BoundStatement statement, IDictionary<Symbol, Symbol> symbols)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement nested in block.Statements)
                    CollectAddressTakenSymbols(nested, symbols);
                break;
            case BoundVariableDeclarationStatement variableDeclaration:
                if (variableDeclaration.Initializer is not null)
                    CollectAddressTakenSymbols(variableDeclaration.Initializer, symbols);
                break;
            case BoundAssignmentStatement assignment:
                CollectAddressTakenSymbols(assignment.Value, symbols);
                break;
            case BoundMultiAssignmentStatement multiAssignment:
                foreach (BoundExpression argument in multiAssignment.Call.Arguments)
                    CollectAddressTakenSymbols(argument, symbols);
                break;
            case BoundExpressionStatement expressionStatement:
                CollectAddressTakenSymbols(expressionStatement.Expression, symbols);
                break;
            case BoundIfStatement ifStatement:
                CollectAddressTakenSymbols(ifStatement.Condition, symbols);
                CollectAddressTakenSymbols(ifStatement.ThenBody, symbols);
                if (ifStatement.ElseBody is not null)
                    CollectAddressTakenSymbols(ifStatement.ElseBody, symbols);
                break;
            case BoundWhileStatement whileStatement:
                CollectAddressTakenSymbols(whileStatement.Condition, symbols);
                CollectAddressTakenSymbols(whileStatement.Body, symbols);
                break;
            case BoundForStatement forStatement:
                CollectAddressTakenSymbols(forStatement.Iterable, symbols);
                CollectAddressTakenSymbols(forStatement.Body, symbols);
                break;
            case BoundLoopStatement loopStatement:
                CollectAddressTakenSymbols(loopStatement.Body, symbols);
                break;
            case BoundRepLoopStatement repLoop:
                CollectAddressTakenSymbols(repLoop.Body, symbols);
                break;
            case BoundRepForStatement repFor:
                CollectAddressTakenSymbols(repFor.Start, symbols);
                CollectAddressTakenSymbols(repFor.End, symbols);
                CollectAddressTakenSymbols(repFor.Body, symbols);
                break;
            case BoundNoirqStatement noirq:
                CollectAddressTakenSymbols(noirq.Body, symbols);
                break;
            case BoundReturnStatement ret:
                foreach (BoundExpression value in ret.Values)
                    CollectAddressTakenSymbols(value, symbols);
                break;
            case BoundYieldtoStatement yieldto:
                foreach (BoundExpression argument in yieldto.Arguments)
                    CollectAddressTakenSymbols(argument, symbols);
                break;
        }
    }

    private static void CollectAddressTakenSymbols(BoundExpression expression, IDictionary<Symbol, Symbol> symbols)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal
                when literal.Value.TryGetPointedValue(out PointedValue pointedValue)
                && pointedValue.Symbol is not AbsoluteAddressSymbol:
                symbols[pointedValue.Symbol] = pointedValue.Symbol;
                break;
            case BoundUnaryExpression unary when unary.Operator.Kind == BoundUnaryOperatorKind.AddressOf:
                CollectAddressTakenTarget(unary.Operand, symbols);
                break;
            case BoundUnaryExpression unary:
                CollectAddressTakenSymbols(unary.Operand, symbols);
                break;
            case BoundBinaryExpression binary:
                CollectAddressTakenSymbols(binary.Left, symbols);
                CollectAddressTakenSymbols(binary.Right, symbols);
                break;
            case BoundCallExpression call:
                foreach (BoundExpression argument in call.Arguments)
                    CollectAddressTakenSymbols(argument, symbols);
                break;
            case BoundModuleCallExpression moduleCall:
                CollectAddressTakenSymbols(moduleCall.Module.Constructor.Body, symbols);
                break;
            case BoundIntrinsicCallExpression intrinsic:
                foreach (BoundExpression argument in intrinsic.Arguments)
                    CollectAddressTakenSymbols(argument, symbols);
                break;
            case BoundMemberAccessExpression member:
                CollectAddressTakenSymbols(member.Receiver, symbols);
                break;
            case BoundIndexExpression index:
                CollectAddressTakenSymbols(index.Expression, symbols);
                CollectAddressTakenSymbols(index.Index, symbols);
                break;
            case BoundPointerDerefExpression deref:
                CollectAddressTakenSymbols(deref.Expression, symbols);
                break;
            case BoundIfExpression ifExpression:
                CollectAddressTakenSymbols(ifExpression.Condition, symbols);
                CollectAddressTakenSymbols(ifExpression.ThenExpression, symbols);
                CollectAddressTakenSymbols(ifExpression.ElseExpression, symbols);
                break;
            case BoundRangeExpression range:
                CollectAddressTakenSymbols(range.Start, symbols);
                CollectAddressTakenSymbols(range.End, symbols);
                break;
            case BoundStructLiteralExpression structLiteral:
                foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                    CollectAddressTakenSymbols(field.Value, symbols);
                break;
            case BoundArrayLiteralExpression arrayLiteral:
                foreach (BoundExpression element in arrayLiteral.Elements)
                    CollectAddressTakenSymbols(element, symbols);
                break;
            case BoundConversionExpression conversion:
                CollectAddressTakenSymbols(conversion.Expression, symbols);
                break;
            case BoundCastExpression cast:
                CollectAddressTakenSymbols(cast.Expression, symbols);
                break;
            case BoundBitcastExpression bitcast:
                CollectAddressTakenSymbols(bitcast.Expression, symbols);
                break;
        }
    }

    private static void CollectAddressTakenTarget(BoundExpression expression, IDictionary<Symbol, Symbol> symbols)
    {
        switch (expression)
        {
            case BoundSymbolExpression symbolExpression:
                symbols[symbolExpression.Symbol] = symbolExpression.Symbol;
                return;

            case BoundIndexExpression indexExpression when indexExpression.Expression.Type is ArrayTypeSymbol:
                CollectAddressTakenTarget(indexExpression.Expression, symbols);
                CollectAddressTakenSymbols(indexExpression.Index, symbols);
                return;

            default:
                CollectAddressTakenSymbols(expression, symbols);
                return;
        }
    }

        private sealed class FunctionLoweringContext
        {
            private readonly FunctionSymbol _symbol;
            private readonly bool _isEntryPoint;
        private readonly IReadOnlyList<BladeType> _returnTypes;
        private readonly IReadOnlyList<ReturnSlot> _returnSlots;
        private readonly Dictionary<MirValueId, MirFlag> _flagValues = [];
        private readonly List<MirValueId> _pendingFlagReturns = [];
        private readonly List<BlockBuilder> _blocks = [];
        private readonly Stack<LoopContext> _loopStack = [];
        private readonly Dictionary<Symbol, StoragePlace> _storagePlacesBySymbol = [];
        private readonly HashSet<StoragePlace> _preInitializedStoragePlaces = [];
        private readonly BlockBuilder _entryBlock;
        private readonly BlockBuilder _exitBlock;
        private readonly Dictionary<Symbol, MirValueId> _currentValues = [];
        private BlockBuilder _currentBlock;

        public FunctionLoweringContext(
            FunctionSymbol symbol,
            bool isEntryPoint,
            IReadOnlyList<BladeType> returnTypes,
            IReadOnlyList<StoragePlace> storagePlaces,
            IReadOnlyList<StorageDefinition> storageDefinitions,
            IReadOnlyList<ReturnSlot>? returnSlots = null)
        {
            _symbol = Requires.NotNull(symbol);
            _isEntryPoint = isEntryPoint;
            _returnTypes = returnTypes;
            _returnSlots = returnSlots ?? [];
            foreach (StoragePlace place in storagePlaces)
                _storagePlacesBySymbol[place.Symbol] = place;
            foreach (StorageDefinition definition in storageDefinitions)
                _preInitializedStoragePlaces.Add(definition.Place);

            _entryBlock = CreateBlock();
            _exitBlock = CreateBlock();
            _currentBlock = _entryBlock;

            for (int i = 0; i < _returnTypes.Count; i++)
            {
                BladeType returnType = _returnTypes[i];
                MirValueId value = NextValue();
                _exitBlock.Parameters.Add(new MirBlockParameter(value, $"ret{i}", returnType));
            }
        }

        public void LowerEntryPointBody(
            IReadOnlyList<GlobalVariableSymbol> globalVariables,
            BoundBlockStatement body)
        {
            foreach (GlobalVariableSymbol global in globalVariables)
            {
                if (global.Initializer is null || !TryGetStoragePlace(global, out StoragePlace place))
                    continue;

                if (_preInitializedStoragePlaces.Contains(place)
                    && place.Kind is StoragePlaceKind.AllocatableGlobalRegister or StoragePlaceKind.AllocatableHubEntry)
                {
                    continue;
                }

                if (IsUndefinedInitializer(global.Initializer))
                    continue;

                MirValueId initializerValue = LowerExpression(global.Initializer);
                EmitStorePlace(place, initializerValue, global.SourceSpan.Span);
            }

            LowerStatement(body);
            EmitFallthroughReturn(body.Span);
        }

        public void LowerFunctionBody(BoundBlockStatement body, IReadOnlyList<ParameterVariableSymbol> parameters)
        {
            foreach (ParameterVariableSymbol parameter in parameters)
            {
                MirValueId parameterValue = NextValue();
                _entryBlock.Parameters.Add(new MirBlockParameter(parameterValue, parameter.Name, parameter.Type));
                _currentValues[parameter] = parameterValue;
                if (TryGetStoragePlace(parameter, out StoragePlace place))
                    EmitStorePlace(place, parameterValue, body.Span);
            }

            LowerStatement(body);
            EmitFallthroughReturn(body.Span);
        }

        public MirFunction Build()
        {
            if (_exitBlock.Terminator is null)
            {
                List<MirValueId> values = [];
                foreach (MirBlockParameter parameter in _exitBlock.Parameters)
                    values.Add(parameter.Value);
                _exitBlock.Terminator = new MirReturnTerminator(values, new TextSpan(0, 0));
            }

            List<MirBlock> blocks = new(_blocks.Count);
            foreach (BlockBuilder block in _blocks)
            {
                MirTerminator terminator = block.Terminator ?? new MirUnreachableTerminator(new TextSpan(0, 0));
                blocks.Add(new MirBlock(block.Label, block.Parameters, block.Instructions, terminator));
            }

            return new MirFunction(_symbol, _isEntryPoint, _returnTypes, blocks, _returnSlots, _flagValues);
        }

        private void EmitFallthroughReturn(TextSpan span)
        {
            if (_currentBlock.Terminator is not null)
                return;

            List<MirValueId> arguments = BuildReturnArguments([], span);
            _currentBlock.Terminator = new MirGotoTerminator(_exitBlock.Label, arguments, span);
        }

        private BlockBuilder CreateBlock()
        {
            BlockBuilder block = new(new MirBlockRef());
            _blocks.Add(block);
            return block;
        }

        private MirValueId NextValue() => new();

        private void EnsureWritableBlock()
        {
            if (_currentBlock.Terminator is not null)
                _currentBlock = CreateBlock();
        }

        private void LowerStatement(BoundStatement statement)
        {
            EnsureWritableBlock();
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (BoundStatement nested in block.Statements)
                        LowerStatement(nested);
                    break;

                case BoundVariableDeclarationStatement variableDeclaration:
                    if (variableDeclaration.Initializer is not null
                        && !IsUndefinedInitializer(variableDeclaration.Initializer))
                    {
                        MirValueId initializer = LowerExpression(variableDeclaration.Initializer);
                        WriteSymbol(variableDeclaration.Symbol, initializer, statement.Span);
                    }
                    break;

                case BoundAssignmentStatement assignment:
                    LowerAssignmentStatement(assignment);
                    break;

                case BoundMultiAssignmentStatement multiAssignment:
                    LowerMultiAssignmentStatement(multiAssignment);
                    break;

                case BoundExpressionStatement expressionStatement:
                    _ = LowerExpression(expressionStatement.Expression);
                    break;

                case BoundIfStatement ifStatement:
                    LowerIfStatement(ifStatement);
                    break;

                case BoundWhileStatement whileStatement:
                    LowerWhileStatement(whileStatement);
                    break;

                case BoundForStatement forStatement:
                    LowerForStatement(forStatement);
                    break;

                case BoundLoopStatement loopStatement:
                    LowerLoopStatement(loopStatement);
                    break;

                case BoundRepLoopStatement repLoopStatement:
                    LowerRepLoopStatement(repLoopStatement);
                    break;

                case BoundRepForStatement repForStatement:
                    LowerRepForStatement(repForStatement);
                    break;

                case BoundNoirqStatement noirqStatement:
                    _currentBlock.Instructions.Add(new MirNoIrqBeginInstruction(noirqStatement.Span));
                    LowerStatement(noirqStatement.Body);
                    EnsureWritableBlock();
                    _currentBlock.Instructions.Add(new MirNoIrqEndInstruction(noirqStatement.Span));
                    break;

                case BoundReturnStatement returnStatement:
                    LowerReturnStatement(returnStatement);
                    break;

                case BoundBreakStatement:
                    LowerLoopTransfer(isBreak: true, statement.Span);
                    break;

                case BoundContinueStatement:
                    LowerLoopTransfer(isBreak: false, statement.Span);
                    break;

                case BoundYieldStatement yieldStatement:
                    _currentBlock.Instructions.Add(new MirYieldInstruction(yieldStatement.Span));
                    break;

                case BoundYieldtoStatement yieldtoStatement:
                {
                    List<MirValueId> arguments = [];
                    foreach (BoundExpression argument in yieldtoStatement.Arguments)
                        arguments.Add(LowerExpression(argument));

                    FunctionSymbol target = yieldtoStatement.Target ?? new FunctionSymbol("<error>", FunctionKind.Default);
                    _currentBlock.Instructions.Add(new MirYieldToInstruction(target, arguments, yieldtoStatement.Span));
                    break;
                }

                case BoundAsmStatement asmStatement:
                    LowerInlineAsmStatement(asmStatement);
                    break;

                case BoundErrorStatement:
                    Assert.Unreachable("MIR lowering must not run on bound statements that already contain binder errors."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void LowerInlineAsmStatement(BoundAsmStatement asmStatement)
        {
            InlineAsmBindingSlot[] bindingSlots = [.. asmStatement.ReferencedSymbols.Keys];
            IReadOnlyDictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> bindingAccess =
                InlineAssemblyBindingAnalysis.ComputeBindingAccess(
                    asmStatement.ParsedLines,
                    bindingSlots);

            List<MirInlineAsmBinding> bindings = new(asmStatement.ReferencedSymbols.Count);
            foreach ((InlineAsmBindingSlot slot, Symbol symbol) in asmStatement.ReferencedSymbols)
            {
                InlineAsmBindingAccess access = bindingAccess.GetValueOrDefault(slot, InlineAsmBindingAccess.ReadWrite);
                if (TryGetStoragePlace(symbol, out StoragePlace? place))
                {
                    bindings.Add(new MirInlineAsmBinding(slot, symbol, value: null, place, access));
                }
                else
                {
                    BladeType type = GetSymbolType(symbol);
                    MirValueId value = IsInlineAsmTempSymbol(symbol) && !_currentValues.ContainsKey(symbol)
                        ? NextValue()
                        : ReadSymbol(symbol, type, asmStatement.Span);
                    bindings.Add(new MirInlineAsmBinding(slot, symbol, value, place: null, access));
                }
            }

            // When the inline asm has a flag output (@C or @Z), it produces a flag-typed result.
            // This value represents the flag state after the asm executes and can be used
            // directly by branches or materialized to a register when needed.
            MirValueId? flagResult = null;
            BladeType? flagResultType = null;
            if (asmStatement.FlagOutput is not null)
            {
                flagResult = NextValue();
                flagResultType = BuiltinTypes.Bool;
                MirFlag flag = asmStatement.FlagOutput == InlineAsmFlagOutput.C ? MirFlag.C : MirFlag.Z;
                _flagValues[flagResult] = flag;
            }

            _currentBlock.Instructions.Add(new MirInlineAsmInstruction(
                asmStatement.Volatility,
                asmStatement.FlagOutput,
                asmStatement.ParsedLines,
                bindings,
                asmStatement.Span,
                flagResult,
                flagResultType));

            // Track flag results so that empty return statements in asm functions
            // can pick them up as implicit return values.
            if (flagResult is not null)
                _pendingFlagReturns.Add(flagResult);
        }

        private void LowerLoopTransfer(bool isBreak, TextSpan span)
        {
            if (_loopStack.Count == 0)
            {
                _currentBlock.Terminator = new MirUnreachableTerminator(span);
                return;
            }

            LoopContext loop = _loopStack.Peek();
            MirBlockRef target = isBreak ? loop.BreakLabel : loop.ContinueLabel;
            List<MirValueId> arguments = BuildEnvironmentArguments(loop.Symbols, span);
            _currentBlock.Terminator = new MirGotoTerminator(target, arguments, span);
        }

        private void LowerAssignmentStatement(BoundAssignmentStatement assignment)
        {
            if (assignment.Target is BoundSymbolAssignmentTarget symbolTarget
                && assignment.OperatorKind != TokenKind.Equal
                && TryGetStoragePlace(symbolTarget.Symbol, out StoragePlace place)
                && TryMapCompoundOperator(assignment.OperatorKind, out BoundBinaryOperatorKind updateKind))
            {
                MirValueId rhs = LowerExpression(assignment.Value);
                EmitUpdatePlace(place, updateKind, rhs, assignment.Span);
                return;
            }

            MirValueId value = LowerExpression(assignment.Value);
            if (assignment.OperatorKind != TokenKind.Equal)
            {
                MirValueId current = LowerAssignmentTargetRead(assignment.Target);
                if (TryMapCompoundOperator(assignment.OperatorKind, out BoundBinaryOperatorKind binaryKind))
                {
                    MirValueId computed = NextValue();
                    _currentBlock.Instructions.Add(new MirBinaryInstruction(
                        computed,
                        assignment.Target.Type,
                        binaryKind,
                        current,
                        value,
                        assignment.Span));
                    value = computed;
                }
            }

            LowerAssignmentTargetWrite(assignment.Target, value, assignment.Span);
        }

        private void LowerMultiAssignmentStatement(BoundMultiAssignmentStatement multiAssignment)
        {
            BoundCallExpression callExpression = multiAssignment.Call;

            // Lower call arguments
            List<MirValueId> arguments = [];
            foreach (BoundExpression argument in callExpression.Arguments)
                arguments.Add(LowerExpression(argument));

            IReadOnlyList<BoundAssignmentTarget> targets = multiAssignment.Targets;

            // First result goes into the primary Result slot
            MirValueId? primaryResult = null;
            BladeType? primaryType = null;
            if (targets.Count > 0 && targets[0] is not BoundDiscardAssignmentTarget)
            {
                primaryResult = NextValue();
                primaryType = callExpression.Function.ReturnTypes[0];
            }
            else if (targets.Count > 0)
            {
                // Even for discards, we need a value ID so the call instruction has a result
                primaryResult = NextValue();
                primaryType = callExpression.Function.ReturnTypes[0];
            }

            // Extra results for positions 1+
            List<(MirValueId Value, BladeType Type)> extraResults = [];
            for (int i = 1; i < targets.Count && i < callExpression.Function.ReturnTypes.Count; i++)
            {
                MirValueId extraValue = NextValue();
                BladeType extraType = callExpression.Function.ReturnTypes[i];
                extraResults.Add((extraValue, extraType));
            }

            _currentBlock.Instructions.Add(new MirCallInstruction(
                primaryResult,
                primaryType,
                callExpression.Function,
                arguments,
                callExpression.Span,
                extraResults));

            // Write results to targets (skip discards)
            if (targets.Count > 0 && targets[0] is not BoundDiscardAssignmentTarget && primaryResult is not null)
                LowerAssignmentTargetWrite(targets[0], primaryResult, multiAssignment.Span);

            for (int i = 0; i < extraResults.Count; i++)
            {
                if (targets[i + 1] is not BoundDiscardAssignmentTarget)
                    LowerAssignmentTargetWrite(targets[i + 1], extraResults[i].Value, multiAssignment.Span);
            }
        }

        private void LowerIfStatement(BoundIfStatement ifStatement)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            MirValueId condition = LowerExpression(ifStatement.Condition);
            BlockBuilder thenBlock = CreateBlock();
            BlockBuilder elseBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> mergeEnv = CreateEnvironmentParameters(mergeBlock, envSymbols, "if");

            _flagValues.TryGetValue(condition, out MirFlag conditionFlag);
            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                thenBlock.Label,
                elseBlock.Label,
                [],
                [],
                ifStatement.Span,
                _flagValues.ContainsKey(condition) ? conditionFlag : null);

            _currentBlock = thenBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            LowerStatement(ifStatement.ThenBody);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    mergeBlock.Label,
                    BuildEnvironmentArguments(envSymbols, ifStatement.ThenBody.Span),
                    ifStatement.ThenBody.Span);
            }

            _currentBlock = elseBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            if (ifStatement.ElseBody is not null)
                LowerStatement(ifStatement.ElseBody);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    mergeBlock.Label,
                    BuildEnvironmentArguments(envSymbols, ifStatement.Span),
                    ifStatement.Span);
            }

            _currentBlock = mergeBlock;
            ReplaceAutomaticEnvironment(mergeEnv);
        }

        private void LowerWhileStatement(BoundWhileStatement whileStatement)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder conditionBlock = CreateBlock();
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> conditionEnv = CreateEnvironmentParameters(conditionBlock, envSymbols, "while");
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "while");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "while");

            _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, BuildEnvironmentArguments(envSymbols, whileStatement.Span), whileStatement.Span);

            _currentBlock = conditionBlock;
            ReplaceAutomaticEnvironment(conditionEnv);
            MirValueId condition = LowerExpression(whileStatement.Condition);
            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                bodyBlock.Label,
                exitBlock.Label,
                BuildEnvironmentArguments(envSymbols, whileStatement.Condition.Span),
                BuildEnvironmentArguments(envSymbols, whileStatement.Condition.Span),
                whileStatement.Condition.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, conditionBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            LowerStatement(whileStatement.Body);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    conditionBlock.Label,
                    BuildEnvironmentArguments(envSymbols, whileStatement.Body.Span),
                    whileStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private void LowerForStatement(BoundForStatement forStatement)
        {
            // The binder guarantees IndexVariable is always present for valid for-loops.
            if (forStatement.IndexVariable is null)
            {
                LowerStatement(forStatement.Body);
                return;
            }

            bool isRangeIteration = forStatement.Iterable is BoundRangeExpression;
            bool isArrayIteration = forStatement.Iterable.Type is ArrayTypeSymbol;

            MirValueId initialIndex;
            MirValueId count;
            MirValueId iterableValue;

            if (isRangeIteration)
            {
                // Range iteration: start index at range.Start, loop while index < range.End (exclusive).
                // Inclusive ranges are handled by normalizing end + 1 here.
                BoundRangeExpression rangeExpr = (BoundRangeExpression)forStatement.Iterable;
                initialIndex = LowerExpression(rangeExpr.Start);
                MirValueId endVal = LowerExpression(rangeExpr.End);
                if (rangeExpr.IsInclusive)
                {
                    MirValueId rangeOne = EmitConstant(new RuntimeBladeValue(BuiltinTypes.U32, 1L), forStatement.Span);
                    MirValueId adjustedEnd = NextValue();
                    _currentBlock.Instructions.Add(new MirBinaryInstruction(
                        adjustedEnd, BuiltinTypes.U32, BoundBinaryOperatorKind.Add,
                        endVal, rangeOne, forStatement.Span));
                    endVal = adjustedEnd;
                }
                count = endVal;
                iterableValue = initialIndex; // unused for range, placeholder
            }
            else
            {
                // Lower the iterable expression and determine the loop count.
                iterableValue = LowerExpression(forStatement.Iterable);
                if (isArrayIteration)
                {
                    ArrayTypeSymbol arrayType = (ArrayTypeSymbol)forStatement.Iterable.Type;
                    count = EmitConstant(new RuntimeBladeValue(BuiltinTypes.U32, (long)(arrayType.Length ?? 0)), forStatement.Span);
                }
                else
                {
                    count = iterableValue;
                }
                initialIndex = EmitConstant(new RuntimeBladeValue(BuiltinTypes.U32, 0L), forStatement.Span);
            }

            // Initialize index.
            WriteSymbol(forStatement.IndexVariable, initialIndex, forStatement.Span);

            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder conditionBlock = CreateBlock();
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> conditionEnv = CreateEnvironmentParameters(conditionBlock, envSymbols, "for");
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "for");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "for");

            _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, BuildEnvironmentArguments(envSymbols, forStatement.Span), forStatement.Span);

            // Condition block: compare index < count.
            _currentBlock = conditionBlock;
            ReplaceAutomaticEnvironment(conditionEnv);
            MirValueId currentIndex = ReadSymbol(forStatement.IndexVariable, BuiltinTypes.U32, forStatement.Span);
            MirValueId condition = NextValue();
            _currentBlock.Instructions.Add(new MirBinaryInstruction(
                condition, BuiltinTypes.Bool, BoundBinaryOperatorKind.Less,
                currentIndex, count, forStatement.Span));

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                bodyBlock.Label,
                exitBlock.Label,
                BuildEnvironmentArguments(envSymbols, forStatement.Span),
                BuildEnvironmentArguments(envSymbols, forStatement.Span),
                forStatement.Span);

            // Body block.
            _loopStack.Push(new LoopContext(exitBlock.Label, conditionBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            MirValueId bodyIndex = ReadSymbol(forStatement.IndexVariable, BuiltinTypes.U32, forStatement.Span);

            // For array iteration, load element at current index.
            VariableStorageClass iterStorageClass = GetStorageClass(forStatement.Iterable);
            if (isArrayIteration && forStatement.ItemVariable is not null)
            {
                MirValueId element = EmitLoadIndex(
                    iterableValue,
                    bodyIndex,
                    forStatement.Iterable.Type,
                    ((ArrayTypeSymbol)forStatement.Iterable.Type).ElementType,
                    iterStorageClass,
                    hasSideEffects: false,
                    forStatement.Span);
                WriteSymbol(forStatement.ItemVariable, element, forStatement.Span);
            }

            LowerStatement(forStatement.Body);

            // Increment index and loop back.
            if (_currentBlock.Terminator is null)
            {
                MirValueId postIndex = ReadSymbol(forStatement.IndexVariable, BuiltinTypes.U32, forStatement.Body.Span);

                // Write back mutable item if needed.
                if (isArrayIteration && forStatement.ItemVariable is not null && forStatement.ItemIsMutable)
                {
                    BladeType elemType = ((ArrayTypeSymbol)forStatement.Iterable.Type).ElementType;
                    MirValueId updatedItem = ReadSymbol(forStatement.ItemVariable, elemType, forStatement.Body.Span);
                    _currentBlock.Instructions.Add(new MirStoreIndexInstruction(elemType, forStatement.Iterable.Type, iterableValue, postIndex, updatedItem, iterStorageClass, forStatement.Body.Span));
                }

                MirValueId bodyOne = EmitConstant(new RuntimeBladeValue(BuiltinTypes.U32, 1L), forStatement.Body.Span);
                MirValueId incremented = NextValue();
                _currentBlock.Instructions.Add(new MirBinaryInstruction(
                    incremented, BuiltinTypes.U32, BoundBinaryOperatorKind.Add,
                    postIndex, bodyOne, forStatement.Body.Span));
                WriteSymbol(forStatement.IndexVariable, incremented, forStatement.Body.Span);

                _currentBlock.Terminator = new MirGotoTerminator(
                    conditionBlock.Label,
                    BuildEnvironmentArguments(envSymbols, forStatement.Body.Span),
                    forStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private void LowerLoopStatement(BoundLoopStatement loopStatement)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "loop");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "loop");

            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, BuildEnvironmentArguments(envSymbols, loopStatement.Span), loopStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            LowerStatement(loopStatement.Body);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    bodyBlock.Label,
                    BuildEnvironmentArguments(envSymbols, loopStatement.Body.Span),
                    loopStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private void LowerRepLoopStatement(BoundRepLoopStatement repLoopStatement)
        {
            MirValueId count = EmitConstant(new RuntimeBladeValue(BuiltinTypes.U32, 0L), repLoopStatement.Span);
            _currentBlock.Instructions.Add(new MirRepSetupInstruction(count, repLoopStatement.Span));

            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "rep");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "rep");

            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, BuildEnvironmentArguments(envSymbols, repLoopStatement.Span), repLoopStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            _currentBlock.Instructions.Add(new MirRepIterInstruction(count, repLoopStatement.Span));
            LowerStatement(repLoopStatement.Body);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    bodyBlock.Label,
                    BuildEnvironmentArguments(envSymbols, repLoopStatement.Body.Span),
                    repLoopStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private void LowerRepForStatement(BoundRepForStatement repForStatement)
        {
            MirValueId start = LowerExpression(repForStatement.Start);
            MirValueId end = LowerExpression(repForStatement.End);
            _currentBlock.Instructions.Add(new MirRepForSetupInstruction(start, end, repForStatement.Span));

            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "repfor");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "repfor");

            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, BuildEnvironmentArguments(envSymbols, repForStatement.Span), repForStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            _currentBlock.Instructions.Add(new MirRepForIterInstruction(start, end, repForStatement.Span));
            LowerStatement(repForStatement.Body);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    bodyBlock.Label,
                    BuildEnvironmentArguments(envSymbols, repForStatement.Body.Span),
                    repForStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private void LowerReturnStatement(BoundReturnStatement returnStatement)
        {
            List<MirValueId> values = [];
            foreach (BoundExpression expression in returnStatement.Values)
                values.Add(LowerExpression(expression));

            List<MirValueId> returnArguments = BuildReturnArguments(values, returnStatement.Span);
            _currentBlock.Terminator = new MirGotoTerminator(_exitBlock.Label, returnArguments, returnStatement.Span);
        }

        private List<MirValueId> BuildReturnArguments(IReadOnlyList<MirValueId> values, TextSpan span)
        {
            // Combine explicit return values with any pending flag return values.
            // For asm functions that return via flags (e.g. -> bool@C), the explicit
            // return values list is empty, but _pendingFlagReturns holds the flag results.
            List<MirValueId> allValues = new(values);
            if (allValues.Count < _exitBlock.Parameters.Count && _pendingFlagReturns.Count > 0)
            {
                foreach (MirValueId flagReturn in _pendingFlagReturns)
                {
                    if (allValues.Count >= _exitBlock.Parameters.Count)
                        break;
                    allValues.Add(flagReturn);
                }
            }

            List<MirValueId> arguments = [];
            for (int i = 0; i < _exitBlock.Parameters.Count; i++)
            {
                if (i < allValues.Count)
                {
                    arguments.Add(allValues[i]);
                }
                else
                {
                    BladeType expectedType = _exitBlock.Parameters[i].Type;
                    arguments.Add(EmitDefaultValue(expectedType, span));
                }
            }

            return arguments;
        }

        private MirValueId EmitDefaultValue(BladeType type, TextSpan span)
        {
            if (type is not BoolTypeSymbol
                && type is not IntegerTypeSymbol
                && type is not EnumTypeSymbol
                && type is not BitfieldTypeSymbol)
            {
                return EmitPlaceholderConstant(type, span);
            }

            BladeValue rawValue = type is BoolTypeSymbol ? BladeValue.Bool(false) : BladeValue.IntegerLiteral(0L);
            Assert.Invariant(BladeValue.TryConvert(rawValue, type, out BladeValue defaultValue) == EvaluationError.None, $"Failed to materialize default value for '{type.Name}'.");
            return EmitConstant(defaultValue, span);
        }

        private MirValueId LowerExpression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    return EmitConstant(literal.Value, literal.Span);

                case BoundSymbolExpression symbolExpression:
                    return ReadSymbol(symbolExpression.Symbol, symbolExpression.Type, symbolExpression.Span);

                case BoundUnaryExpression unaryExpression:
                    return LowerUnaryExpression(unaryExpression);

                case BoundBinaryExpression binaryExpression:
                    return LowerBinaryExpression(binaryExpression);

                case BoundCallExpression callExpression:
                    return LowerCallExpression(callExpression);

                case BoundModuleCallExpression moduleCallExpression:
                    return LowerCallExpression(new BoundCallExpression(
                        moduleCallExpression.Module.Constructor.Symbol,
                        [],
                        moduleCallExpression.Span,
                        moduleCallExpression.Type));

                case BoundIntrinsicCallExpression intrinsicCall:
                {
                    List<MirValueId> arguments = [];
                    foreach (BoundExpression argument in intrinsicCall.Arguments)
                        arguments.Add(LowerExpression(argument));

                    MirValueId? result = intrinsicCall.Type is VoidTypeSymbol ? null : NextValue();
                    _currentBlock.Instructions.Add(new MirIntrinsicCallInstruction(
                        result,
                        intrinsicCall.Type is VoidTypeSymbol ? null : intrinsicCall.Type,
                        intrinsicCall.Mnemonic,
                        arguments,
                        intrinsicCall.Span));
                    return result ?? EmitPlaceholderConstant(BuiltinTypes.Unknown, intrinsicCall.Span);
                }

                case BoundEnumLiteralExpression enumLiteral:
                    Assert.Invariant(
                        BladeValue.TryConvert(BladeValue.IntegerLiteral(enumLiteral.Value), enumLiteral.Type, out BladeValue enumValue) == EvaluationError.None,
                        $"Failed to materialize enum literal '{enumLiteral.MemberName}' of type '{enumLiteral.Type.Name}'.");
                    return EmitConstant(enumValue, enumLiteral.Span);

                case BoundArrayLiteralExpression arrayLiteral:
                    return LowerArrayLiteralExpression(arrayLiteral);

                case BoundMemberAccessExpression memberAccess:
                {
                    MirValueId receiver = LowerExpression(memberAccess.Receiver);
                    if (memberAccess.Member.IsBitfield)
                        return EmitBitfieldExtract(receiver, memberAccess.Member, memberAccess.Span);

                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirLoadMemberInstruction(result, memberAccess.Type, receiver, memberAccess.Member, memberAccess.Span));
                    return result;
                }

                case BoundIndexExpression indexExpression:
                {
                    MirValueId indexed = LowerExpression(indexExpression.Expression);
                    MirValueId index = LowerExpression(indexExpression.Index);
                    bool isVolatile = indexExpression.Expression.Type is MultiPointerTypeSymbol pointer && pointer.IsVolatile;
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirLoadIndexInstruction(
                        result,
                        indexExpression.Type,
                        indexExpression.Expression.Type,
                        indexed,
                        index,
                        GetStorageClass(indexExpression.Expression),
                        isVolatile,
                        indexExpression.Span));
                    return result;
                }

                case BoundPointerDerefExpression pointerDerefExpression:
                {
                    MirValueId pointer = LowerExpression(pointerDerefExpression.Expression);
                    bool isVolatile = pointerDerefExpression.Expression.Type is PointerTypeSymbol pointerType && pointerType.IsVolatile;
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirLoadDerefInstruction(
                        result,
                        pointerDerefExpression.Type,
                        pointerDerefExpression.Expression.Type,
                        pointer,
                        GetStorageClass(pointerDerefExpression.Expression.Type),
                        isVolatile,
                        pointerDerefExpression.Span));
                    return result;
                }

                case BoundIfExpression ifExpression:
                    return LowerIfExpression(ifExpression);

                case BoundRangeExpression:
                    Assert.Unreachable("Range expressions must be consumed by loop lowering before MIR expression lowering."); // pragma: force-coverage
                    break; // pragma: force-coverage

                case BoundStructLiteralExpression structLiteral:
                {
                    StructTypeSymbol structType = (StructTypeSymbol)structLiteral.Type;
                    List<MirStructLiteralField> fields = [];
                    foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                    {
                        MirValueId value = LowerExpression(field.Value);
                        AggregateMemberSymbol member = structType.Members[field.Name];
                        fields.Add(new MirStructLiteralField(member, value));
                    }

                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirStructLiteralInstruction(result, structType, fields, structLiteral.Span));
                    return result;
                }

                case BoundConversionExpression conversionExpression:
                {
                    MirValueId operand = LowerExpression(conversionExpression.Expression);
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirConvertInstruction(result, conversionExpression.Type, operand, conversionExpression.Span));
                    return result;
                }

                case BoundCastExpression castExpression:
                {
                    MirValueId operand = LowerExpression(castExpression.Expression);
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirConvertInstruction(result, castExpression.Type, operand, castExpression.Span));
                    return result;
                }

                case BoundBitcastExpression bitcastExpression:
                {
                    MirValueId operand = LowerExpression(bitcastExpression.Expression);
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirCopyInstruction(result, bitcastExpression.Type, operand, bitcastExpression.Span));
                    return result;
                }

                case BoundErrorExpression:
                    return Assert.UnreachableValue<MirValueId>("MIR lowering must not run on bound expressions that already contain binder errors."); // pragma: force-coverage
            }

            return EmitPlaceholderConstant(BuiltinTypes.Unknown, expression.Span);
        }

        private MirValueId LowerArrayLiteralExpression(BoundArrayLiteralExpression arrayLiteral)
        {
            MirValueId arrayValue = EmitPlaceholderConstant(arrayLiteral.Type, arrayLiteral.Span);
            int explicitCount = arrayLiteral.Elements.Count;
            int producedLength = arrayLiteral.Type.Length ?? explicitCount;

            List<MirValueId> elementValues = new(explicitCount);
            foreach (BoundExpression element in arrayLiteral.Elements)
                elementValues.Add(LowerExpression(element));

            VariableStorageClass storageClass = GetStorageClass(arrayLiteral.Type);
            BladeType arrElemType = arrayLiteral.Type.ElementType;
            for (int i = 0; i < explicitCount; i++)
            {
                MirValueId indexValue = EmitConstant(BladeValue.IntegerLiteral(i), arrayLiteral.Span);
                _currentBlock.Instructions.Add(new MirStoreIndexInstruction(arrElemType, arrayLiteral.Type, arrayValue, indexValue, elementValues[i], storageClass, arrayLiteral.Span));
            }

            if (arrayLiteral.LastElementIsSpread && explicitCount > 0)
            {
                MirValueId spreadValue = elementValues[^1];
                for (int i = explicitCount; i < producedLength; i++)
                {
                    MirValueId indexValue = EmitConstant(BladeValue.IntegerLiteral(i), arrayLiteral.Span);
                    _currentBlock.Instructions.Add(new MirStoreIndexInstruction(arrElemType, arrayLiteral.Type, arrayValue, indexValue, spreadValue, storageClass, arrayLiteral.Span));
                }
            }
            else if (explicitCount == 0)
            {
                for (int i = 0; i < producedLength; i++)
                {
                    MirValueId indexValue = EmitConstant(BladeValue.IntegerLiteral(i), arrayLiteral.Span);
                    MirValueId defaultValue = EmitDefaultValue(arrElemType, arrayLiteral.Span);
                    _currentBlock.Instructions.Add(new MirStoreIndexInstruction(arrElemType, arrayLiteral.Type, arrayValue, indexValue, defaultValue, storageClass, arrayLiteral.Span));
                }
            }

            return arrayValue;
        }

        private MirValueId LowerUnaryExpression(BoundUnaryExpression unaryExpression)
        {
            if (unaryExpression.Operator.Kind == BoundUnaryOperatorKind.AddressOf
                && unaryExpression.Operand is BoundSymbolExpression)
            {
                Assert.Unreachable($"Direct address-of symbol expressions should fold to symbolic pointer literals before MIR lowering. Span={unaryExpression.Span}"); // pragma: force-coverage
            }

            if (unaryExpression.Operator.Kind == BoundUnaryOperatorKind.AddressOf
                && unaryExpression.Operand is BoundIndexExpression indexExpression)
            {
                return LowerIndexedAddress(indexExpression, unaryExpression.Type, unaryExpression.Span);
            }

            MirValueId operand = LowerExpression(unaryExpression.Operand);

            if (unaryExpression.Operator.Kind == BoundUnaryOperatorKind.UnaryPlus)
            {
                MirValueId copyResult = NextValue();
                _currentBlock.Instructions.Add(new MirCopyInstruction(copyResult, unaryExpression.Type, operand, unaryExpression.Span));
                return copyResult;
            }

            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirUnaryInstruction(
                result,
                unaryExpression.Type,
                unaryExpression.Operator.Kind,
                operand,
                unaryExpression.Span));
            return result;
        }

        private MirValueId LowerIndexedAddress(BoundIndexExpression indexExpression, BladeType pointerType, TextSpan span)
        {
            MirValueId baseAddress = LowerExpression(indexExpression.Expression);
            MirValueId offset = LowerExpression(indexExpression.Index);
            VariableStorageClass storageClass = GetStorageClass(indexExpression.Expression);
            int stride = GetPointerElementStride(indexExpression.Type, storageClass);

            MirValueId address = NextValue();
            _currentBlock.Instructions.Add(new MirPointerOffsetInstruction(
                address,
                pointerType,
                BoundBinaryOperatorKind.Add,
                baseAddress,
                offset,
                stride,
                span));
            return address;
        }

        private MirValueId LowerCallExpression(BoundCallExpression callExpression)
        {
            List<MirValueId> arguments = [];
            foreach (BoundExpression argument in callExpression.Arguments)
                arguments.Add(LowerExpression(argument));

            MirValueId? result = callExpression.Type is VoidTypeSymbol ? null : NextValue();
            _currentBlock.Instructions.Add(new MirCallInstruction(
                result,
                callExpression.Type is VoidTypeSymbol ? null : callExpression.Type,
                callExpression.Function,
                arguments,
                callExpression.Span));

            return result ?? EmitPlaceholderConstant(BuiltinTypes.Unknown, callExpression.Span);
        }

        private MirValueId LowerBinaryExpression(BoundBinaryExpression binaryExpression)
        {
            if (binaryExpression.Operator.Kind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
                return LowerShortCircuitBinaryExpression(binaryExpression);

            if (binaryExpression.Left.Type is MultiPointerTypeSymbol leftPointer
                && binaryExpression.Operator.Kind is BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract)
            {
                MirValueId leftValue = LowerExpression(binaryExpression.Left);
                MirValueId rightValue = LowerExpression(binaryExpression.Right);
                MirValueId pointerResult = NextValue();
                int stride = GetPointerElementStride(leftPointer.PointeeType, leftPointer.StorageClass);

                if (binaryExpression.Type is MultiPointerTypeSymbol)
                {
                    _currentBlock.Instructions.Add(new MirPointerOffsetInstruction(
                        pointerResult,
                        binaryExpression.Type,
                        binaryExpression.Operator.Kind,
                        leftValue,
                        rightValue,
                        stride,
                        binaryExpression.Span));
                    return pointerResult;
                }

                _currentBlock.Instructions.Add(new MirPointerDifferenceInstruction(
                    pointerResult,
                    binaryExpression.Type,
                    leftValue,
                    rightValue,
                    stride,
                    binaryExpression.Span));
                return pointerResult;
            }

            MirValueId left = LowerExpression(binaryExpression.Left);
            MirValueId right = LowerExpression(binaryExpression.Right);
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirBinaryInstruction(
                result,
                binaryExpression.Type,
                binaryExpression.Operator.Kind,
                left,
                right,
                binaryExpression.Span));

            // Comparison operators produce flag values using the same polarity as the
            // eventual branch condition: Z/NZ for equality, C/NC for ordering.
            MirFlag? flag = binaryExpression.Operator.Kind switch
            {
                BoundBinaryOperatorKind.Equals => MirFlag.Z,
                BoundBinaryOperatorKind.NotEquals => MirFlag.NZ,
                BoundBinaryOperatorKind.Less => MirFlag.C,
                BoundBinaryOperatorKind.LessOrEqual => MirFlag.NC,
                BoundBinaryOperatorKind.Greater => MirFlag.C,
                BoundBinaryOperatorKind.GreaterOrEqual => MirFlag.NC,
                _ => null,
            };

            if (flag is not null)
                _flagValues[result] = flag.Value;

            return result;
        }

        private MirValueId LowerShortCircuitBinaryExpression(BoundBinaryExpression binaryExpression)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            MirValueId left = LowerExpression(binaryExpression.Left);
            BlockBuilder rhsBlock = CreateBlock();
            BlockBuilder shortCircuitBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();
            MirValueId result = NextValue();
            mergeBlock.Parameters.Add(new MirBlockParameter(result, "logic", BuiltinTypes.Bool));
            Dictionary<Symbol, MirValueId> mergeEnv = CreateEnvironmentParameters(mergeBlock, envSymbols, "logic");

            bool isLogicalAnd = binaryExpression.Operator.Kind == BoundBinaryOperatorKind.LogicalAnd;
            MirBlockRef trueLabel = isLogicalAnd ? rhsBlock.Label : shortCircuitBlock.Label;
            MirBlockRef falseLabel = isLogicalAnd ? shortCircuitBlock.Label : rhsBlock.Label;
            _currentBlock.Terminator = new MirBranchTerminator(
                left,
                trueLabel,
                falseLabel,
                [],
                [],
                binaryExpression.Left.Span);

            _currentBlock = shortCircuitBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            MirValueId shortCircuitValue = EmitConstant(new RuntimeBladeValue(BuiltinTypes.Bool, !isLogicalAnd), binaryExpression.Span);
            List<MirValueId> shortCircuitArguments = new() { shortCircuitValue };
            shortCircuitArguments.AddRange(BuildEnvironmentArguments(envSymbols, binaryExpression.Span));
            _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, shortCircuitArguments, binaryExpression.Span);

            _currentBlock = rhsBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            MirValueId right = LowerExpression(binaryExpression.Right);
            if (_currentBlock.Terminator is null)
            {
                List<MirValueId> rhsArguments = new() { right };
                rhsArguments.AddRange(BuildEnvironmentArguments(envSymbols, binaryExpression.Right.Span));
                _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, rhsArguments, binaryExpression.Right.Span);
            }

            _currentBlock = mergeBlock;
            ReplaceAutomaticEnvironment(mergeEnv);
            return result;
        }

        private MirValueId LowerIfExpression(BoundIfExpression ifExpression)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            MirValueId condition = LowerExpression(ifExpression.Condition);
            BlockBuilder thenBlock = CreateBlock();
            BlockBuilder elseBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();
            MirValueId result = NextValue();
            mergeBlock.Parameters.Add(new MirBlockParameter(result, "ifexpr", ifExpression.Type));
            Dictionary<Symbol, MirValueId> mergeEnv = CreateEnvironmentParameters(mergeBlock, envSymbols, "ifexpr");

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                thenBlock.Label,
                elseBlock.Label,
                [],
                [],
                ifExpression.Condition.Span);

            _currentBlock = thenBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            MirValueId thenValue = LowerExpression(ifExpression.ThenExpression);
            if (_currentBlock.Terminator is null)
            {
                List<MirValueId> arguments = new() { thenValue };
                arguments.AddRange(BuildEnvironmentArguments(envSymbols, ifExpression.ThenExpression.Span));
                _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, arguments, ifExpression.ThenExpression.Span);
            }

            _currentBlock = elseBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            MirValueId elseValue = LowerExpression(ifExpression.ElseExpression);
            if (_currentBlock.Terminator is null)
            {
                List<MirValueId> arguments = new() { elseValue };
                arguments.AddRange(BuildEnvironmentArguments(envSymbols, ifExpression.ElseExpression.Span));
                _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, arguments, ifExpression.ElseExpression.Span);
            }

            _currentBlock = mergeBlock;
            ReplaceAutomaticEnvironment(mergeEnv);
            return result;
        }

        private MirValueId LowerAssignmentTargetRead(BoundAssignmentTarget target)
        {
            return target switch
            {
                BoundSymbolAssignmentTarget symbolTarget => ReadSymbol(symbolTarget.Symbol, symbolTarget.Type, target.Span),
                BoundMemberAssignmentTarget memberTarget => EmitLoadMember(
                    LowerExpression(memberTarget.Receiver),
                    memberTarget.Type,
                    memberTarget.Member,
                    target.Span),
                BoundIndexAssignmentTarget indexTarget => EmitLoadIndex(
                    LowerExpression(indexTarget.Expression),
                    LowerExpression(indexTarget.Index),
                    indexTarget.Expression.Type,
                    indexTarget.Type,
                    GetStorageClass(indexTarget.Expression),
                    indexTarget.Expression.Type is MultiPointerTypeSymbol pointer && pointer.IsVolatile,
                    target.Span),
                BoundPointerDerefAssignmentTarget pointerTarget => EmitLoadDeref(
                    LowerExpression(pointerTarget.Expression),
                    pointerTarget.Expression.Type,
                    pointerTarget.Type,
                    GetStorageClass(pointerTarget.Expression.Type),
                    pointerTarget.Expression.Type is PointerTypeSymbol pointerType && pointerType.IsVolatile,
                    target.Span),
                BoundBitfieldAssignmentTarget bitfieldTarget => EmitBitfieldExtract(
                    LowerExpression(bitfieldTarget.ReceiverValue),
                    bitfieldTarget.Member,
                    target.Span),
                _ => EmitPlaceholderConstant(BuiltinTypes.Unknown, target.Span),
            };
        }

        private void LowerAssignmentTargetWrite(BoundAssignmentTarget target, MirValueId value, TextSpan span)
        {
            switch (target)
            {
                case BoundSymbolAssignmentTarget symbolTarget:
                    WriteSymbol(symbolTarget.Symbol, value, span);
                    return;

                case BoundMemberAssignmentTarget memberTarget:
                {
                    MirValueId receiver = LowerExpression(memberTarget.Receiver);
                    MirValueId updated = EmitAggregateMemberInsert(receiver, value, memberTarget.Receiver.Type, memberTarget.Member, span);
                    WriteAggregateExpression(memberTarget.Receiver, updated, span);
                    return;
                }

                case BoundBitfieldAssignmentTarget bitfieldTarget:
                {
                    MirValueId receiver = LowerExpression(bitfieldTarget.ReceiverValue);
                    MirValueId updated = EmitBitfieldInsert(receiver, value, bitfieldTarget.ReceiverValue.Type, bitfieldTarget.Member, span);
                    LowerAssignmentTargetWrite(bitfieldTarget.ReceiverTarget, updated, span);
                    return;
                }

                case BoundIndexAssignmentTarget indexTarget:
                {
                    MirValueId indexed = LowerExpression(indexTarget.Expression);
                    MirValueId index = LowerExpression(indexTarget.Index);
                    _currentBlock.Instructions.Add(new MirStoreIndexInstruction(indexTarget.Type, indexTarget.Expression.Type, indexed, index, value, GetStorageClass(indexTarget.Expression), span));
                    return;
                }

                case BoundPointerDerefAssignmentTarget pointerTarget:
                {
                    MirValueId pointer = LowerExpression(pointerTarget.Expression);
                    _currentBlock.Instructions.Add(new MirStoreDerefInstruction(pointerTarget.Type, pointerTarget.Expression.Type, pointer, value, GetStorageClass(pointerTarget.Expression), span));
                    return;
                }

                case BoundDiscardAssignmentTarget:
                    // Discard — value computed for side effects only, no store needed.
                    return;

                case BoundErrorAssignmentTarget:
                    Assert.Unreachable("MIR lowering must not run on bound assignment targets that already contain binder errors."); // pragma: force-coverage
                    return; // pragma: force-coverage
            }
        }

        private void WriteAggregateExpression(BoundExpression expression, MirValueId value, TextSpan span)
        {
            switch (expression)
            {
                case BoundSymbolExpression symbolExpression:
                    WriteSymbol(symbolExpression.Symbol, value, span);
                    return;

                case BoundMemberAccessExpression memberAccess:
                {
                    MirValueId receiver = LowerExpression(memberAccess.Receiver);
                    MirValueId updated = memberAccess.Member.IsBitfield
                        ? EmitBitfieldInsert(receiver, value, memberAccess.Receiver.Type, memberAccess.Member, span)
                        : EmitAggregateMemberInsert(receiver, value, memberAccess.Receiver.Type, memberAccess.Member, span);
                    WriteAggregateExpression(memberAccess.Receiver, updated, span);
                    return;
                }

                case BoundIndexExpression indexExpression:
                {
                    MirValueId indexed = LowerExpression(indexExpression.Expression);
                    MirValueId index = LowerExpression(indexExpression.Index);
                    _currentBlock.Instructions.Add(new MirStoreIndexInstruction(indexExpression.Type, indexExpression.Expression.Type, indexed, index, value, GetStorageClass(indexExpression.Expression), span));
                    return;
                }

                case BoundPointerDerefExpression pointerDerefExpression:
                {
                    MirValueId pointer = LowerExpression(pointerDerefExpression.Expression);
                    _currentBlock.Instructions.Add(new MirStoreDerefInstruction(pointerDerefExpression.Type, pointerDerefExpression.Expression.Type, pointer, value, GetStorageClass(pointerDerefExpression.Expression), span));
                    return;
                }
            }

            Assert.Unreachable($"Unsupported aggregate write expression '{expression.Kind}'."); // pragma: force-coverage
        }

        private void WriteSymbol(Symbol symbol, MirValueId value, TextSpan span)
        {
                if (TryGetStoragePlace(symbol, out StoragePlace place))
                {
                    EmitStorePlace(place, value, span);
                    return;
                }

            _currentValues[symbol] = value;
        }

        private MirValueId ReadSymbol(Symbol symbol, BladeType type, TextSpan span)
        {
            if (TryGetStoragePlace(symbol, out StoragePlace place))
                return EmitLoadPlace(place, type, span);

            if (_currentValues.TryGetValue(symbol, out MirValueId? value) && value is not null)
                return value;

            MirValueId defaultValue = EmitDefaultValue(type, span);
            _currentValues[symbol] = defaultValue;
            return defaultValue;
        }

        private bool TryGetStoragePlace(Symbol symbol, out StoragePlace place)
        {
            if (_storagePlacesBySymbol.TryGetValue(symbol, out StoragePlace? resolved))
            {
                place = resolved;
                return true;
            }

            place = null!;
            return false;
        }

        private Dictionary<Symbol, MirValueId> SnapshotAutomaticEnvironment()
        {
            Dictionary<Symbol, MirValueId> snapshot = [];
            foreach ((Symbol symbol, MirValueId value) in _currentValues)
            {
                if (IsAutomaticSymbol(symbol))
                    snapshot[symbol] = value;
            }

            return snapshot;
        }

        private void ReplaceAutomaticEnvironment(IReadOnlyDictionary<Symbol, MirValueId> newValues)
        {
            List<Symbol> toRemove = [];
            foreach (Symbol symbol in _currentValues.Keys)
            {
                if (IsAutomaticSymbol(symbol))
                    toRemove.Add(symbol);
            }

            foreach (Symbol symbol in toRemove)
                _currentValues.Remove(symbol);

            foreach ((Symbol symbol, MirValueId value) in newValues)
                _currentValues[symbol] = value;
        }

        private static bool IsAutomaticSymbol(Symbol symbol)
        {
            return symbol is AutomaticVariableSymbol;
        }

        private static bool IsInlineAsmTempSymbol(Symbol symbol)
            => symbol is VariableSymbol { ScopeKind: VariableScopeKind.InlineAsmTemporary };

        private static BladeType GetSymbolType(Symbol symbol)
        {
            return symbol switch
            {
                VariableSymbol variable => variable.Type,
                _ => BuiltinTypes.Unknown,
            };
        }

        private static IReadOnlyList<Symbol> GetOrderedAutomaticSymbols(IReadOnlyDictionary<Symbol, MirValueId> values)
        {
            return [.. values.Keys.OrderBy(static symbol => symbol.Name, StringComparer.Ordinal)];
        }

        private Dictionary<Symbol, MirValueId> CreateEnvironmentParameters(BlockBuilder block, IReadOnlyList<Symbol> symbols, string prefix)
        {
            Dictionary<Symbol, MirValueId> values = new(symbols.Count);
            foreach (Symbol symbol in symbols)
            {
                MirValueId value = NextValue();
                block.Parameters.Add(new MirBlockParameter(value, $"{prefix}_{symbol.Name}", GetSymbolType(symbol)));
                values[symbol] = value;
            }

            return values;
        }

        private List<MirValueId> BuildEnvironmentArguments(IReadOnlyList<Symbol> symbols, TextSpan span)
        {
            List<MirValueId> arguments = new(symbols.Count);
            foreach (Symbol symbol in symbols)
            {
                if (_currentValues.TryGetValue(symbol, out MirValueId? value) && value is not null)
                {
                    arguments.Add(value);
                }
                else
                {
                    arguments.Add(EmitDefaultValue(GetSymbolType(symbol), span));
                }
            }

            return arguments;
        }

        private static bool TryMapCompoundOperator(TokenKind operatorKind, out BoundBinaryOperatorKind binaryKind)
        {
            binaryKind = operatorKind switch
            {
                TokenKind.PlusEqual => BoundBinaryOperatorKind.Add,
                TokenKind.MinusEqual => BoundBinaryOperatorKind.Subtract,
                TokenKind.StarEqual => BoundBinaryOperatorKind.Multiply,
                TokenKind.SlashEqual => BoundBinaryOperatorKind.Divide,
                TokenKind.PercentEqual => BoundBinaryOperatorKind.Modulo,
                TokenKind.AmpersandEqual => BoundBinaryOperatorKind.BitwiseAnd,
                TokenKind.PipeEqual => BoundBinaryOperatorKind.BitwiseOr,
                TokenKind.CaretEqual => BoundBinaryOperatorKind.BitwiseXor,
                TokenKind.LessLessEqual => BoundBinaryOperatorKind.ShiftLeft,
                TokenKind.GreaterGreaterEqual => BoundBinaryOperatorKind.ShiftRight,
                TokenKind.LessLessLessEqual => BoundBinaryOperatorKind.ArithmeticShiftLeft,
                TokenKind.GreaterGreaterGreaterEqual => BoundBinaryOperatorKind.ArithmeticShiftRight,
                TokenKind.RotateLeftEqual => BoundBinaryOperatorKind.RotateLeft,
                TokenKind.RotateRightEqual => BoundBinaryOperatorKind.RotateRight,
                _ => default,
            };

            return operatorKind is TokenKind.PlusEqual
                or TokenKind.MinusEqual
                or TokenKind.StarEqual
                or TokenKind.SlashEqual
                or TokenKind.PercentEqual
                or TokenKind.AmpersandEqual
                or TokenKind.PipeEqual
                or TokenKind.CaretEqual
                or TokenKind.LessLessEqual
                or TokenKind.GreaterGreaterEqual
                or TokenKind.LessLessLessEqual
                or TokenKind.GreaterGreaterGreaterEqual
                or TokenKind.RotateLeftEqual
                or TokenKind.RotateRightEqual;
        }

        private MirValueId EmitConstant(BladeValue value, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirConstantInstruction(id, value.Type, value, span));
            return id;
        }

        private MirValueId EmitPlaceholderConstant(BladeType type, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirConstantInstruction(id, type, null, span));
            return id;
        }

        private MirValueId EmitLoadPlace(StoragePlace place, BladeType type, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirLoadPlaceInstruction(id, type, place, span));
            return id;
        }

        private MirValueId EmitLoadMember(MirValueId receiver, BladeType type, AggregateMemberSymbol member, TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirLoadMemberInstruction(result, type, receiver, member, span));
            return result;
        }

        private MirValueId EmitLoadIndex(
            MirValueId indexed,
            MirValueId index,
            BladeType indexedType,
            BladeType type,
            VariableStorageClass storageClass,
            bool hasSideEffects,
            TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirLoadIndexInstruction(result, type, indexedType, indexed, index, storageClass, hasSideEffects, span));
            return result;
        }

        private MirValueId EmitLoadDeref(
            MirValueId pointer,
            BladeType pointerType,
            BladeType type,
            VariableStorageClass storageClass,
            bool hasSideEffects,
            TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirLoadDerefInstruction(result, type, pointerType, pointer, storageClass, hasSideEffects, span));
            return result;
        }

        private static int GetPointerElementStride(BladeType pointeeType, VariableStorageClass storageClass)
        {
            Assert.Invariant(pointeeType is RuntimeTypeSymbol, $"Pointer arithmetic requires a concrete element stride for '{pointeeType.Name}' in '{storageClass}'.");
            RuntimeTypeSymbol runtimeType = (RuntimeTypeSymbol)pointeeType;
            return runtimeType.GetPointerElementStride(storageClass);
        }

        private MirValueId EmitBitfieldExtract(MirValueId receiver, AggregateMemberSymbol member, TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirBitfieldExtractInstruction(result, member.Type, receiver, member, span));
            return result;
        }

        private MirValueId EmitBitfieldInsert(MirValueId receiver, MirValueId value, BladeType aggregateType, AggregateMemberSymbol member, TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirBitfieldInsertInstruction(result, aggregateType, receiver, value, member, span));
            return result;
        }

        private MirValueId EmitAggregateMemberInsert(MirValueId receiver, MirValueId value, BladeType aggregateType, AggregateMemberSymbol member, TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirInsertMemberInstruction(result, aggregateType, receiver, value, member, span));
            return result;
        }

        private void EmitStorePlace(StoragePlace place, MirValueId value, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirStorePlaceInstruction(place, value, span));
        }

        private void EmitUpdatePlace(StoragePlace place, BoundBinaryOperatorKind operatorKind, MirValueId value, TextSpan span)
        {
            if (place.StorageClass is VariableStorageClass.Lut or VariableStorageClass.Hub)
            {
                BladeType placeType = GetSymbolType(place.Symbol);
                if (placeType is not MultiPointerTypeSymbol)
                {
                    MirValueId loaded = NextValue();
                    _currentBlock.Instructions.Add(new MirLoadPlaceInstruction(loaded, placeType, place, span));
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirBinaryInstruction(result, placeType, operatorKind, loaded, value, span));
                    _currentBlock.Instructions.Add(new MirStorePlaceInstruction(place, result, span));
                    return;
                }
            }

            int? pointerStride = null;
            if (GetSymbolType(place.Symbol) is MultiPointerTypeSymbol pointerType)
            {
                pointerStride = GetPointerElementStride(pointerType.PointeeType, pointerType.StorageClass);
            }

            _currentBlock.Instructions.Add(new MirUpdatePlaceInstruction(place, operatorKind, value, span, pointerStride));
        }

        private sealed class BlockBuilder(MirBlockRef label)
        {
            public MirBlockRef Label { get; } = label;
            public List<MirBlockParameter> Parameters { get; } = [];
            public List<MirInstruction> Instructions { get; } = [];
            public MirTerminator? Terminator { get; set; }
        }

        private readonly record struct LoopContext(MirBlockRef BreakLabel, MirBlockRef ContinueLabel, IReadOnlyList<Symbol> Symbols);
    }

    private static bool IsUndefinedInitializer(BoundExpression expression)
    {
        // Unwrap conversions to find the underlying literal.
        BoundExpression inner = expression;
        while (inner is BoundConversionExpression conversion)
            inner = conversion.Expression;

        return inner.Type is UndefinedLiteralTypeSymbol
            || (inner is BoundLiteralExpression { Value.Value: UndefinedValue } literal && literal.Type is not VoidTypeSymbol);
    }

    private static bool TryEvaluateStaticValue(BoundExpression expression, BladeType targetType, out RuntimeBladeValue? value)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal when targetType is RuntimeTypeSymbol runtimeType:
                if (BladeValue.TryConvert(literal.Value, runtimeType, out BladeValue normalizedValue) != EvaluationError.None)
                    break;

                if (normalizedValue is RuntimeBladeValue runtimeValue)
                {
                    value = runtimeValue;
                    return true;
                }

                break;
        }

        value = null;
        return false;
    }
}
