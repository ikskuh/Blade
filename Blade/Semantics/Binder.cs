using System;
using System.Collections.Generic;
using Blade.Diagnostics;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;
using Blade.Syntax.Nodes;

namespace Blade.Semantics;

public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, TypeAliasSymbol> _typeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeSymbol> _resolvedTypeAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typeAliasResolutionStack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Scope _globalScope;
    private Scope _currentScope;
    private FunctionSymbol? _currentFunction;
    private readonly Stack<LoopContext> _loopStack = new();
    private int _anonymousStructIndex;

    private Binder(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _globalScope = new Scope(parent: null);
        _currentScope = _globalScope;
    }

    public static BoundProgram Bind(CompilationUnitSyntax unit, DiagnosticBag diagnostics)
    {
        Binder binder = new(diagnostics);
        return binder.BindCompilationUnit(unit);
    }

    private BoundProgram BindCompilationUnit(CompilationUnitSyntax unit)
    {
        CollectTopLevelTypes(unit);
        CollectTopLevelFunctions(unit);
        ResolveFunctionSignatures();
        DeclareTopLevelVariables(unit);
        ResolveAllTypeAliases();

        List<BoundGlobalVariableMember> boundGlobals = new();
        List<BoundFunctionMember> boundFunctions = new();
        List<BoundStatement> boundTopLevelStatements = new();

        _currentScope = _globalScope;
        foreach (MemberSyntax member in unit.Members)
        {
            switch (member)
            {
                case TypeAliasDeclarationSyntax:
                    break;

                case ImportDeclarationSyntax:
                    break;

                case VariableDeclarationSyntax variable:
                    boundGlobals.Add(BindGlobalVariable(variable));
                    break;

                case FunctionDeclarationSyntax function:
                    boundFunctions.Add(BindFunction(function));
                    break;

                case GlobalStatementSyntax globalStatement:
                    boundTopLevelStatements.Add(BindStatement(globalStatement.Statement, isTopLevel: true));
                    break;
            }
        }

        return new BoundProgram(
            boundTopLevelStatements,
            boundGlobals,
            boundFunctions,
            _resolvedTypeAliases,
            _functions);
    }

    private void CollectTopLevelTypes(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not TypeAliasDeclarationSyntax typeAlias)
                continue;

            TypeAliasSymbol symbol = new(typeAlias.Name.Text, typeAlias);
            if (!_typeAliases.TryAdd(symbol.Name, symbol))
                _diagnostics.ReportSymbolAlreadyDeclared(typeAlias.Name.Span, symbol.Name);
        }
    }

    private void CollectTopLevelFunctions(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not FunctionDeclarationSyntax functionDecl)
                continue;

            FunctionKind kind = GetFunctionKind(functionDecl.FuncKindKeyword?.Kind);
            FunctionSymbol function = new(functionDecl.Name.Text, functionDecl, kind);
            if (!_functions.TryAdd(function.Name, function))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(functionDecl.Name.Span, function.Name);
                continue;
            }

            _globalScope.TryDeclare(function);
        }
    }

    private void ResolveFunctionSignatures()
    {
        foreach ((_, FunctionSymbol function) in _functions)
        {
            List<ParameterSymbol> parameters = new();
            HashSet<string> parameterNames = new(StringComparer.Ordinal);

            foreach (ParameterSyntax param in function.Syntax.Parameters)
            {
                TypeSymbol parameterType = BindType(param.Type);
                if (!parameterNames.Add(param.Name.Text))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(param.Name.Span, param.Name.Text);
                    continue;
                }

                parameters.Add(new ParameterSymbol(param.Name.Text, parameterType));
            }

            List<TypeSymbol> returns = new();
            if (function.Syntax.ReturnSpec is not null)
            {
                foreach (ReturnItemSyntax returnItem in function.Syntax.ReturnSpec)
                {
                    TypeSymbol returnType = BindType(returnItem.Type);
                    returns.Add(returnType);
                }
            }

            if (returns.Count == 1 && returns[0].IsVoid)
                returns.Clear();

            function.Parameters = parameters;
            function.ReturnTypes = returns;
        }
    }

    private void DeclareTopLevelVariables(CompilationUnitSyntax unit)
    {
        foreach (MemberSyntax member in unit.Members)
        {
            if (member is not VariableDeclarationSyntax variableDecl)
                continue;

            TypeSymbol variableType = BindType(variableDecl.Type);
            bool isConst = variableDecl.MutabilityKeyword.Kind == TokenKind.ConstKeyword;
            VariableSymbol symbol = new(variableDecl.Name.Text, variableType, isConst);

            if (!_globalScope.TryDeclare(symbol))
                _diagnostics.ReportSymbolAlreadyDeclared(variableDecl.Name.Span, variableDecl.Name.Text);
        }
    }

    private void ResolveAllTypeAliases()
    {
        foreach ((string aliasName, TypeAliasSymbol aliasSymbol) in _typeAliases)
            _ = ResolveTypeAlias(aliasName, aliasSymbol.Syntax.Name.Span);
    }

    private BoundGlobalVariableMember BindGlobalVariable(VariableDeclarationSyntax variable)
    {
        if (!_currentScope.TryLookup(variable.Name.Text, out Symbol? symbol) || symbol is not VariableSymbol variableSymbol)
            return new BoundGlobalVariableMember(new VariableSymbol(variable.Name.Text, BuiltinTypes.Unknown, isConst: false), initializer: null, variable.Span);

        BoundExpression? initializer = null;
        if (variable.Initializer is not null)
            initializer = BindExpression(variable.Initializer, variableSymbol.Type);

        return new BoundGlobalVariableMember(variableSymbol, initializer, variable.Span);
    }

    private BoundFunctionMember BindFunction(FunctionDeclarationSyntax functionSyntax)
    {
        if (!_functions.TryGetValue(functionSyntax.Name.Text, out FunctionSymbol? function))
        {
            return new BoundFunctionMember(
                new FunctionSymbol(functionSyntax.Name.Text, functionSyntax, FunctionKind.Default),
                new BoundBlockStatement([], functionSyntax.Body.Span),
                functionSyntax.Span);
        }

        Scope previousScope = _currentScope;
        FunctionSymbol? previousFunction = _currentFunction;

        _currentScope = new Scope(_globalScope);
        _currentFunction = function;

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            if (!_currentScope.TryDeclare(parameter))
                _diagnostics.ReportSymbolAlreadyDeclared(functionSyntax.Name.Span, parameter.Name);
        }

        BoundBlockStatement body = BindBlockStatement(functionSyntax.Body, createScope: false, isTopLevel: false);

        _currentFunction = previousFunction;
        _currentScope = previousScope;

        return new BoundFunctionMember(function, body, functionSyntax.Span);
    }

    private BoundBlockStatement BindBlockStatement(BlockStatementSyntax block, bool createScope, bool isTopLevel)
    {
        Scope? previousScope = null;
        if (createScope)
        {
            previousScope = _currentScope;
            _currentScope = new Scope(_currentScope);
        }

        List<BoundStatement> statements = new();
        foreach (StatementSyntax statement in block.Statements)
            statements.Add(BindStatement(statement, isTopLevel));

        if (createScope && previousScope is not null)
            _currentScope = previousScope;

        return new BoundBlockStatement(statements, block.Span);
    }

    private BoundStatement BindStatement(StatementSyntax statement, bool isTopLevel)
    {
        switch (statement)
        {
            case BlockStatementSyntax block:
                return BindBlockStatement(block, createScope: true, isTopLevel: false);

            case VariableDeclarationStatementSyntax variableDeclStatement:
                return BindLocalVariableDeclaration(variableDeclStatement.Declaration);

            case ExpressionStatementSyntax expressionStatement:
                return new BoundExpressionStatement(BindExpression(expressionStatement.Expression), expressionStatement.Span);

            case AssignmentStatementSyntax assignment:
            {
                BoundAssignmentTarget target = BindAssignmentTarget(assignment.Target);
                BoundExpression value = BindExpression(assignment.Value, target.Type);
                return new BoundAssignmentStatement(target, value, assignment.Operator.Kind, assignment.Span);
            }

            case IfStatementSyntax ifStatement:
            {
                BoundExpression condition = BindExpression(ifStatement.Condition, BuiltinTypes.Bool);
                BoundStatement thenBody = ifStatement.ThenBody switch
                {
                    BlockStatementSyntax thenBlock => BindBlockStatement(thenBlock, createScope: true, isTopLevel: false),
                    _ => BindStatement(ifStatement.ThenBody, isTopLevel: false),
                };
                BoundStatement? elseBody = null;
                if (ifStatement.ElseClause is not null)
                {
                    elseBody = ifStatement.ElseClause.Body switch
                    {
                        BlockStatementSyntax elseBlock => BindBlockStatement(elseBlock, createScope: true, isTopLevel: false),
                        _ => BindStatement(ifStatement.ElseClause.Body, isTopLevel: false),
                    };
                }

                return new BoundIfStatement(condition, thenBody, elseBody, ifStatement.Span);
            }

            case WhileStatementSyntax whileStatement:
            {
                BoundExpression condition = BindExpression(whileStatement.Condition, BuiltinTypes.Bool);
                PushLoop(LoopContext.Regular);
                BoundBlockStatement body = BindBlockStatement(whileStatement.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundWhileStatement(condition, body, whileStatement.Span);
            }

            case ForStatementSyntax forStatement:
            {
                VariableSymbol? variable = ResolveVariableSymbol(forStatement.Variable);
                PushLoop(LoopContext.Regular);
                BoundBlockStatement body = BindBlockStatement(forStatement.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundForStatement(variable, body, forStatement.Span);
            }

            case LoopStatementSyntax loopStatement:
            {
                PushLoop(LoopContext.Regular);
                BoundBlockStatement body = BindBlockStatement(loopStatement.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundLoopStatement(body, loopStatement.Span);
            }

            case RepLoopStatementSyntax repLoop:
            {
                BoundExpression count = BindExpression(repLoop.Count);
                if (!count.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(repLoop.Count.Span, "integer", count.Type.Name);

                PushLoop(LoopContext.Rep);
                BoundBlockStatement body = BindBlockStatement(repLoop.Body, createScope: true, isTopLevel: false);
                PopLoop();
                return new BoundRepLoopStatement(count, body, repLoop.Span);
            }

            case RepForStatementSyntax repFor:
            {
                BoundExpression start = BindExpression(repFor.Range.Start);
                BoundExpression end = BindExpression(repFor.Range.End);
                if (!start.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(repFor.Range.Start.Span, "integer", start.Type.Name);
                if (!end.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(repFor.Range.End.Span, "integer", end.Type.Name);

                PushLoop(LoopContext.Rep);
                BoundRepForStatement boundRepFor = BindRepForBody(repFor, start, end);
                PopLoop();
                return boundRepFor;
            }

            case NoirqStatementSyntax noirq:
            {
                BoundBlockStatement body = BindBlockStatement(noirq.Body, createScope: true, isTopLevel: false);
                return new BoundNoirqStatement(body, noirq.Span);
            }

            case ReturnStatementSyntax returnStatement:
                return BindReturnStatement(returnStatement);

            case BreakStatementSyntax breakStatement:
                return BindBreakOrContinueStatement(breakStatement.BreakKeyword, isBreak: true);

            case ContinueStatementSyntax continueStatement:
                return BindBreakOrContinueStatement(continueStatement.ContinueKeyword, isBreak: false);

            case YieldStatementSyntax yieldStatement:
            {
                if (_currentFunction is null
                    || (_currentFunction.Kind is not FunctionKind.Int1
                        and not FunctionKind.Int2
                        and not FunctionKind.Int3))
                {
                    _diagnostics.ReportInvalidYield(yieldStatement.YieldKeyword.Span);
                }

                return new BoundYieldStatement(yieldStatement.Span);
            }

            case YieldtoStatementSyntax yieldtoStatement:
                return BindYieldtoStatement(yieldtoStatement, isTopLevel);

            case AsmBlockStatementSyntax asm:
            {
                string? flagOutput = asm.FlagOutput is null ? null : $"@{asm.FlagOutput.Flag.Text}";
                return new BoundAsmStatement(asm.Body, flagOutput, asm.Span);
            }
        }

        return new BoundErrorStatement(statement.Span);
    }

    private BoundVariableDeclarationStatement BindLocalVariableDeclaration(VariableDeclarationSyntax declaration)
    {
        TypeSymbol variableType = BindType(declaration.Type);
        bool isConst = declaration.MutabilityKeyword.Kind == TokenKind.ConstKeyword;
        VariableSymbol variableSymbol = new(declaration.Name.Text, variableType, isConst);
        if (!_currentScope.TryDeclare(variableSymbol))
        {
            _diagnostics.ReportSymbolAlreadyDeclared(declaration.Name.Span, declaration.Name.Text);
            return new BoundVariableDeclarationStatement(variableSymbol, initializer: null, declaration.Span);
        }

        BoundExpression? initializer = null;
        if (declaration.Initializer is not null)
            initializer = BindExpression(declaration.Initializer, variableType);

        return new BoundVariableDeclarationStatement(variableSymbol, initializer, declaration.Span);
    }

    private BoundRepForStatement BindRepForBody(RepForStatementSyntax repFor, BoundExpression start, BoundExpression end)
    {
        Scope previousScope = _currentScope;
        _currentScope = new Scope(previousScope);

        VariableSymbol variable = new(repFor.Variable.Text, BuiltinTypes.IntegerLiteral, isConst: true);
        _currentScope.TryDeclare(variable);

        BoundBlockStatement body = BindBlockStatement(repFor.Body, createScope: false, isTopLevel: false);

        _currentScope = previousScope;
        return new BoundRepForStatement(variable, start, end, body, repFor.Span);
    }

    private BoundStatement BindReturnStatement(ReturnStatementSyntax returnStatement)
    {
        List<BoundExpression> values = new();

        if (_currentFunction is null)
        {
            _diagnostics.ReportReturnOutsideFunction(returnStatement.ReturnKeyword.Span);
            if (returnStatement.Values is not null)
            {
                foreach (ExpressionSyntax value in returnStatement.Values)
                    values.Add(BindExpression(value));
            }

            return new BoundReturnStatement(values, returnStatement.Span);
        }

        int expectedCount = _currentFunction.ReturnTypes.Count;
        int actualCount = returnStatement.Values?.Count ?? 0;
        if (expectedCount != actualCount)
        {
            _diagnostics.ReportReturnValueCountMismatch(
                returnStatement.ReturnKeyword.Span,
                _currentFunction.Name,
                expectedCount,
                actualCount);
        }

        if (returnStatement.Values is not null)
        {
            int comparedCount = Math.Min(expectedCount, returnStatement.Values.Count);
            for (int i = 0; i < comparedCount; i++)
                values.Add(BindExpression(returnStatement.Values[i], _currentFunction.ReturnTypes[i]));

            for (int i = comparedCount; i < returnStatement.Values.Count; i++)
                values.Add(BindExpression(returnStatement.Values[i]));
        }

        return new BoundReturnStatement(values, returnStatement.Span);
    }

    private BoundStatement BindBreakOrContinueStatement(Token keywordToken, bool isBreak)
    {
        if (_loopStack.Count == 0)
        {
            _diagnostics.ReportInvalidLoopControl(keywordToken.Span, keywordToken.Text);
            return isBreak ? new BoundBreakStatement(keywordToken.Span) : new BoundContinueStatement(keywordToken.Span);
        }

        if (isBreak && _loopStack.Peek() == LoopContext.Rep)
            _diagnostics.ReportInvalidBreakInRep(keywordToken.Span);

        return isBreak ? new BoundBreakStatement(keywordToken.Span) : new BoundContinueStatement(keywordToken.Span);
    }

    private BoundYieldtoStatement BindYieldtoStatement(YieldtoStatementSyntax yieldtoStatement, bool isTopLevel)
    {
        bool allowedContext = isTopLevel || (_currentFunction?.Kind == FunctionKind.Coro);
        if (!allowedContext)
            _diagnostics.ReportInvalidYieldto(yieldtoStatement.YieldtoKeyword.Span);

        FunctionSymbol? target = null;
        if (_functions.TryGetValue(yieldtoStatement.Target.Text, out FunctionSymbol? targetFunction))
        {
            target = targetFunction;
            if (target.Kind != FunctionKind.Coro)
                _diagnostics.ReportInvalidYieldtoTarget(yieldtoStatement.Target.Span, yieldtoStatement.Target.Text);
        }
        else
        {
            _diagnostics.ReportUndefinedName(yieldtoStatement.Target.Span, yieldtoStatement.Target.Text);
        }

        List<BoundExpression> arguments = target is null
            ? BindArgumentsLoose(yieldtoStatement.Arguments)
            : BindCallArguments(target, yieldtoStatement.Arguments, yieldtoStatement.Target.Span);

        return new BoundYieldtoStatement(target, arguments, yieldtoStatement.Span);
    }

    private BoundAssignmentTarget BindAssignmentTarget(ExpressionSyntax target)
    {
        switch (target)
        {
            case NameExpressionSyntax nameExpression:
                return BindNameAssignmentTarget(nameExpression);

            case MemberAccessExpressionSyntax memberAccess:
            {
                BoundExpression receiver = BindExpression(memberAccess.Expression);
                TypeSymbol type = BuiltinTypes.Unknown;
                if (receiver.Type is StructTypeSymbol structType)
                {
                    if (structType.Fields.TryGetValue(memberAccess.Member.Text, out TypeSymbol? fieldType))
                    {
                        type = fieldType;
                    }
                    else
                    {
                        _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);
                    }
                }

                return new BoundMemberAssignmentTarget(receiver, memberAccess.Member.Text, target.Span, type);
            }

            case IndexExpressionSyntax index:
            {
                BoundExpression expression = BindExpression(index.Expression);
                BoundExpression indexExpr = BindExpression(index.Index);
                if (!indexExpr.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(index.Index.Span, "integer", indexExpr.Type.Name);

                TypeSymbol type = expression.Type switch
                {
                    ArrayTypeSymbol array => array.ElementType,
                    PointerTypeSymbol pointer => pointer.PointeeType,
                    _ => BuiltinTypes.Unknown,
                };

                return new BoundIndexAssignmentTarget(expression, indexExpr, target.Span, type);
            }

            case PointerDerefExpressionSyntax pointerDeref:
            {
                BoundExpression expression = BindExpression(pointerDeref.Expression);
                TypeSymbol type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
                return new BoundPointerDerefAssignmentTarget(expression, target.Span, type);
            }

            default:
                _diagnostics.ReportInvalidAssignmentTarget(target.Span);
                return new BoundErrorAssignmentTarget(target.Span);
        }
    }

    private BoundAssignmentTarget BindNameAssignmentTarget(NameExpressionSyntax nameExpression)
    {
        if (!_currentScope.TryLookup(nameExpression.Name.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is FunctionSymbol)
        {
            _diagnostics.ReportInvalidAssignmentTarget(nameExpression.Span);
            return new BoundErrorAssignmentTarget(nameExpression.Span);
        }

        if (symbol is VariableSymbol variable)
        {
            if (variable.IsConst)
                _diagnostics.ReportCannotAssignToConstant(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundSymbolAssignmentTarget(symbol, nameExpression.Span, variable.Type);
        }

        if (symbol is ParameterSymbol parameter)
            return new BoundSymbolAssignmentTarget(symbol, nameExpression.Span, parameter.Type);

        _diagnostics.ReportInvalidAssignmentTarget(nameExpression.Span);
        return new BoundErrorAssignmentTarget(nameExpression.Span);
    }

    private BoundExpression BindExpression(ExpressionSyntax expression, TypeSymbol? expectedType = null)
    {
        BoundExpression bound = BindExpressionCore(expression, expectedType);
        if (expectedType is null)
            return bound;

        return BindConversion(bound, expectedType, expression.Span, reportMismatch: true);
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax expression, TypeSymbol? expectedType)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return BindLiteralExpression(literal);

            case NameExpressionSyntax name:
                return BindNameExpression(name);

            case ParenthesizedExpressionSyntax parenthesized:
                return BindExpression(parenthesized.Expression, expectedType);

            case UnaryExpressionSyntax unary:
                return BindUnaryExpression(unary);

            case BinaryExpressionSyntax binary:
                return BindBinaryExpression(binary);

            case PostfixUnaryExpressionSyntax postfixUnary:
                return BindPostfixUnaryExpression(postfixUnary);

            case MemberAccessExpressionSyntax memberAccess:
                return BindMemberAccessExpression(memberAccess);

            case PointerDerefExpressionSyntax pointerDeref:
                return BindPointerDerefExpression(pointerDeref);

            case IndexExpressionSyntax index:
                return BindIndexExpression(index);

            case CallExpressionSyntax call:
                return BindCallExpression(call);

            case IntrinsicCallExpressionSyntax intrinsic:
                return BindIntrinsicCallExpression(intrinsic);

            case StructLiteralExpressionSyntax structLiteral:
                return BindStructLiteralExpression(structLiteral, expectedType);

            case ComptimeExpressionSyntax comptime:
                return BindComptimeExpression(comptime);

            case IfExpressionSyntax ifExpression:
                return BindIfExpression(ifExpression);

            case RangeExpressionSyntax range:
                return BindRangeExpression(range);
        }

        return new BoundErrorExpression(expression.Span);
    }

    private BoundExpression BindLiteralExpression(LiteralExpressionSyntax literal)
    {
        TypeSymbol type = literal.Token.Kind switch
        {
            TokenKind.TrueKeyword or TokenKind.FalseKeyword => BuiltinTypes.Bool,
            TokenKind.StringLiteral => BuiltinTypes.String,
            TokenKind.UndefinedKeyword => BuiltinTypes.UndefinedLiteral,
            TokenKind.IntegerLiteral => BuiltinTypes.IntegerLiteral,
            _ => BuiltinTypes.Unknown,
        };

        return new BoundLiteralExpression(literal.Token.Value, literal.Span, type);
    }

    private BoundExpression BindNameExpression(NameExpressionSyntax nameExpression)
    {
        if (!_currentScope.TryLookup(nameExpression.Name.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(nameExpression.Name.Span, nameExpression.Name.Text);
            return new BoundErrorExpression(nameExpression.Span);
        }

        return symbol switch
        {
            VariableSymbol variable => new BoundSymbolExpression(symbol, nameExpression.Span, variable.Type),
            ParameterSymbol parameter => new BoundSymbolExpression(symbol, nameExpression.Span, parameter.Type),
            FunctionSymbol function => new BoundSymbolExpression(symbol, nameExpression.Span, new FunctionTypeSymbol(function)),
            _ => new BoundErrorExpression(nameExpression.Span),
        };
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax unary)
    {
        if (unary.Operator.Kind == TokenKind.Star)
        {
            BoundExpression operand = BindExpression(unary.Operand);
            TypeSymbol type = BindPointerDerefType(unary.Operand.Span, operand.Type);
            return new BoundPointerDerefExpression(operand, unary.Span, type);
        }

        BoundUnaryOperator? op = BoundUnaryOperator.Bind(unary.Operator.Kind);
        if (op is null)
            return new BoundErrorExpression(unary.Span);

        switch (op.Kind)
        {
            case BoundUnaryOperatorKind.LogicalNot:
            {
                BoundExpression operand = BindExpression(unary.Operand, BuiltinTypes.Bool);
                return new BoundUnaryExpression(op, operand, unary.Span, BuiltinTypes.Bool);
            }

            case BoundUnaryOperatorKind.Negation:
            {
                BoundExpression operand = BindExpression(unary.Operand);
                if (!operand.Type.IsInteger)
                    _diagnostics.ReportTypeMismatch(unary.Operand.Span, "integer", operand.Type.Name);
                return new BoundUnaryExpression(op, operand, unary.Span, operand.Type.IsInteger ? operand.Type : BuiltinTypes.Unknown);
            }
        }

        return new BoundErrorExpression(unary.Span);
    }

    private BoundExpression BindPostfixUnaryExpression(PostfixUnaryExpressionSyntax postfixUnary)
    {
        BoundAssignmentTarget target = BindAssignmentTarget(postfixUnary.Operand);
        if (!target.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(postfixUnary.Operand.Span, "integer", target.Type.Name);

        BoundExpression operandExpression = target switch
        {
            BoundSymbolAssignmentTarget symbol => new BoundSymbolExpression(symbol.Symbol, postfixUnary.Operand.Span, symbol.Type),
            BoundMemberAssignmentTarget member => new BoundMemberAccessExpression(member.Receiver, member.MemberName, postfixUnary.Operand.Span, member.Type),
            BoundIndexAssignmentTarget index => new BoundIndexExpression(index.Expression, index.Index, postfixUnary.Operand.Span, index.Type),
            BoundPointerDerefAssignmentTarget deref => new BoundPointerDerefExpression(deref.Expression, postfixUnary.Operand.Span, deref.Type),
            _ => new BoundErrorExpression(postfixUnary.Operand.Span),
        };

        BoundUnaryOperatorKind kind = postfixUnary.Operator.Kind == TokenKind.PlusPlus
            ? BoundUnaryOperatorKind.PostIncrement
            : BoundUnaryOperatorKind.PostDecrement;

        return new BoundUnaryExpression(
            new BoundUnaryOperator(postfixUnary.Operator.Kind, kind),
            operandExpression,
            postfixUnary.Span,
            target.Type);
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax binary)
    {
        BoundBinaryOperator? op = BoundBinaryOperator.Bind(binary.Operator.Kind);
        if (op is null)
            return new BoundErrorExpression(binary.Span);

        BoundExpression left = BindExpression(binary.Left);
        BoundExpression right = BindExpression(binary.Right);

        if (op.IsComparison)
        {
            if (!IsComparable(left.Type, right.Type))
                _diagnostics.ReportTypeMismatch(binary.Span, left.Type.Name, right.Type.Name);

            if (left.Type.IsInteger && right.Type.IsInteger)
            {
                TypeSymbol numericType = BestNumericType(left.Type, right.Type);
                left = BindConversion(left, numericType, left.Span, reportMismatch: false);
                right = BindConversion(right, numericType, right.Span, reportMismatch: false);
            }

            return new BoundBinaryExpression(left, op, right, binary.Span, BuiltinTypes.Bool);
        }

        if (!left.Type.IsInteger || !right.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(binary.Span, "integer", $"{left.Type.Name}, {right.Type.Name}");

        TypeSymbol resultType = BestNumericType(left.Type, right.Type);
        left = BindConversion(left, resultType, left.Span, reportMismatch: false);
        right = BindConversion(right, resultType, right.Span, reportMismatch: false);
        return new BoundBinaryExpression(left, op, right, binary.Span, resultType);
    }

    private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax memberAccess)
    {
        BoundExpression receiver = BindExpression(memberAccess.Expression);
        if (receiver.Type is StructTypeSymbol structType
            && structType.Fields.TryGetValue(memberAccess.Member.Text, out TypeSymbol? fieldType))
        {
            return new BoundMemberAccessExpression(receiver, memberAccess.Member.Text, memberAccess.Span, fieldType);
        }

        if (receiver.Type is StructTypeSymbol)
            _diagnostics.ReportUndefinedName(memberAccess.Member.Span, memberAccess.Member.Text);

        return new BoundMemberAccessExpression(receiver, memberAccess.Member.Text, memberAccess.Span, BuiltinTypes.Unknown);
    }

    private BoundExpression BindPointerDerefExpression(PointerDerefExpressionSyntax pointerDeref)
    {
        BoundExpression expression = BindExpression(pointerDeref.Expression);
        TypeSymbol type = BindPointerDerefType(pointerDeref.Expression.Span, expression.Type);
        return new BoundPointerDerefExpression(expression, pointerDeref.Span, type);
    }

    private TypeSymbol BindPointerDerefType(TextSpan span, TypeSymbol expressionType)
    {
        if (expressionType is PointerTypeSymbol pointerType)
            return pointerType.PointeeType;

        _diagnostics.ReportTypeMismatch(span, "pointer", expressionType.Name);
        return BuiltinTypes.Unknown;
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax indexExpression)
    {
        BoundExpression expression = BindExpression(indexExpression.Expression);
        BoundExpression index = BindExpression(indexExpression.Index);
        if (!index.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(indexExpression.Index.Span, "integer", index.Type.Name);

        TypeSymbol type = expression.Type switch
        {
            ArrayTypeSymbol array => array.ElementType,
            PointerTypeSymbol pointer => pointer.PointeeType,
            _ => BuiltinTypes.Unknown,
        };

        return new BoundIndexExpression(expression, index, indexExpression.Span, type);
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax callExpression)
    {
        BoundExpression callee = BindExpression(callExpression.Callee);
        if (!TryGetFunctionSymbol(callee, out FunctionSymbol? maybeFunction) || maybeFunction is null)
        {
            _diagnostics.ReportNotCallable(callExpression.Callee.Span, callee.Type.Name);
            _ = BindArgumentsLoose(callExpression.Arguments);
            return new BoundErrorExpression(callExpression.Span);
        }

        FunctionSymbol function = maybeFunction;
        List<BoundExpression> arguments = BindCallArguments(function, callExpression.Arguments, callExpression.Callee.Span);
        TypeSymbol returnType = function.ReturnTypes.Count switch
        {
            0 => BuiltinTypes.Void,
            1 => function.ReturnTypes[0],
            _ => BuiltinTypes.Unknown,
        };

        return new BoundCallExpression(function, arguments, callExpression.Span, returnType);
    }

    private BoundExpression BindIntrinsicCallExpression(IntrinsicCallExpressionSyntax intrinsic)
    {
        List<BoundExpression> arguments = new(intrinsic.Arguments.Count);
        foreach (ExpressionSyntax argument in intrinsic.Arguments)
            arguments.Add(BindExpression(argument));

        return new BoundIntrinsicCallExpression(intrinsic.Name.Text, arguments, intrinsic.Span, BuiltinTypes.U32);
    }

    private BoundExpression BindStructLiteralExpression(StructLiteralExpressionSyntax structLiteral, TypeSymbol? expectedType)
    {
        if (expectedType is StructTypeSymbol structType)
        {
            List<BoundStructFieldInitializer> initializers = new(structLiteral.Initializers.Count);
            foreach (FieldInitializerSyntax initializer in structLiteral.Initializers)
            {
                if (!structType.Fields.TryGetValue(initializer.Name.Text, out TypeSymbol? fieldType))
                {
                    _diagnostics.ReportUndefinedName(initializer.Name.Span, initializer.Name.Text);
                    initializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, BindExpression(initializer.Value)));
                    continue;
                }

                BoundExpression value = BindExpression(initializer.Value, fieldType);
                initializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, value));
            }

            return new BoundStructLiteralExpression(initializers, structLiteral.Span, structType);
        }

        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        List<BoundStructFieldInitializer> inferredInitializers = new(structLiteral.Initializers.Count);
        foreach (FieldInitializerSyntax initializer in structLiteral.Initializers)
        {
            BoundExpression value = BindExpression(initializer.Value);
            if (!fields.TryAdd(initializer.Name.Text, value.Type))
                _diagnostics.ReportSymbolAlreadyDeclared(initializer.Name.Span, initializer.Name.Text);
            inferredInitializers.Add(new BoundStructFieldInitializer(initializer.Name.Text, value));
        }

        _anonymousStructIndex++;
        StructTypeSymbol inferredType = new($"<struct#{_anonymousStructIndex}>", fields);
        return new BoundStructLiteralExpression(inferredInitializers, structLiteral.Span, inferredType);
    }

    private BoundExpression BindComptimeExpression(ComptimeExpressionSyntax comptime)
    {
        _ = BindBlockStatement(comptime.Body, createScope: true, isTopLevel: false);
        return new BoundErrorExpression(comptime.Span);
    }

    private BoundExpression BindIfExpression(IfExpressionSyntax ifExpression)
    {
        BoundExpression condition = BindExpression(ifExpression.Condition, BuiltinTypes.Bool);
        BoundExpression thenExpression = BindExpression(ifExpression.ThenExpression);
        BoundExpression elseExpression = BindExpression(ifExpression.ElseExpression);

        if (IsAssignable(thenExpression.Type, elseExpression.Type))
        {
            elseExpression = BindConversion(elseExpression, thenExpression.Type, elseExpression.Span, reportMismatch: false);
            return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, thenExpression.Type);
        }

        if (IsAssignable(elseExpression.Type, thenExpression.Type))
        {
            thenExpression = BindConversion(thenExpression, elseExpression.Type, thenExpression.Span, reportMismatch: false);
            return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, elseExpression.Type);
        }

        _diagnostics.ReportTypeMismatch(ifExpression.Span, thenExpression.Type.Name, elseExpression.Type.Name);
        return new BoundIfExpression(condition, thenExpression, elseExpression, ifExpression.Span, BuiltinTypes.Unknown);
    }

    private BoundExpression BindRangeExpression(RangeExpressionSyntax rangeExpression)
    {
        BoundExpression start = BindExpression(rangeExpression.Start);
        BoundExpression end = BindExpression(rangeExpression.End);

        if (!start.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(rangeExpression.Start.Span, "integer", start.Type.Name);
        if (!end.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(rangeExpression.End.Span, "integer", end.Type.Name);

        return new BoundRangeExpression(start, end, rangeExpression.Span);
    }

    private List<BoundExpression> BindCallArguments(
        FunctionSymbol function,
        SeparatedSyntaxList<ExpressionSyntax> arguments,
        TextSpan callSiteSpan)
    {
        if (arguments.Count != function.Parameters.Count)
        {
            _diagnostics.ReportArgumentCountMismatch(callSiteSpan, function.Name, function.Parameters.Count, arguments.Count);
        }

        List<BoundExpression> boundArguments = new(arguments.Count);
        int compared = Math.Min(arguments.Count, function.Parameters.Count);
        for (int i = 0; i < compared; i++)
            boundArguments.Add(BindExpression(arguments[i], function.Parameters[i].Type));

        for (int i = compared; i < arguments.Count; i++)
            boundArguments.Add(BindExpression(arguments[i]));

        return boundArguments;
    }

    private List<BoundExpression> BindArgumentsLoose(SeparatedSyntaxList<ExpressionSyntax> arguments)
    {
        List<BoundExpression> bound = new(arguments.Count);
        foreach (ExpressionSyntax argument in arguments)
            bound.Add(BindExpression(argument));
        return bound;
    }

    private static bool TryGetFunctionSymbol(BoundExpression expression, out FunctionSymbol? function)
    {
        if (expression is BoundSymbolExpression symbolExpression && symbolExpression.Symbol is FunctionSymbol directFunction)
        {
            function = directFunction;
            return true;
        }

        if (expression.Type is FunctionTypeSymbol functionType)
        {
            function = functionType.Function;
            return true;
        }

        function = null;
        return false;
    }

    private BoundExpression BindConversion(BoundExpression expression, TypeSymbol targetType, TextSpan span, bool reportMismatch)
    {
        if (targetType.IsUnknown || expression.Type.IsUnknown)
            return expression;

        if (targetType.Name == expression.Type.Name)
            return expression;

        if (!IsAssignable(targetType, expression.Type))
        {
            if (reportMismatch)
                _diagnostics.ReportTypeMismatch(span, targetType.Name, expression.Type.Name);
            return new BoundErrorExpression(span);
        }

        return new BoundConversionExpression(expression, span, targetType);
    }

    private VariableSymbol? ResolveVariableSymbol(Token token)
    {
        if (!_currentScope.TryLookup(token.Text, out Symbol? symbol) || symbol is null)
        {
            _diagnostics.ReportUndefinedName(token.Span, token.Text);
            return null;
        }

        return symbol switch
        {
            VariableSymbol variable => variable,
            ParameterSymbol parameter => new VariableSymbol(parameter.Name, parameter.Type, isConst: false),
            _ => null,
        };
    }

    private TypeSymbol BindType(TypeSyntax syntax, string? aliasName = null)
    {
        return syntax switch
        {
            PrimitiveTypeSyntax primitive => BindPrimitiveType(primitive.Keyword),
            GenericWidthTypeSyntax generic => BindGenericWidthType(generic),
            ArrayTypeSyntax array => BindArrayType(array),
            PointerTypeSyntax pointer => new PointerTypeSymbol(BindType(pointer.PointeeType), pointer.ConstKeyword is not null),
            StructTypeSyntax structType => BindStructType(structType, aliasName),
            NamedTypeSyntax named => BindNamedType(named),
            _ => BuiltinTypes.Unknown,
        };
    }

    private TypeSymbol BindPrimitiveType(Token keywordToken)
    {
        if (BuiltinTypes.TryGet(keywordToken.Text, out TypeSymbol type))
            return type;

        _diagnostics.ReportUndefinedType(keywordToken.Span, keywordToken.Text);
        return BuiltinTypes.Unknown;
    }

    private TypeSymbol BindGenericWidthType(GenericWidthTypeSyntax genericType)
    {
        BoundExpression width = BindExpression(genericType.Width);
        if (!width.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(genericType.Width.Span, "integer", width.Type.Name);

        return genericType.Keyword.Kind == TokenKind.UintKeyword ? BuiltinTypes.Uint : BuiltinTypes.Int;
    }

    private TypeSymbol BindArrayType(ArrayTypeSyntax arrayType)
    {
        BoundExpression size = BindExpression(arrayType.Size);
        if (!size.Type.IsInteger)
            _diagnostics.ReportTypeMismatch(arrayType.Size.Span, "integer", size.Type.Name);

        TypeSymbol elementType = BindType(arrayType.ElementType);
        return new ArrayTypeSymbol(elementType);
    }

    private TypeSymbol BindStructType(StructTypeSyntax structType, string? aliasName)
    {
        Dictionary<string, TypeSymbol> fields = new(StringComparer.Ordinal);
        foreach (StructFieldSyntax field in structType.Fields)
        {
            TypeSymbol fieldType = BindType(field.Type);
            if (!fields.TryAdd(field.Name.Text, fieldType))
                _diagnostics.ReportSymbolAlreadyDeclared(field.Name.Span, field.Name.Text);
        }

        string name = aliasName ?? $"<anon-struct#{++_anonymousStructIndex}>";
        return new StructTypeSymbol(name, fields);
    }

    private TypeSymbol BindNamedType(NamedTypeSyntax namedType)
    {
        if (BuiltinTypes.TryGet(namedType.Name.Text, out TypeSymbol builtin))
            return builtin;

        return ResolveTypeAlias(namedType.Name.Text, namedType.Name.Span);
    }

    private TypeSymbol ResolveTypeAlias(string aliasName, TextSpan span)
    {
        if (_resolvedTypeAliases.TryGetValue(aliasName, out TypeSymbol? resolved))
            return resolved;

        if (!_typeAliases.TryGetValue(aliasName, out TypeAliasSymbol? alias))
        {
            _diagnostics.ReportUndefinedType(span, aliasName);
            return BuiltinTypes.Unknown;
        }

        if (!_typeAliasResolutionStack.Add(aliasName))
        {
            _diagnostics.ReportUndefinedType(span, aliasName);
            return BuiltinTypes.Unknown;
        }

        TypeSymbol boundType = BindType(alias.Syntax.Type, alias.Syntax.Name.Text);
        _typeAliasResolutionStack.Remove(aliasName);
        _resolvedTypeAliases[aliasName] = boundType;
        return boundType;
    }

    private static TypeSymbol BestNumericType(TypeSymbol left, TypeSymbol right)
    {
        if (left.IsUnknown || right.IsUnknown)
            return BuiltinTypes.Unknown;
        if (left == BuiltinTypes.IntegerLiteral && right == BuiltinTypes.IntegerLiteral)
            return BuiltinTypes.IntegerLiteral;
        if (left == BuiltinTypes.IntegerLiteral)
            return right;
        return left;
    }

    private static bool IsComparable(TypeSymbol left, TypeSymbol right)
    {
        if (left.IsUnknown || right.IsUnknown)
            return true;
        if (left.IsUndefinedLiteral || right.IsUndefinedLiteral)
            return true;
        if (left.IsInteger && right.IsInteger)
            return true;
        if (left.IsBool && right.IsBool)
            return true;
        return left.Name == right.Name;
    }

    private static bool IsAssignable(TypeSymbol target, TypeSymbol source)
    {
        if (target.IsUnknown || source.IsUnknown)
            return true;
        if (source.IsUndefinedLiteral)
            return true;
        if (target.IsInteger && source.IsInteger)
            return true;
        if (target.IsBool && source.IsBool)
            return true;
        if (target is PointerTypeSymbol && source.IsUndefinedLiteral)
            return true;

        if (target is StructTypeSymbol targetStruct && source is StructTypeSymbol sourceStruct)
        {
            if (ReferenceEquals(targetStruct, sourceStruct))
                return true;

            if (targetStruct.Fields.Count != sourceStruct.Fields.Count)
                return false;

            foreach ((string fieldName, TypeSymbol fieldType) in targetStruct.Fields)
            {
                if (!sourceStruct.Fields.TryGetValue(fieldName, out TypeSymbol? sourceFieldType))
                    return false;
                if (!IsAssignable(fieldType, sourceFieldType))
                    return false;
            }

            return true;
        }

        return target.Name == source.Name;
    }

    private void PushLoop(LoopContext kind) => _loopStack.Push(kind);

    private void PopLoop()
    {
        if (_loopStack.Count > 0)
            _loopStack.Pop();
    }

    private static FunctionKind GetFunctionKind(TokenKind? kind) => kind switch
    {
        TokenKind.LeafKeyword => FunctionKind.Leaf,
        TokenKind.InlineKeyword => FunctionKind.Inline,
        TokenKind.RecKeyword => FunctionKind.Rec,
        TokenKind.CoroKeyword => FunctionKind.Coro,
        TokenKind.ComptimeKeyword => FunctionKind.Comptime,
        TokenKind.Int1Keyword => FunctionKind.Int1,
        TokenKind.Int2Keyword => FunctionKind.Int2,
        TokenKind.Int3Keyword => FunctionKind.Int3,
        _ => FunctionKind.Default,
    };

    private enum LoopContext
    {
        Regular,
        Rep,
    }
}
