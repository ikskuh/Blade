using System;
using System.Collections.Generic;
using System.Globalization;
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

        List<StoragePlace> storagePlaces = CollectStoragePlaces(program);

        List<MirFunction> functions = new();
        functions.Add(LowerTopLevel(program, storagePlaces));

        foreach (BoundFunctionMember functionMember in program.Functions)
            functions.Add(LowerFunction(functionMember, storagePlaces));

        return new MirModule(storagePlaces, functions);
    }

    private static MirFunction LowerTopLevel(BoundProgram program, IReadOnlyList<StoragePlace> storagePlaces)
    {
        FunctionLoweringContext context = new("$top", isEntryPoint: true, FunctionKind.Default, [], storagePlaces);
        context.LowerTopLevel(program.GlobalVariables, program.TopLevelStatements);
        return context.Build();
    }

    private static MirFunction LowerFunction(BoundFunctionMember functionMember, IReadOnlyList<StoragePlace> storagePlaces)
    {
        FunctionSymbol symbol = functionMember.Symbol;
        FunctionLoweringContext context = new(
            symbol.Name,
            isEntryPoint: false,
            symbol.Kind,
            symbol.ReturnTypes,
            storagePlaces);
        context.LowerFunctionBody(functionMember.Body, symbol.Parameters);
        return context.Build();
    }

    private static List<StoragePlace> CollectStoragePlaces(BoundProgram program)
    {
        List<StoragePlace> places = new(program.GlobalVariables.Count);
        HashSet<int> seenSymbolIds = [];
        foreach (BoundGlobalVariableMember global in program.GlobalVariables)
        {
            VariableSymbol symbol = global.Symbol;
            if (!symbol.UsesGlobalRegisterStorage)
                continue;

            StoragePlaceKind kind = symbol.FixedAddress.HasValue
                ? StoragePlaceKind.FixedRegisterAlias
                : symbol.IsExtern
                    ? StoragePlaceKind.ExternalAlias
                    : StoragePlaceKind.AllocatableGlobalRegister;

            object? staticInitializer = null;
            if (kind == StoragePlaceKind.AllocatableGlobalRegister
                && global.Initializer is not null
                && TryEvaluateStaticValue(global.Initializer, out object? value))
            {
                staticInitializer = value;
            }

            places.Add(new StoragePlace(symbol, kind, symbol.FixedAddress, staticInitializer));
            seenSymbolIds.Add(symbol.Id);
        }

        foreach (Symbol symbol in CollectAddressTakenSymbols(program))
        {
            if (!seenSymbolIds.Add(symbol.Id))
                continue;

            places.Add(new StoragePlace(symbol, StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null));
        }

        return places;
    }

    private static IReadOnlyList<Symbol> CollectAddressTakenSymbols(BoundProgram program)
    {
        Dictionary<int, Symbol> symbols = [];

        foreach (BoundStatement statement in program.TopLevelStatements)
            CollectAddressTakenSymbols(statement, symbols);

        foreach (BoundFunctionMember function in program.Functions)
            CollectAddressTakenSymbols(function.Body, symbols);

        return [.. symbols.Values];
    }

    private static void CollectAddressTakenSymbols(BoundStatement statement, IDictionary<int, Symbol> symbols)
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
                CollectAddressTakenSymbols(forStatement.Body, symbols);
                break;
            case BoundLoopStatement loopStatement:
                CollectAddressTakenSymbols(loopStatement.Body, symbols);
                break;
            case BoundRepLoopStatement repLoop:
                CollectAddressTakenSymbols(repLoop.Count, symbols);
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

    private static void CollectAddressTakenSymbols(BoundExpression expression, IDictionary<int, Symbol> symbols)
    {
        switch (expression)
        {
            case BoundUnaryExpression unary when unary.Operator.Kind == BoundUnaryOperatorKind.AddressOf
                && unary.Operand is BoundSymbolExpression symbolExpression:
                symbols[symbolExpression.Symbol.Id] = symbolExpression.Symbol;
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
                foreach (BoundStatement statement in moduleCall.Module.Program.TopLevelStatements)
                    CollectAddressTakenSymbols(statement, symbols);
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

    private sealed class FunctionLoweringContext
    {
        private readonly string _name;
        private readonly bool _isEntryPoint;
        private readonly FunctionKind _kind;
        private readonly IReadOnlyList<TypeSymbol> _returnTypes;
        private readonly List<BlockBuilder> _blocks = [];
        private readonly Stack<LoopContext> _loopStack = [];
        private readonly Dictionary<int, StoragePlace> _storagePlacesBySymbolId = [];
        private readonly BlockBuilder _entryBlock;
        private readonly BlockBuilder _exitBlock;
        private readonly Dictionary<Symbol, MirValueId> _currentValues = [];
        private BlockBuilder _currentBlock;
        private int _nextBlockId;
        private int _nextValueId;

        public FunctionLoweringContext(
            string name,
            bool isEntryPoint,
            FunctionKind kind,
            IReadOnlyList<TypeSymbol> returnTypes,
            IReadOnlyList<StoragePlace> storagePlaces)
        {
            _name = name;
            _isEntryPoint = isEntryPoint;
            _kind = kind;
            _returnTypes = returnTypes;
            foreach (StoragePlace place in storagePlaces)
                _storagePlacesBySymbolId[place.Symbol.Id] = place;

            _entryBlock = CreateBlock();
            _exitBlock = CreateBlock();
            _currentBlock = _entryBlock;

            for (int i = 0; i < _returnTypes.Count; i++)
            {
                TypeSymbol returnType = _returnTypes[i];
                MirValueId value = NextValue();
                _exitBlock.Parameters.Add(new MirBlockParameter(value, $"ret{i}", returnType));
            }
        }

        public void LowerTopLevel(
            IReadOnlyList<BoundGlobalVariableMember> globalVariables,
            IReadOnlyList<BoundStatement> statements)
        {
            foreach (BoundGlobalVariableMember global in globalVariables)
            {
                if (global.Initializer is null || !TryGetStoragePlace(global.Symbol, out StoragePlace place))
                    continue;

                if (place.Kind == StoragePlaceKind.AllocatableGlobalRegister && place.HasStaticInitializer)
                    continue;

                MirValueId initializerValue = LowerExpression(global.Initializer);
                EmitStorePlace(place, initializerValue, global.Span);
            }

            foreach (BoundStatement statement in statements)
                LowerStatement(statement);

            EmitFallthroughReturn(new TextSpan(0, 0));
        }

        public void LowerFunctionBody(BoundBlockStatement body, IReadOnlyList<ParameterSymbol> parameters)
        {
            foreach (ParameterSymbol parameter in parameters)
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

            return new MirFunction(_name, _isEntryPoint, _kind, _returnTypes, blocks);
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
            BlockBuilder block = new($"bb{_nextBlockId++}");
            _blocks.Add(block);
            return block;
        }

        private MirValueId NextValue() => new(_nextValueId++);

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
                    if (variableDeclaration.Initializer is not null)
                    {
                        MirValueId initializer = LowerExpression(variableDeclaration.Initializer);
                        WriteSymbol(variableDeclaration.Symbol, initializer, statement.Span);
                    }
                    break;

                case BoundAssignmentStatement assignment:
                    LowerAssignmentStatement(assignment);
                    break;

                case BoundExpressionStatement expressionStatement:
                    if (expressionStatement.Expression is BoundModuleCallExpression moduleCallExpression)
                    {
                        foreach (BoundStatement moduleStatement in moduleCallExpression.Module.Program.TopLevelStatements)
                            LowerStatement(moduleStatement);
                    }
                    else
                    {
                        _ = LowerExpression(expressionStatement.Expression);
                    }
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
                    EmitOp("noirq.begin", [], hasSideEffects: true, noirqStatement.Span);
                    LowerStatement(noirqStatement.Body);
                    EnsureWritableBlock();
                    EmitOp("noirq.end", [], hasSideEffects: true, noirqStatement.Span);
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
                    EmitOp("yield", [], hasSideEffects: true, yieldStatement.Span);
                    break;

                case BoundYieldtoStatement yieldtoStatement:
                {
                    List<MirValueId> arguments = [];
                    foreach (BoundExpression argument in yieldtoStatement.Arguments)
                        arguments.Add(LowerExpression(argument));

                    string target = yieldtoStatement.Target?.Name ?? "<error>";
                    EmitOp($"yieldto:{target}", arguments, hasSideEffects: true, yieldtoStatement.Span);
                    break;
                }

                case BoundAsmStatement asmStatement:
                    LowerInlineAsmStatement(asmStatement);
                    break;

                case BoundErrorStatement errorStatement:
                    EmitOp("error.statement", [], hasSideEffects: true, errorStatement.Span);
                    break;
            }
        }

        private void LowerInlineAsmStatement(BoundAsmStatement asmStatement)
        {
            string[] bindingNames = [.. asmStatement.ReferencedSymbols.Keys];
            IReadOnlyDictionary<string, InlineAsmBindingAccess> bindingAccess =
                InlineAssemblyBindingAnalysis.ComputeBindingAccess(
                    asmStatement.Volatility,
                    asmStatement.FlagOutput,
                    asmStatement.ParsedLines,
                    bindingNames);

            List<MirInlineAsmBinding> bindings = new(asmStatement.ReferencedSymbols.Count);
            foreach ((string name, Symbol symbol) in asmStatement.ReferencedSymbols)
            {
                InlineAsmBindingAccess access = bindingAccess.GetValueOrDefault(name, InlineAsmBindingAccess.ReadWrite);
                if (TryGetStoragePlace(symbol, out StoragePlace? place))
                {
                    bindings.Add(new MirInlineAsmBinding(name, value: null, place, access));
                }
                else
                {
                    TypeSymbol type = GetSymbolType(symbol);
                    MirValueId value = ReadSymbol(symbol, type, asmStatement.Span);
                    bindings.Add(new MirInlineAsmBinding(name, value, place: null, access));
                }
            }

            _currentBlock.Instructions.Add(new MirInlineAsmInstruction(
                asmStatement.Volatility,
                asmStatement.Body,
                asmStatement.FlagOutput,
                asmStatement.ParsedLines,
                bindings,
                asmStatement.Span));
        }

        private void LowerLoopTransfer(bool isBreak, TextSpan span)
        {
            if (_loopStack.Count == 0)
            {
                _currentBlock.Terminator = new MirUnreachableTerminator(span);
                return;
            }

            LoopContext loop = _loopStack.Peek();
            string target = isBreak ? loop.BreakLabel : loop.ContinueLabel;
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

        private void LowerIfStatement(BoundIfStatement ifStatement)
        {
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            MirValueId condition = LowerExpression(ifStatement.Condition);
            BlockBuilder thenBlock = CreateBlock();
            BlockBuilder elseBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> mergeEnv = CreateEnvironmentParameters(mergeBlock, envSymbols, "if");

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                thenBlock.Label,
                elseBlock.Label,
                [],
                [],
                ifStatement.Span);

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
            Dictionary<Symbol, MirValueId> beforeEnv = SnapshotAutomaticEnvironment();
            IReadOnlyList<Symbol> envSymbols = GetOrderedAutomaticSymbols(beforeEnv);

            BlockBuilder conditionBlock = CreateBlock();
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            Dictionary<Symbol, MirValueId> conditionEnv = CreateEnvironmentParameters(conditionBlock, envSymbols, "for");
            Dictionary<Symbol, MirValueId> bodyEnv = CreateEnvironmentParameters(bodyBlock, envSymbols, "for");
            Dictionary<Symbol, MirValueId> exitEnv = CreateEnvironmentParameters(exitBlock, envSymbols, "for");

            _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, BuildEnvironmentArguments(envSymbols, forStatement.Span), forStatement.Span);

            _currentBlock = conditionBlock;
            ReplaceAutomaticEnvironment(conditionEnv);
            MirValueId condition = forStatement.Variable is null
                ? EmitConstant(true, BuiltinTypes.Bool, forStatement.Span)
                : LowerConditionVariable(forStatement.Variable, forStatement.Span);

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                bodyBlock.Label,
                exitBlock.Label,
                BuildEnvironmentArguments(envSymbols, forStatement.Span),
                BuildEnvironmentArguments(envSymbols, forStatement.Span),
                forStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, conditionBlock.Label, envSymbols));
            _currentBlock = bodyBlock;
            ReplaceAutomaticEnvironment(bodyEnv);
            LowerStatement(forStatement.Body);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    conditionBlock.Label,
                    BuildEnvironmentArguments(envSymbols, forStatement.Body.Span),
                    forStatement.Body.Span);
            }
            _loopStack.Pop();

            _currentBlock = exitBlock;
            ReplaceAutomaticEnvironment(exitEnv);
        }

        private MirValueId LowerConditionVariable(Symbol symbol, TextSpan span)
        {
            TypeSymbol type = GetSymbolType(symbol);
            return ReadSymbol(symbol, type, span);
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
            MirValueId count = LowerExpression(repLoopStatement.Count);
            EmitOp("rep.setup", [count], hasSideEffects: true, repLoopStatement.Span);

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
            EmitOp("rep.iter", [count], hasSideEffects: true, repLoopStatement.Span);
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
            EmitOp("repfor.setup", [start, end], hasSideEffects: true, repForStatement.Span);

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
            EmitOp("repfor.iter", [start, end], hasSideEffects: true, repForStatement.Span);
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
            List<MirValueId> arguments = [];
            for (int i = 0; i < _exitBlock.Parameters.Count; i++)
            {
                if (i < values.Count)
                {
                    arguments.Add(values[i]);
                }
                else
                {
                    TypeSymbol expectedType = _exitBlock.Parameters[i].Type;
                    arguments.Add(EmitDefaultValue(expectedType, span));
                }
            }

            return arguments;
        }

        private MirValueId EmitDefaultValue(TypeSymbol type, TextSpan span)
        {
            object? value = type.IsBool ? false : type.IsInteger ? 0 : null;
            return EmitConstant(value, type, span);
        }

        private MirValueId LowerExpression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    return EmitConstant(literal.Value, literal.Type, literal.Span);

                case BoundSymbolExpression symbolExpression:
                    return ReadSymbol(symbolExpression.Symbol, symbolExpression.Type, symbolExpression.Span);

                case BoundUnaryExpression unaryExpression:
                    return LowerUnaryExpression(unaryExpression);

                case BoundBinaryExpression binaryExpression:
                    return LowerBinaryExpression(binaryExpression);

                case BoundCallExpression callExpression:
                    return LowerCallExpression(callExpression);

                case BoundModuleCallExpression moduleCallExpression:
                    foreach (BoundStatement moduleStatement in moduleCallExpression.Module.Program.TopLevelStatements)
                        LowerStatement(moduleStatement);
                    return EmitConstant(null, BuiltinTypes.Void, moduleCallExpression.Span);

                case BoundIntrinsicCallExpression intrinsicCall:
                {
                    List<MirValueId> arguments = [];
                    foreach (BoundExpression argument in intrinsicCall.Arguments)
                        arguments.Add(LowerExpression(argument));

                    MirValueId? result = intrinsicCall.Type.IsVoid ? null : NextValue();
                    _currentBlock.Instructions.Add(new MirIntrinsicCallInstruction(
                        result,
                        intrinsicCall.Type.IsVoid ? null : intrinsicCall.Type,
                        intrinsicCall.Name,
                        arguments,
                        intrinsicCall.Span));
                    return result ?? EmitConstant(null, BuiltinTypes.Unknown, intrinsicCall.Span);
                }

                case BoundEnumLiteralExpression enumLiteral:
                    return EmitConstant(enumLiteral.Value, enumLiteral.Type, enumLiteral.Span);

                case BoundMemberAccessExpression memberAccess:
                {
                    MirValueId receiver = LowerExpression(memberAccess.Receiver);
                    if (memberAccess.Member.IsBitfield)
                        return EmitBitfieldExtract(receiver, memberAccess.Member, memberAccess.Span);

                    return EmitOp(
                        $"load.member.{memberAccess.MemberName}",
                        memberAccess.Type,
                        [receiver],
                        hasSideEffects: false,
                        memberAccess.Span);
                }

                case BoundIndexExpression indexExpression:
                {
                    MirValueId indexed = LowerExpression(indexExpression.Expression);
                    MirValueId index = LowerExpression(indexExpression.Index);
                    bool isVolatile = indexExpression.Expression.Type is MultiPointerTypeSymbol pointer && pointer.IsVolatile;
                    return EmitOp("load.index", indexExpression.Type, [indexed, index], hasSideEffects: isVolatile, indexExpression.Span);
                }

                case BoundPointerDerefExpression pointerDerefExpression:
                {
                    MirValueId pointer = LowerExpression(pointerDerefExpression.Expression);
                    bool isVolatile = pointerDerefExpression.Expression.Type is PointerTypeSymbol pointerType && pointerType.IsVolatile;
                    return EmitOp("load.deref", pointerDerefExpression.Type, [pointer], hasSideEffects: isVolatile, pointerDerefExpression.Span);
                }

                case BoundIfExpression ifExpression:
                    return LowerIfExpression(ifExpression);

                case BoundRangeExpression rangeExpression:
                {
                    MirValueId start = LowerExpression(rangeExpression.Start);
                    MirValueId end = LowerExpression(rangeExpression.End);
                    return EmitOp("range", rangeExpression.Type, [start, end], hasSideEffects: false, rangeExpression.Span);
                }

                case BoundStructLiteralExpression structLiteral:
                {
                    List<MirValueId> fieldValues = [];
                    string opcode = "structlit";
                    foreach (BoundStructFieldInitializer field in structLiteral.Fields)
                    {
                        fieldValues.Add(LowerExpression(field.Value));
                        opcode += $".{field.Name}";
                    }

                    return EmitOp(opcode, structLiteral.Type, fieldValues, hasSideEffects: false, structLiteral.Span);
                }

                case BoundConversionExpression conversionExpression:
                {
                    MirValueId operand = LowerExpression(conversionExpression.Expression);
                    return EmitOp("convert", conversionExpression.Type, [operand], hasSideEffects: false, conversionExpression.Span);
                }

                case BoundCastExpression castExpression:
                {
                    MirValueId operand = LowerExpression(castExpression.Expression);
                    return EmitOp("convert", castExpression.Type, [operand], hasSideEffects: false, castExpression.Span);
                }

                case BoundBitcastExpression bitcastExpression:
                {
                    MirValueId operand = LowerExpression(bitcastExpression.Expression);
                    MirValueId result = NextValue();
                    _currentBlock.Instructions.Add(new MirCopyInstruction(result, bitcastExpression.Type, operand, bitcastExpression.Span));
                    return result;
                }

                case BoundErrorExpression errorExpression:
                    return EmitConstant(null, BuiltinTypes.Unknown, errorExpression.Span);
            }

            return EmitConstant(null, BuiltinTypes.Unknown, expression.Span);
        }

        private MirValueId LowerUnaryExpression(BoundUnaryExpression unaryExpression)
        {
            if (unaryExpression.Operator.Kind == BoundUnaryOperatorKind.AddressOf
                && unaryExpression.Operand is BoundSymbolExpression symbolExpression
                && TryGetStoragePlace(symbolExpression.Symbol, out StoragePlace addressPlace))
            {
                MirValueId addressResult = NextValue();
                _currentBlock.Instructions.Add(new MirLoadSymbolInstruction(
                    addressResult,
                    unaryExpression.Type,
                    addressPlace.EmittedName,
                    unaryExpression.Span));
                return addressResult;
            }

            MirValueId operand = LowerExpression(unaryExpression.Operand);
            if (unaryExpression.Operator.Kind is BoundUnaryOperatorKind.PostIncrement or BoundUnaryOperatorKind.PostDecrement)
            {
                BoundBinaryOperatorKind binaryKind = unaryExpression.Operator.Kind == BoundUnaryOperatorKind.PostIncrement
                    ? BoundBinaryOperatorKind.Add
                    : BoundBinaryOperatorKind.Subtract;
                MirValueId one = EmitConstant(1, unaryExpression.Type, unaryExpression.Span);
                MirValueId updated = NextValue();
                _currentBlock.Instructions.Add(new MirBinaryInstruction(
                    updated,
                    unaryExpression.Type,
                    binaryKind,
                    operand,
                    one,
                    unaryExpression.Span));
                return updated;
            }

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

        private MirValueId LowerCallExpression(BoundCallExpression callExpression)
        {
            List<MirValueId> arguments = [];
            foreach (BoundExpression argument in callExpression.Arguments)
                arguments.Add(LowerExpression(argument));

            MirValueId? result = callExpression.Type.IsVoid ? null : NextValue();
            _currentBlock.Instructions.Add(new MirCallInstruction(
                result,
                callExpression.Type.IsVoid ? null : callExpression.Type,
                callExpression.Function.Name,
                arguments,
                callExpression.Span));

            return result ?? EmitConstant(null, BuiltinTypes.Unknown, callExpression.Span);
        }

        private MirValueId LowerBinaryExpression(BoundBinaryExpression binaryExpression)
        {
            if (binaryExpression.Operator.Kind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
                return LowerShortCircuitBinaryExpression(binaryExpression);

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
            string trueLabel = isLogicalAnd ? rhsBlock.Label : shortCircuitBlock.Label;
            string falseLabel = isLogicalAnd ? shortCircuitBlock.Label : rhsBlock.Label;
            _currentBlock.Terminator = new MirBranchTerminator(
                left,
                trueLabel,
                falseLabel,
                [],
                [],
                binaryExpression.Left.Span);

            _currentBlock = shortCircuitBlock;
            ReplaceAutomaticEnvironment(beforeEnv);
            MirValueId shortCircuitValue = EmitConstant(!isLogicalAnd, BuiltinTypes.Bool, binaryExpression.Span);
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
                BoundMemberAssignmentTarget memberTarget => EmitOp(
                    $"load.member.{memberTarget.MemberName}",
                    memberTarget.Type,
                    [LowerExpression(memberTarget.Receiver)],
                    hasSideEffects: false,
                    target.Span),
                BoundIndexAssignmentTarget indexTarget => EmitOp(
                    "load.index",
                    indexTarget.Type,
                    [LowerExpression(indexTarget.Expression), LowerExpression(indexTarget.Index)],
                    hasSideEffects: indexTarget.Expression.Type is MultiPointerTypeSymbol pointer && pointer.IsVolatile,
                    target.Span),
                BoundPointerDerefAssignmentTarget pointerTarget => EmitOp(
                    "load.deref",
                    pointerTarget.Type,
                    [LowerExpression(pointerTarget.Expression)],
                    hasSideEffects: pointerTarget.Expression.Type is PointerTypeSymbol pointerType && pointerType.IsVolatile,
                    target.Span),
                BoundBitfieldAssignmentTarget bitfieldTarget => EmitBitfieldExtract(
                    LowerExpression(bitfieldTarget.ReceiverValue),
                    bitfieldTarget.Member,
                    target.Span),
                _ => EmitConstant(null, BuiltinTypes.Unknown, target.Span),
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
                    EmitStore("member:" + memberTarget.MemberName, [receiver, value], span);
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
                    EmitStore("index", [indexed, index, value], span);
                    return;
                }

                case BoundPointerDerefAssignmentTarget pointerTarget:
                {
                    MirValueId pointer = LowerExpression(pointerTarget.Expression);
                    EmitStore("deref", [pointer, value], span);
                    return;
                }

                case BoundErrorAssignmentTarget:
                    EmitOp("store.error", [value], hasSideEffects: true, span);
                    return;
            }
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

        private MirValueId ReadSymbol(Symbol symbol, TypeSymbol type, TextSpan span)
        {
            if (TryGetStoragePlace(symbol, out StoragePlace place))
                return EmitLoadPlace(place, type, span);

            if (_currentValues.TryGetValue(symbol, out MirValueId value))
                return value;

            MirValueId defaultValue = EmitDefaultValue(type, span);
            _currentValues[symbol] = defaultValue;
            return defaultValue;
        }

        private bool TryGetStoragePlace(Symbol symbol, out StoragePlace place)
        {
            if (_storagePlacesBySymbolId.TryGetValue(symbol.Id, out StoragePlace? resolved))
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
            return symbol switch
            {
                VariableSymbol variable => variable.IsAutomatic,
                ParameterSymbol => true,
                _ => false,
            };
        }

        private static TypeSymbol GetSymbolType(Symbol symbol)
        {
            return symbol switch
            {
                VariableSymbol variable => variable.Type,
                ParameterSymbol parameter => parameter.Type,
                _ => BuiltinTypes.Unknown,
            };
        }

        private static IReadOnlyList<Symbol> GetOrderedAutomaticSymbols(IReadOnlyDictionary<Symbol, MirValueId> values)
        {
            List<Symbol> symbols = new(values.Count);
            foreach (Symbol symbol in values.Keys)
                symbols.Add(symbol);
            symbols.Sort((left, right) => left.Id.CompareTo(right.Id));
            return symbols;
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
                if (_currentValues.TryGetValue(symbol, out MirValueId value))
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
                TokenKind.PercentEqual => BoundBinaryOperatorKind.Modulo,
                TokenKind.AmpersandEqual => BoundBinaryOperatorKind.BitwiseAnd,
                TokenKind.PipeEqual => BoundBinaryOperatorKind.BitwiseOr,
                TokenKind.CaretEqual => BoundBinaryOperatorKind.BitwiseXor,
                TokenKind.LessLessEqual => BoundBinaryOperatorKind.ShiftLeft,
                TokenKind.GreaterGreaterEqual => BoundBinaryOperatorKind.ShiftRight,
                _ => default,
            };

            return operatorKind is TokenKind.PlusEqual
                or TokenKind.MinusEqual
                or TokenKind.PercentEqual
                or TokenKind.AmpersandEqual
                or TokenKind.PipeEqual
                or TokenKind.CaretEqual
                or TokenKind.LessLessEqual
                or TokenKind.GreaterGreaterEqual;
        }

        private MirValueId EmitConstant(object? value, TypeSymbol type, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirConstantInstruction(id, type, value, span));
            return id;
        }

        private MirValueId EmitLoadPlace(StoragePlace place, TypeSymbol type, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirLoadPlaceInstruction(id, type, place, span));
            return id;
        }

        private MirValueId EmitOp(
            string opcode,
            TypeSymbol resultType,
            IReadOnlyList<MirValueId> operands,
            bool hasSideEffects,
            TextSpan span)
        {
            MirValueId result = NextValue();
            _currentBlock.Instructions.Add(new MirOpInstruction(opcode, result, resultType, operands, hasSideEffects, span));
            return result;
        }

        private void EmitOp(string opcode, IReadOnlyList<MirValueId> operands, bool hasSideEffects, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirOpInstruction(opcode, result: null, resultType: null, operands, hasSideEffects, span));
        }

        private void EmitStore(string target, IReadOnlyList<MirValueId> operands, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirStoreInstruction(target, operands, span));
        }

        private MirValueId EmitBitfieldExtract(MirValueId receiver, AggregateMemberSymbol member, TextSpan span)
        {
            string opcode = $"bitfield.extract.{member.BitOffset}.{member.BitWidth}";
            return EmitOp(opcode, member.Type, [receiver], hasSideEffects: false, span);
        }

        private MirValueId EmitBitfieldInsert(MirValueId receiver, MirValueId value, TypeSymbol aggregateType, AggregateMemberSymbol member, TextSpan span)
        {
            string opcode = $"bitfield.insert.{member.BitOffset}.{member.BitWidth}";
            return EmitOp(opcode, aggregateType, [receiver, value], hasSideEffects: false, span);
        }

        private void EmitStorePlace(StoragePlace place, MirValueId value, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirStorePlaceInstruction(place, value, span));
        }

        private void EmitUpdatePlace(StoragePlace place, BoundBinaryOperatorKind operatorKind, MirValueId value, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirUpdatePlaceInstruction(place, operatorKind, value, span));
        }

        private sealed class BlockBuilder
        {
            public BlockBuilder(string label)
            {
                Label = label;
            }

            public string Label { get; }
            public List<MirBlockParameter> Parameters { get; } = [];
            public List<MirInstruction> Instructions { get; } = [];
            public MirTerminator? Terminator { get; set; }
        }

        private readonly record struct LoopContext(string BreakLabel, string ContinueLabel, IReadOnlyList<Symbol> Symbols);
    }

    private static bool TryEvaluateStaticValue(BoundExpression expression, out object? value)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                value = literal.Value;
                return true;

            case BoundConversionExpression conversion:
                return TryEvaluateConvertedValue(conversion.Expression, conversion.Type, out value);

            case BoundCastExpression cast:
                return TryEvaluateConvertedValue(cast.Expression, cast.Type, out value);

            case BoundBitcastExpression bitcast:
                return TryEvaluateConvertedValue(bitcast.Expression, bitcast.Type, out value);

            case BoundUnaryExpression unary when TryEvaluateStaticValue(unary.Operand, out object? unaryValue):
                value = unary.Operator.Kind switch
                {
                    BoundUnaryOperatorKind.Negation when unaryValue is IConvertible
                        => -Convert.ToInt64(unaryValue, CultureInfo.InvariantCulture),
                    BoundUnaryOperatorKind.LogicalNot when unaryValue is bool boolean => !boolean,
                    BoundUnaryOperatorKind.BitwiseNot when unaryValue is IConvertible
                        => ~Convert.ToInt64(unaryValue, CultureInfo.InvariantCulture),
                    BoundUnaryOperatorKind.UnaryPlus when unaryValue is IConvertible
                        => Convert.ToInt64(unaryValue, CultureInfo.InvariantCulture),
                    _ => null,
                };
                return value is not null;

            case BoundBinaryExpression binary
                when TryEvaluateStaticValue(binary.Left, out object? leftValue)
                && TryEvaluateStaticValue(binary.Right, out object? rightValue):
                value = EvaluateBinary(binary.Operator.Kind, leftValue, rightValue);
                return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryEvaluateConvertedValue(BoundExpression expression, TypeSymbol targetType, out object? value)
    {
        if (!TryEvaluateStaticValue(expression, out object? operandValue))
        {
            value = null;
            return false;
        }

        return TypeFacts.TryNormalizeValue(operandValue, targetType, out value);
    }

    private static object? EvaluateBinary(BoundBinaryOperatorKind kind, object? leftValue, object? rightValue)
    {
        if (leftValue is bool leftBool && rightValue is bool rightBool)
        {
            return kind switch
            {
                BoundBinaryOperatorKind.LogicalAnd => leftBool && rightBool,
                BoundBinaryOperatorKind.LogicalOr => leftBool || rightBool,
                BoundBinaryOperatorKind.Equals => leftBool == rightBool,
                BoundBinaryOperatorKind.NotEquals => leftBool != rightBool,
                _ => null,
            };
        }

        if (leftValue is not IConvertible || rightValue is not IConvertible)
            return null;

        long left = Convert.ToInt64(leftValue, CultureInfo.InvariantCulture);
        long right = Convert.ToInt64(rightValue, CultureInfo.InvariantCulture);
        return kind switch
        {
            BoundBinaryOperatorKind.Add => left + right,
            BoundBinaryOperatorKind.Subtract => left - right,
            BoundBinaryOperatorKind.Multiply => left * right,
            BoundBinaryOperatorKind.Divide => right == 0 ? null : left / right,
            BoundBinaryOperatorKind.Modulo => right == 0 ? null : left % right,
            BoundBinaryOperatorKind.BitwiseAnd => left & right,
            BoundBinaryOperatorKind.BitwiseOr => left | right,
            BoundBinaryOperatorKind.BitwiseXor => left ^ right,
            BoundBinaryOperatorKind.ShiftLeft => left << (int)right,
            BoundBinaryOperatorKind.ShiftRight => left >> (int)right,
            BoundBinaryOperatorKind.ArithmeticShiftLeft => left << (int)right,
            BoundBinaryOperatorKind.ArithmeticShiftRight => left >> (int)right,
            BoundBinaryOperatorKind.RotateLeft => RotateLeft(left, right),
            BoundBinaryOperatorKind.RotateRight => RotateRight(left, right),
            BoundBinaryOperatorKind.Equals => left == right,
            BoundBinaryOperatorKind.NotEquals => left != right,
            BoundBinaryOperatorKind.Less => left < right,
            BoundBinaryOperatorKind.LessOrEqual => left <= right,
            BoundBinaryOperatorKind.Greater => left > right,
            BoundBinaryOperatorKind.GreaterOrEqual => left >= right,
            _ => null,
        };
    }

    private static long RotateLeft(long value, long shift)
    {
        int amount = (int)(shift & 31);
        uint bits = unchecked((uint)value);
        return unchecked((int)((bits << amount) | (bits >> ((32 - amount) & 31))));
    }

    private static long RotateRight(long value, long shift)
    {
        int amount = (int)(shift & 31);
        uint bits = unchecked((uint)value);
        return unchecked((int)((bits >> amount) | (bits << ((32 - amount) & 31))));
    }
}
