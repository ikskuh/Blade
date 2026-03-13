using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;

namespace Blade.IR.Mir;

public static class MirLowerer
{
    public static MirModule Lower(BoundProgram program)
    {
        List<MirFunction> functions = new();
        functions.Add(LowerTopLevel(program));

        foreach (BoundFunctionMember functionMember in program.Functions)
        {
            MirFunction function = LowerFunction(functionMember);
            functions.Add(function);
        }

        return new MirModule(functions);
    }

    private static MirFunction LowerTopLevel(BoundProgram program)
    {
        FunctionLoweringContext context = new("$top", isEntryPoint: true, FunctionKind.Default, []);
        context.LowerTopLevel(program.GlobalVariables, program.TopLevelStatements);
        return context.Build();
    }

    private static MirFunction LowerFunction(BoundFunctionMember functionMember)
    {
        FunctionSymbol symbol = functionMember.Symbol;
        FunctionLoweringContext context = new(
            symbol.Name,
            isEntryPoint: false,
            symbol.Kind,
            symbol.ReturnTypes);
        context.LowerFunctionBody(functionMember.Body, symbol.Parameters);
        return context.Build();
    }

    private sealed class FunctionLoweringContext
    {
        private readonly string _name;
        private readonly bool _isEntryPoint;
        private readonly FunctionKind _kind;
        private readonly IReadOnlyList<TypeSymbol> _returnTypes;
        private readonly List<BlockBuilder> _blocks = [];
        private readonly Stack<LoopContext> _loopStack = [];
        private readonly BlockBuilder _entryBlock;
        private readonly BlockBuilder _exitBlock;
        private BlockBuilder _currentBlock;
        private int _nextBlockId;
        private int _nextValueId;

        public FunctionLoweringContext(string name, bool isEntryPoint, FunctionKind kind, IReadOnlyList<TypeSymbol> returnTypes)
        {
            _name = name;
            _isEntryPoint = isEntryPoint;
            _kind = kind;
            _returnTypes = returnTypes;
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
                if (global.Initializer is null)
                    continue;

                MirValueId initializerValue = LowerExpression(global.Initializer);
                EmitStore($"global:{global.Symbol.Name}", [initializerValue], global.Span);
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
                _entryBlock.Instructions.Add(new MirStoreInstruction(parameter.Name, [parameterValue], body.Span));
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
                        EmitStore(variableDeclaration.Symbol.Name, [initializer], statement.Span);
                    }

                    break;

                case BoundAssignmentStatement assignment:
                    LowerAssignmentStatement(assignment);
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
                    EmitOp("noirq.begin", [], hasSideEffects: true, noirqStatement.Span);
                    LowerStatement(noirqStatement.Body);
                    EnsureWritableBlock();
                    EmitOp("noirq.end", [], hasSideEffects: true, noirqStatement.Span);
                    break;

                case BoundReturnStatement returnStatement:
                    LowerReturnStatement(returnStatement);
                    break;

                case BoundBreakStatement:
                    if (_loopStack.Count > 0)
                    {
                        LoopContext loop = _loopStack.Peek();
                        _currentBlock.Terminator = new MirGotoTerminator(loop.BreakLabel, [], statement.Span);
                    }
                    else
                    {
                        _currentBlock.Terminator = new MirUnreachableTerminator(statement.Span);
                    }

                    break;

                case BoundContinueStatement:
                    if (_loopStack.Count > 0)
                    {
                        LoopContext loop = _loopStack.Peek();
                        _currentBlock.Terminator = new MirGotoTerminator(loop.ContinueLabel, [], statement.Span);
                    }
                    else
                    {
                        _currentBlock.Terminator = new MirUnreachableTerminator(statement.Span);
                    }

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
                {
                    string opcode = asmStatement.FlagOutput is null
                        ? "asm"
                        : $"asm:{asmStatement.FlagOutput}";
                    EmitOp(opcode, [], hasSideEffects: true, asmStatement.Span);
                    break;
                }

                case BoundErrorStatement errorStatement:
                    EmitOp("error.statement", [], hasSideEffects: true, errorStatement.Span);
                    break;
            }
        }

        private void LowerAssignmentStatement(BoundAssignmentStatement assignment)
        {
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
            MirValueId condition = LowerExpression(ifStatement.Condition);
            BlockBuilder thenBlock = CreateBlock();
            BlockBuilder elseBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                thenBlock.Label,
                elseBlock.Label,
                [],
                [],
                ifStatement.Span);

            _currentBlock = thenBlock;
            LowerStatement(ifStatement.ThenBody);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, [], ifStatement.ThenBody.Span);

            _currentBlock = elseBlock;
            if (ifStatement.ElseBody is not null)
                LowerStatement(ifStatement.ElseBody);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(mergeBlock.Label, [], ifStatement.Span);

            _currentBlock = mergeBlock;
        }

        private void LowerWhileStatement(BoundWhileStatement whileStatement)
        {
            BlockBuilder conditionBlock = CreateBlock();
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();

            _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, [], whileStatement.Span);

            _currentBlock = conditionBlock;
            MirValueId condition = LowerExpression(whileStatement.Condition);
            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                bodyBlock.Label,
                exitBlock.Label,
                [],
                [],
                whileStatement.Condition.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, conditionBlock.Label));
            _currentBlock = bodyBlock;
            LowerStatement(whileStatement.Body);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, [], whileStatement.Body.Span);
            _loopStack.Pop();

            _currentBlock = exitBlock;
        }

        private void LowerForStatement(BoundForStatement forStatement)
        {
            BlockBuilder conditionBlock = CreateBlock();
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, [], forStatement.Span);

            _currentBlock = conditionBlock;
            MirValueId condition = forStatement.Variable is null
                ? EmitConstant(true, BuiltinTypes.Bool, forStatement.Span)
                : EmitLoadSymbol(forStatement.Variable.Name, forStatement.Variable.Type, forStatement.Span);

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                bodyBlock.Label,
                exitBlock.Label,
                [],
                [],
                forStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, conditionBlock.Label));
            _currentBlock = bodyBlock;
            LowerStatement(forStatement.Body);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(conditionBlock.Label, [], forStatement.Body.Span);
            _loopStack.Pop();

            _currentBlock = exitBlock;
        }

        private void LowerLoopStatement(BoundLoopStatement loopStatement)
        {
            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], loopStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label));
            _currentBlock = bodyBlock;
            LowerStatement(loopStatement.Body);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], loopStatement.Body.Span);
            _loopStack.Pop();

            _currentBlock = exitBlock;
        }

        private void LowerRepLoopStatement(BoundRepLoopStatement repLoopStatement)
        {
            MirValueId count = LowerExpression(repLoopStatement.Count);
            EmitOp("rep.setup", [count], hasSideEffects: true, repLoopStatement.Span);

            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], repLoopStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label));
            _currentBlock = bodyBlock;
            EmitOp("rep.iter", [count], hasSideEffects: true, repLoopStatement.Span);
            LowerStatement(repLoopStatement.Body);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], repLoopStatement.Body.Span);
            _loopStack.Pop();

            _currentBlock = exitBlock;
        }

        private void LowerRepForStatement(BoundRepForStatement repForStatement)
        {
            MirValueId start = LowerExpression(repForStatement.Start);
            MirValueId end = LowerExpression(repForStatement.End);
            EmitOp("repfor.setup", [start, end], hasSideEffects: true, repForStatement.Span);

            BlockBuilder bodyBlock = CreateBlock();
            BlockBuilder exitBlock = CreateBlock();
            _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], repForStatement.Span);

            _loopStack.Push(new LoopContext(exitBlock.Label, bodyBlock.Label));
            _currentBlock = bodyBlock;
            EmitOp("repfor.iter", [start, end], hasSideEffects: true, repForStatement.Span);
            LowerStatement(repForStatement.Body);
            if (_currentBlock.Terminator is null)
                _currentBlock.Terminator = new MirGotoTerminator(bodyBlock.Label, [], repForStatement.Body.Span);
            _loopStack.Pop();

            _currentBlock = exitBlock;
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
                    return EmitLoadSymbol(symbolExpression.Symbol.Name, symbolExpression.Type, symbolExpression.Span);

                case BoundUnaryExpression unaryExpression:
                    return LowerUnaryExpression(unaryExpression);

                case BoundBinaryExpression binaryExpression:
                {
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

                case BoundCallExpression callExpression:
                    return LowerCallExpression(callExpression);

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

                case BoundMemberAccessExpression memberAccess:
                {
                    MirValueId receiver = LowerExpression(memberAccess.Receiver);
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
                    return EmitOp("load.index", indexExpression.Type, [indexed, index], hasSideEffects: false, indexExpression.Span);
                }

                case BoundPointerDerefExpression pointerDerefExpression:
                {
                    MirValueId pointer = LowerExpression(pointerDerefExpression.Expression);
                    return EmitOp("load.deref", pointerDerefExpression.Type, [pointer], hasSideEffects: false, pointerDerefExpression.Span);
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

                case BoundErrorExpression errorExpression:
                    return EmitConstant(null, BuiltinTypes.Unknown, errorExpression.Span);
            }

            return EmitConstant(null, BuiltinTypes.Unknown, expression.Span);
        }

        private MirValueId LowerUnaryExpression(BoundUnaryExpression unaryExpression)
        {
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

        private MirValueId LowerIfExpression(BoundIfExpression ifExpression)
        {
            MirValueId condition = LowerExpression(ifExpression.Condition);
            BlockBuilder thenBlock = CreateBlock();
            BlockBuilder elseBlock = CreateBlock();
            BlockBuilder mergeBlock = CreateBlock();
            MirValueId result = NextValue();
            mergeBlock.Parameters.Add(new MirBlockParameter(result, "ifexpr", ifExpression.Type));

            _currentBlock.Terminator = new MirBranchTerminator(
                condition,
                thenBlock.Label,
                elseBlock.Label,
                [],
                [],
                ifExpression.Condition.Span);

            _currentBlock = thenBlock;
            MirValueId thenValue = LowerExpression(ifExpression.ThenExpression);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    mergeBlock.Label,
                    [thenValue],
                    ifExpression.ThenExpression.Span);
            }

            _currentBlock = elseBlock;
            MirValueId elseValue = LowerExpression(ifExpression.ElseExpression);
            if (_currentBlock.Terminator is null)
            {
                _currentBlock.Terminator = new MirGotoTerminator(
                    mergeBlock.Label,
                    [elseValue],
                    ifExpression.ElseExpression.Span);
            }

            _currentBlock = mergeBlock;
            return result;
        }

        private MirValueId LowerAssignmentTargetRead(BoundAssignmentTarget target)
        {
            return target switch
            {
                BoundSymbolAssignmentTarget symbolTarget => EmitLoadSymbol(symbolTarget.Symbol.Name, symbolTarget.Type, target.Span),
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
                    hasSideEffects: false,
                    target.Span),
                BoundPointerDerefAssignmentTarget pointerTarget => EmitOp(
                    "load.deref",
                    pointerTarget.Type,
                    [LowerExpression(pointerTarget.Expression)],
                    hasSideEffects: false,
                    target.Span),
                _ => EmitConstant(null, BuiltinTypes.Unknown, target.Span),
            };
        }

        private void LowerAssignmentTargetWrite(BoundAssignmentTarget target, MirValueId value, TextSpan span)
        {
            switch (target)
            {
                case BoundSymbolAssignmentTarget symbolTarget:
                    EmitStore(symbolTarget.Symbol.Name, [value], span);
                    return;

                case BoundMemberAssignmentTarget memberTarget:
                {
                    MirValueId receiver = LowerExpression(memberTarget.Receiver);
                    EmitStore($"member:{memberTarget.MemberName}", [receiver, value], span);
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

        private static bool TryMapCompoundOperator(TokenKind operatorKind, out BoundBinaryOperatorKind binaryKind)
        {
            binaryKind = operatorKind switch
            {
                TokenKind.PlusEqual => BoundBinaryOperatorKind.Add,
                TokenKind.MinusEqual => BoundBinaryOperatorKind.Subtract,
                TokenKind.AmpersandEqual => BoundBinaryOperatorKind.BitwiseAnd,
                TokenKind.PipeEqual => BoundBinaryOperatorKind.BitwiseOr,
                TokenKind.CaretEqual => BoundBinaryOperatorKind.BitwiseXor,
                TokenKind.LessLessEqual => BoundBinaryOperatorKind.ShiftLeft,
                TokenKind.GreaterGreaterEqual => BoundBinaryOperatorKind.ShiftRight,
                _ => default,
            };

            return operatorKind is TokenKind.PlusEqual
                or TokenKind.MinusEqual
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

        private MirValueId EmitLoadSymbol(string symbolName, TypeSymbol type, TextSpan span)
        {
            MirValueId id = NextValue();
            _currentBlock.Instructions.Add(new MirLoadSymbolInstruction(id, type, symbolName, span));
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

        private void EmitOp(
            string opcode,
            IReadOnlyList<MirValueId> operands,
            bool hasSideEffects,
            TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirOpInstruction(opcode, result: null, resultType: null, operands, hasSideEffects, span));
        }

        private void EmitStore(string target, IReadOnlyList<MirValueId> operands, TextSpan span)
        {
            _currentBlock.Instructions.Add(new MirStoreInstruction(target, operands, span));
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

        private readonly record struct LoopContext(string BreakLabel, string ContinueLabel);
    }
}
