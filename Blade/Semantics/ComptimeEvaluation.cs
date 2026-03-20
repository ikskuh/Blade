using System;
using System.Collections.Generic;
using System.Globalization;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;

namespace Blade.Semantics;

internal enum ComptimeFailureKind
{
    None,
    NotEvaluable,
    UnsupportedConstruct,
    ForbiddenSymbolAccess,
    FuelExhausted,
}

internal readonly record struct ComptimeFailure(ComptimeFailureKind Kind, TextSpan Span, string Detail)
{
    public static ComptimeFailure None => new(ComptimeFailureKind.None, new TextSpan(0, 0), string.Empty);
}

internal readonly record struct ComptimeSupportResult(bool IsSupported, ComptimeFailure Failure);

internal static class ComptimeTypeFacts
{
    public static bool InvolvesPointers(TypeSymbol type)
    {
        return InvolvesPointers(type, new HashSet<TypeSymbol>());
    }

    private static bool InvolvesPointers(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (!visited.Add(type))
            return false;

        if (type is PointerLikeTypeSymbol)
            return true;

        return type switch
        {
            ArrayTypeSymbol array => InvolvesPointers(array.ElementType, visited),
            StructTypeSymbol structType => InvolvesPointers(structType.Fields, visited),
            UnionTypeSymbol unionType => InvolvesPointers(unionType.Fields, visited),
            BitfieldTypeSymbol bitfieldType => InvolvesPointers(bitfieldType.BackingType, visited),
            EnumTypeSymbol enumType => InvolvesPointers(enumType.BackingType, visited),
            _ => false,
        };
    }

    private static bool InvolvesPointers(IReadOnlyDictionary<string, TypeSymbol> fields, HashSet<TypeSymbol> visited)
    {
        foreach ((_, TypeSymbol fieldType) in fields)
        {
            if (InvolvesPointers(fieldType, visited))
                return true;
        }

        return false;
    }

    public static bool TryNormalizeValue(object? value, TypeSymbol targetType, out object? normalized)
    {
        if (ReferenceEquals(targetType, BuiltinTypes.Bool))
        {
            if (value is bool boolValue)
            {
                normalized = boolValue;
                return true;
            }

            if (value is IConvertible convertible)
            {
                normalized = Convert.ToInt64(convertible, CultureInfo.InvariantCulture) != 0;
                return true;
            }

            normalized = null;
            return false;
        }

        if (targetType is EnumTypeSymbol enumType)
        {
            if (!TryNormalizeValue(value, enumType.BackingType, out object? enumValue))
            {
                normalized = null;
                return false;
            }

            normalized = enumValue;
            return true;
        }

        if (value is bool)
        {
            normalized = null;
            return false;
        }

        return TypeFacts.TryNormalizeValue(value, targetType, out normalized);
    }
}

internal sealed class ComptimeFunctionSupportAnalyzer
{
    public ComptimeSupportResult Analyze(FunctionSymbol function, BoundBlockStatement body)
    {
        Requires.NotNull(function);
        Requires.NotNull(body);

        if (function.ReturnTypes.Count > 1)
            return Unsupported(body.Span, $"function '{function.Name}' returns multiple values.");

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            if (ComptimeTypeFacts.InvolvesPointers(parameter.Type))
                return Unsupported(body.Span, $"function '{function.Name}' uses pointer-typed parameters.");
        }

        foreach (TypeSymbol returnType in function.ReturnTypes)
        {
            if (ComptimeTypeFacts.InvolvesPointers(returnType))
                return Unsupported(body.Span, $"function '{function.Name}' returns a pointer-typed value.");
        }

        return AnalyzeStatement(body);
    }

    private ComptimeSupportResult AnalyzeStatement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement nested in block.Statements)
                {
                    ComptimeSupportResult nestedResult = AnalyzeStatement(nested);
                    if (!nestedResult.IsSupported)
                        return nestedResult;
                }

                return Supported();

            case BoundVariableDeclarationStatement declaration:
                if (ComptimeTypeFacts.InvolvesPointers(declaration.Symbol.Type))
                    return Unsupported(declaration.Span, $"local '{declaration.Symbol.Name}' has a pointer-involving type.");

                return declaration.Initializer is null
                    ? Supported()
                    : AnalyzeExpression(declaration.Initializer);

            case BoundAssignmentStatement assignment:
            {
                ComptimeSupportResult targetResult = AnalyzeAssignmentTarget(assignment.Target);
                if (!targetResult.IsSupported)
                    return targetResult;

                if (assignment.OperatorKind is not TokenKind.Equal
                    and not TokenKind.PlusEqual
                    and not TokenKind.MinusEqual
                    and not TokenKind.PercentEqual
                    and not TokenKind.AmpersandEqual
                    and not TokenKind.PipeEqual
                    and not TokenKind.CaretEqual
                    and not TokenKind.LessLessEqual
                    and not TokenKind.GreaterGreaterEqual)
                {
                    return Unsupported(assignment.Span, $"assignment operator '{assignment.OperatorKind}' is not supported.");
                }

                return AnalyzeExpression(assignment.Value);
            }

            case BoundExpressionStatement expressionStatement:
                return AnalyzeExpression(expressionStatement.Expression);

            case BoundIfStatement ifStatement:
            {
                ComptimeSupportResult conditionResult = AnalyzeExpression(ifStatement.Condition);
                if (!conditionResult.IsSupported)
                    return conditionResult;

                ComptimeSupportResult thenResult = AnalyzeStatement(ifStatement.ThenBody);
                if (!thenResult.IsSupported)
                    return thenResult;

                return ifStatement.ElseBody is null
                    ? Supported()
                    : AnalyzeStatement(ifStatement.ElseBody);
            }

            case BoundWhileStatement whileStatement:
            {
                ComptimeSupportResult conditionResult = AnalyzeExpression(whileStatement.Condition);
                if (!conditionResult.IsSupported)
                    return conditionResult;

                return AnalyzeStatement(whileStatement.Body);
            }

            case BoundForStatement forStatement:
            {
                ComptimeSupportResult iterableResult = AnalyzeExpression(forStatement.Iterable);
                if (!iterableResult.IsSupported)
                    return iterableResult;

                if (forStatement.Iterable.Type is ArrayTypeSymbol)
                    return Unsupported(forStatement.Iterable.Span, "array iteration is not supported during comptime evaluation.");

                if (forStatement.ItemVariable is not null && ComptimeTypeFacts.InvolvesPointers(forStatement.ItemVariable.Type))
                    return Unsupported(forStatement.Span, $"loop item '{forStatement.ItemVariable.Name}' has a pointer-involving type.");

                return AnalyzeStatement(forStatement.Body);
            }

            case BoundLoopStatement loopStatement:
                return AnalyzeStatement(loopStatement.Body);

            case BoundRepLoopStatement repLoopStatement:
            {
                ComptimeSupportResult countResult = AnalyzeExpression(repLoopStatement.Count);
                if (!countResult.IsSupported)
                    return countResult;

                return AnalyzeStatement(repLoopStatement.Body);
            }

            case BoundRepForStatement repForStatement:
            {
                if (ComptimeTypeFacts.InvolvesPointers(repForStatement.Variable.Type))
                    return Unsupported(repForStatement.Span, $"loop variable '{repForStatement.Variable.Name}' has a pointer-involving type.");

                ComptimeSupportResult startResult = AnalyzeExpression(repForStatement.Start);
                if (!startResult.IsSupported)
                    return startResult;

                ComptimeSupportResult endResult = AnalyzeExpression(repForStatement.End);
                if (!endResult.IsSupported)
                    return endResult;

                return AnalyzeStatement(repForStatement.Body);
            }

            case BoundNoirqStatement noirqStatement:
                return AnalyzeStatement(noirqStatement.Body);

            case BoundReturnStatement returnStatement:
                foreach (BoundExpression value in returnStatement.Values)
                {
                    ComptimeSupportResult valueResult = AnalyzeExpression(value);
                    if (!valueResult.IsSupported)
                        return valueResult;
                }

                return Supported();

            case BoundBreakStatement:
            case BoundContinueStatement:
                return Supported();

            case BoundYieldStatement:
                return Unsupported(statement.Span, "'yield' is not supported during comptime evaluation.");

            case BoundYieldtoStatement:
                return Unsupported(statement.Span, "'yieldto' is not supported during comptime evaluation.");

            case BoundAsmStatement:
                return Unsupported(statement.Span, "inline asm is not supported during comptime evaluation.");

            case BoundErrorStatement:
                return Unsupported(statement.Span, "error statements are not evaluable.");

            default:
                return Unsupported(statement.Span, $"statement '{statement.Kind}' is not supported during comptime evaluation.");
        }
    }

    private ComptimeSupportResult AnalyzeAssignmentTarget(BoundAssignmentTarget target)
    {
        return target switch
        {
            BoundSymbolAssignmentTarget symbolTarget => AnalyzeAssignmentSymbol(symbolTarget.Symbol, target.Span),
            BoundMemberAssignmentTarget => Unsupported(target.Span, "member assignment is not supported during comptime evaluation."),
            BoundBitfieldAssignmentTarget => Unsupported(target.Span, "bitfield assignment is not supported during comptime evaluation."),
            BoundIndexAssignmentTarget => Unsupported(target.Span, "index assignment is not supported during comptime evaluation."),
            BoundPointerDerefAssignmentTarget => Unsupported(target.Span, "pointer writes are not supported during comptime evaluation."),
            BoundErrorAssignmentTarget => Unsupported(target.Span, "invalid assignment targets are not evaluable."),
            _ => Unsupported(target.Span, $"assignment target '{target.Kind}' is not supported during comptime evaluation."),
        };
    }

    private ComptimeSupportResult AnalyzeAssignmentSymbol(Symbol symbol, TextSpan span)
    {
        return symbol switch
        {
            VariableSymbol variable when variable.ScopeKind == VariableScopeKind.Local => Supported(),
            ParameterSymbol => Unsupported(span, "assigning to parameters is not supported during comptime evaluation."),
            VariableSymbol variable => Forbidden(span, variable.Name),
            _ => Unsupported(span, $"assignment target '{symbol.Name}' is not supported during comptime evaluation."),
        };
    }

    private ComptimeSupportResult AnalyzeExpression(BoundExpression expression)
    {
        if (ComptimeTypeFacts.InvolvesPointers(expression.Type))
            return Unsupported(expression.Span, $"expression '{expression.Kind}' involves pointer data.");

        switch (expression)
        {
            case BoundLiteralExpression:
            case BoundEnumLiteralExpression:
                return Supported();

            case BoundSymbolExpression symbolExpression:
                return AnalyzeSymbolExpression(symbolExpression);

            case BoundUnaryExpression unary:
            {
                if (unary.Operator.Kind is BoundUnaryOperatorKind.AddressOf or BoundUnaryOperatorKind.PostIncrement or BoundUnaryOperatorKind.PostDecrement)
                    return Unsupported(unary.Span, $"operator '{unary.Operator.Kind}' is not supported during comptime evaluation.");

                return AnalyzeExpression(unary.Operand);
            }

            case BoundBinaryExpression binary:
            {
                ComptimeSupportResult leftResult = AnalyzeExpression(binary.Left);
                if (!leftResult.IsSupported)
                    return leftResult;

                return AnalyzeExpression(binary.Right);
            }

            case BoundCallExpression call:
            {
                if (call.Function.ReturnTypes.Count > 1)
                    return Unsupported(call.Span, $"function '{call.Function.Name}' returns multiple values.");

                foreach (BoundExpression argument in call.Arguments)
                {
                    ComptimeSupportResult argumentResult = AnalyzeExpression(argument);
                    if (!argumentResult.IsSupported)
                        return argumentResult;
                }

                return Supported();
            }

            case BoundIfExpression ifExpression:
            {
                ComptimeSupportResult conditionResult = AnalyzeExpression(ifExpression.Condition);
                if (!conditionResult.IsSupported)
                    return conditionResult;

                ComptimeSupportResult thenResult = AnalyzeExpression(ifExpression.ThenExpression);
                if (!thenResult.IsSupported)
                    return thenResult;

                return AnalyzeExpression(ifExpression.ElseExpression);
            }

            case BoundConversionExpression conversion:
                return AnalyzeExpression(conversion.Expression);

            case BoundCastExpression cast:
                return AnalyzeExpression(cast.Expression);

            case BoundBitcastExpression bitcast:
                return AnalyzeExpression(bitcast.Expression);

            case BoundIntrinsicCallExpression:
                return Unsupported(expression.Span, "intrinsic calls are not supported during comptime evaluation.");

            case BoundModuleCallExpression:
                return Unsupported(expression.Span, "module calls are not supported during comptime evaluation.");

            case BoundArrayLiteralExpression:
                return Unsupported(expression.Span, "array literals are not supported during comptime evaluation.");

            case BoundStructLiteralExpression:
                return Unsupported(expression.Span, "struct literals are not supported during comptime evaluation.");

            case BoundMemberAccessExpression:
                return Unsupported(expression.Span, "member access is not supported during comptime evaluation.");

            case BoundIndexExpression:
                return Unsupported(expression.Span, "indexing is not supported during comptime evaluation.");

            case BoundPointerDerefExpression:
                return Unsupported(expression.Span, "pointer dereference is not supported during comptime evaluation.");

            case BoundRangeExpression:
                return Unsupported(expression.Span, "range expressions are not supported during comptime evaluation.");

            case BoundErrorExpression:
                return Unsupported(expression.Span, "error expressions are not evaluable.");

            default:
                return Unsupported(expression.Span, $"expression '{expression.Kind}' is not supported during comptime evaluation.");
        }
    }

    private static ComptimeSupportResult AnalyzeSymbolExpression(BoundSymbolExpression symbolExpression)
    {
        return symbolExpression.Symbol switch
        {
            VariableSymbol variable when variable.ScopeKind == VariableScopeKind.Local => Supported(),
            ParameterSymbol => Supported(),
            VariableSymbol variable => Forbidden(symbolExpression.Span, variable.Name),
            _ => Unsupported(symbolExpression.Span, $"symbol '{symbolExpression.Symbol.Name}' is not supported during comptime evaluation."),
        };
    }

    private static ComptimeSupportResult Supported() => new(true, ComptimeFailure.None);

    private static ComptimeSupportResult Unsupported(TextSpan span, string detail)
        => new(false, new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, span, detail));

    private static ComptimeSupportResult Forbidden(TextSpan span, string name)
        => new(false, new ComptimeFailure(ComptimeFailureKind.ForbiddenSymbolAccess, span, $"'{name}' cannot be accessed during comptime evaluation."));
}

internal sealed class ComptimeEvaluator
{
    private readonly Func<FunctionSymbol, BoundBlockStatement?> _functionBodyResolver;
    private readonly Func<FunctionSymbol, ComptimeSupportResult> _supportResolver;

    public ComptimeEvaluator(
        int fuel,
        Func<FunctionSymbol, BoundBlockStatement?> functionBodyResolver,
        Func<FunctionSymbol, ComptimeSupportResult> supportResolver)
    {
        Fuel = fuel;
        _functionBodyResolver = Requires.NotNull(functionBodyResolver);
        _supportResolver = Requires.NotNull(supportResolver);
    }

    public int Fuel { get; private set; }

    public bool TryEvaluateExpression(BoundExpression expression, out object? value, out ComptimeFailure failure)
    {
        return TryEvaluateExpression(expression, new Dictionary<Symbol, object?>(), out value, out failure);
    }

    private bool TryEvaluateExpression(
        BoundExpression expression,
        Dictionary<Symbol, object?> frame,
        out object? value,
        out ComptimeFailure failure)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                value = literal.Value;
                failure = ComptimeFailure.None;
                return true;

            case BoundEnumLiteralExpression enumLiteral:
                value = enumLiteral.Value;
                failure = ComptimeFailure.None;
                return true;

            case BoundSymbolExpression symbolExpression:
                return TryEvaluateSymbol(symbolExpression, frame, out value, out failure);

            case BoundUnaryExpression unary:
                return TryEvaluateUnary(unary, frame, out value, out failure);

            case BoundBinaryExpression binary:
                return TryEvaluateBinary(binary, frame, out value, out failure);

            case BoundCallExpression call:
                return TryEvaluateCall(call, frame, out value, out failure);

            case BoundIfExpression ifExpression:
                return TryEvaluateIfExpression(ifExpression, frame, out value, out failure);

            case BoundConversionExpression conversion:
                return TryEvaluateConverted(conversion.Expression, conversion.Type, frame, conversion.Span, out value, out failure);

            case BoundCastExpression cast:
                return TryEvaluateConverted(cast.Expression, cast.Type, frame, cast.Span, out value, out failure);

            case BoundBitcastExpression bitcast:
                return TryEvaluateConverted(bitcast.Expression, bitcast.Type, frame, bitcast.Span, out value, out failure);

            default:
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, expression.Span, $"expression '{expression.Kind}' is not supported during comptime evaluation.");
                return false;
        }
    }

    private bool TryEvaluateSymbol(
        BoundSymbolExpression symbolExpression,
        Dictionary<Symbol, object?> frame,
        out object? value,
        out ComptimeFailure failure)
    {
        if (!frame.TryGetValue(symbolExpression.Symbol, out value))
        {
            failure = symbolExpression.Symbol switch
            {
                VariableSymbol variable => new ComptimeFailure(ComptimeFailureKind.ForbiddenSymbolAccess, symbolExpression.Span, $"'{variable.Name}' cannot be accessed during comptime evaluation."),
                _ => new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, symbolExpression.Span, $"symbol '{symbolExpression.Symbol.Name}' is not supported during comptime evaluation."),
            };
            return false;
        }

        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryEvaluateUnary(
        BoundUnaryExpression unary,
        Dictionary<Symbol, object?> frame,
        out object? value,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(unary.Operand, frame, out object? operandValue, out failure))
        {
            value = null;
            return false;
        }

        switch (unary.Operator.Kind)
        {
            case BoundUnaryOperatorKind.LogicalNot when operandValue is bool boolOperand:
                value = !boolOperand;
                failure = ComptimeFailure.None;
                return true;

            case BoundUnaryOperatorKind.Negation when operandValue is IConvertible:
                return NormalizeLiteral(-Convert.ToInt64(operandValue, CultureInfo.InvariantCulture), unary.Type, unary.Span, out value, out failure);

            case BoundUnaryOperatorKind.BitwiseNot when operandValue is IConvertible:
                return NormalizeLiteral(~Convert.ToInt64(operandValue, CultureInfo.InvariantCulture), unary.Type, unary.Span, out value, out failure);

            case BoundUnaryOperatorKind.UnaryPlus when operandValue is IConvertible:
                return NormalizeLiteral(Convert.ToInt64(operandValue, CultureInfo.InvariantCulture), unary.Type, unary.Span, out value, out failure);

            default:
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, unary.Span, $"operator '{unary.Operator.Kind}' is not supported during comptime evaluation.");
                return false;
        }
    }

    private bool TryEvaluateBinary(
        BoundBinaryExpression binary,
        Dictionary<Symbol, object?> frame,
        out object? value,
        out ComptimeFailure failure)
    {
        if (binary.Operator.Kind == BoundBinaryOperatorKind.LogicalAnd)
        {
            if (!TryEvaluateExpression(binary.Left, frame, out object? leftValue, out failure))
            {
                value = null;
                return false;
            }

            if (leftValue is not bool leftBool)
            {
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, binary.Left.Span, "logical expressions require bool operands.");
                return false;
            }

            if (!leftBool)
            {
                value = false;
                failure = ComptimeFailure.None;
                return true;
            }

            if (!TryEvaluateExpression(binary.Right, frame, out object? rightValue, out failure))
            {
                value = null;
                return false;
            }

            if (rightValue is not bool rightBool)
            {
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, binary.Right.Span, "logical expressions require bool operands.");
                return false;
            }

            value = rightBool;
            failure = ComptimeFailure.None;
            return true;
        }

        if (binary.Operator.Kind == BoundBinaryOperatorKind.LogicalOr)
        {
            if (!TryEvaluateExpression(binary.Left, frame, out object? leftValue, out failure))
            {
                value = null;
                return false;
            }

            if (leftValue is not bool leftBool)
            {
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, binary.Left.Span, "logical expressions require bool operands.");
                return false;
            }

            if (leftBool)
            {
                value = true;
                failure = ComptimeFailure.None;
                return true;
            }

            if (!TryEvaluateExpression(binary.Right, frame, out object? rightValue, out failure))
            {
                value = null;
                return false;
            }

            if (rightValue is not bool rightBool)
            {
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, binary.Right.Span, "logical expressions require bool operands.");
                return false;
            }

            value = rightBool;
            failure = ComptimeFailure.None;
            return true;
        }

        if (!TryEvaluateExpression(binary.Left, frame, out object? rawLeftValue, out failure)
            || !TryEvaluateExpression(binary.Right, frame, out object? rawRightValue, out failure))
        {
            value = null;
            return false;
        }

        if (rawLeftValue is bool leftBoolValue && rawRightValue is bool rightBoolValue)
        {
            object? boolResult = binary.Operator.Kind switch
            {
                BoundBinaryOperatorKind.Equals => leftBoolValue == rightBoolValue,
                BoundBinaryOperatorKind.NotEquals => leftBoolValue != rightBoolValue,
                _ => null,
            };

            if (boolResult is not null)
            {
                value = boolResult;
                failure = ComptimeFailure.None;
                return true;
            }
        }

        if (rawLeftValue is not IConvertible || rawRightValue is not IConvertible)
        {
            value = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, binary.Span, "binary operands are not compile-time scalars.");
            return false;
        }

        long left = Convert.ToInt64(rawLeftValue, CultureInfo.InvariantCulture);
        long right = Convert.ToInt64(rawRightValue, CultureInfo.InvariantCulture);
        switch (binary.Operator.Kind)
        {
            case BoundBinaryOperatorKind.Add:
                return NormalizeLiteral(left + right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.Subtract:
                return NormalizeLiteral(left - right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.Multiply:
                return NormalizeLiteral(left * right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.Divide:
                if (right == 0)
                    return FailNotEvaluable(binary.Span, "division by zero is not evaluable at compile time.", out value, out failure);
                return NormalizeLiteral(left / right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.Modulo:
                if (right == 0)
                    return FailNotEvaluable(binary.Span, "modulo by zero is not evaluable at compile time.", out value, out failure);
                return NormalizeLiteral(left % right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.BitwiseAnd:
                return NormalizeLiteral(left & right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.BitwiseOr:
                return NormalizeLiteral(left | right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.BitwiseXor:
                return NormalizeLiteral(left ^ right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.ShiftLeft:
            case BoundBinaryOperatorKind.ArithmeticShiftLeft:
                return NormalizeLiteral(left << (int)right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.ShiftRight:
            case BoundBinaryOperatorKind.ArithmeticShiftRight:
                return NormalizeLiteral(left >> (int)right, binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.RotateLeft:
                return NormalizeLiteral((long)(((uint)left << (int)right) | ((uint)left >> (32 - ((int)right & 31)))), binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.RotateRight:
                return NormalizeLiteral((long)(((uint)left >> (int)right) | ((uint)left << (32 - ((int)right & 31)))), binary.Type, binary.Span, out value, out failure);
            case BoundBinaryOperatorKind.Equals:
                value = left == right;
                failure = ComptimeFailure.None;
                return true;
            case BoundBinaryOperatorKind.NotEquals:
                value = left != right;
                failure = ComptimeFailure.None;
                return true;
            case BoundBinaryOperatorKind.Less:
                value = left < right;
                failure = ComptimeFailure.None;
                return true;
            case BoundBinaryOperatorKind.LessOrEqual:
                value = left <= right;
                failure = ComptimeFailure.None;
                return true;
            case BoundBinaryOperatorKind.Greater:
                value = left > right;
                failure = ComptimeFailure.None;
                return true;
            case BoundBinaryOperatorKind.GreaterOrEqual:
                value = left >= right;
                failure = ComptimeFailure.None;
                return true;
            default:
                value = null;
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, binary.Span, $"operator '{binary.Operator.Kind}' is not supported during comptime evaluation.");
                return false;
        }
    }

    private bool TryEvaluateIfExpression(
        BoundIfExpression ifExpression,
        Dictionary<Symbol, object?> frame,
        out object? value,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(ifExpression.Condition, frame, out object? conditionValue, out failure))
        {
            value = null;
            return false;
        }

        if (conditionValue is not bool conditionBool)
        {
            value = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, ifExpression.Condition.Span, "if-expression conditions must be bool.");
            return false;
        }

        return TryEvaluateExpression(conditionBool ? ifExpression.ThenExpression : ifExpression.ElseExpression, frame, out value, out failure);
    }

    private bool TryEvaluateConverted(
        BoundExpression expression,
        TypeSymbol targetType,
        Dictionary<Symbol, object?> frame,
        TextSpan span,
        out object? value,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(expression, frame, out object? operandValue, out failure))
        {
            value = null;
            return false;
        }

        return NormalizeLiteral(operandValue, targetType, span, out value, out failure);
    }

    private bool TryEvaluateCall(
        BoundCallExpression call,
        Dictionary<Symbol, object?> callerFrame,
        out object? value,
        out ComptimeFailure failure)
    {
        if (call.Arguments.Count != call.Function.Parameters.Count)
        {
            value = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, call.Span, $"call to '{call.Function.Name}' does not have a full argument list.");
            return false;
        }

        BoundBlockStatement? body = _functionBodyResolver(call.Function);
        if (body is null)
        {
            value = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, call.Span, $"function body for '{call.Function.Name}' is unavailable during comptime evaluation.");
            return false;
        }

        ComptimeSupportResult support = _supportResolver(call.Function);
        if (!support.IsSupported)
        {
            value = null;
            failure = support.Failure;
            return false;
        }

        Dictionary<Symbol, object?> calleeFrame = new();
        for (int i = 0; i < call.Function.Parameters.Count; i++)
        {
            if (!TryEvaluateExpression(call.Arguments[i], callerFrame, out object? argumentValue, out failure))
            {
                value = null;
                return false;
            }

            ParameterSymbol parameter = call.Function.Parameters[i];
            if (!NormalizeLiteral(argumentValue, parameter.Type, call.Arguments[i].Span, out object? normalizedArgument, out failure))
            {
                value = null;
                return false;
            }

            calleeFrame[parameter] = normalizedArgument;
        }

        if (!TryExecuteBlock(body, calleeFrame, out EvaluationOutcome outcome, out failure))
        {
            value = null;
            return false;
        }

        if (outcome.Kind != EvaluationOutcomeKind.Return)
        {
            value = call.Function.ReturnTypes.Count == 0 ? null : 0L;
            failure = ComptimeFailure.None;
            return call.Function.ReturnTypes.Count == 0
                || NormalizeLiteral(value, call.Type, call.Span, out value, out failure);
        }

        if (call.Function.ReturnTypes.Count == 0)
        {
            value = null;
            failure = ComptimeFailure.None;
            return true;
        }

        return NormalizeLiteral(outcome.Value, call.Type, call.Span, out value, out failure);
    }

    private bool TryExecuteBlock(
        BoundBlockStatement block,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        foreach (BoundStatement statement in block.Statements)
        {
            if (!TryExecuteStatement(statement, frame, out outcome, out failure))
                return false;

            if (outcome.Kind != EvaluationOutcomeKind.None)
                return true;
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryExecuteStatement(
        BoundStatement statement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                return TryExecuteBlock(block, frame, out outcome, out failure);

            case BoundVariableDeclarationStatement declaration:
            {
                object? initializerValue = 0L;
                if (declaration.Initializer is not null
                    && !TryEvaluateExpression(declaration.Initializer, frame, out initializerValue, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }

                if (!NormalizeLiteral(initializerValue, declaration.Symbol.Type, declaration.Span, out object? normalizedInitializer, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }

                frame[declaration.Symbol] = normalizedInitializer;
                outcome = EvaluationOutcome.None;
                return true;
            }

            case BoundAssignmentStatement assignment:
                return TryExecuteAssignment(assignment, frame, out outcome, out failure);

            case BoundExpressionStatement expressionStatement:
            {
                if (!TryEvaluateExpression(expressionStatement.Expression, frame, out _, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }

                outcome = EvaluationOutcome.None;
                return true;
            }

            case BoundIfStatement ifStatement:
                return TryExecuteIfStatement(ifStatement, frame, out outcome, out failure);

            case BoundWhileStatement whileStatement:
                return TryExecuteWhileStatement(whileStatement, frame, out outcome, out failure);

            case BoundForStatement forStatement:
                return TryExecuteForStatement(forStatement, frame, out outcome, out failure);

            case BoundLoopStatement loopStatement:
                return TryExecuteLoopStatement(loopStatement, frame, out outcome, out failure);

            case BoundRepLoopStatement repLoopStatement:
                return TryExecuteRepLoopStatement(repLoopStatement, frame, out outcome, out failure);

            case BoundRepForStatement repForStatement:
                return TryExecuteRepForStatement(repForStatement, frame, out outcome, out failure);

            case BoundNoirqStatement noirqStatement:
                return TryExecuteBlock(noirqStatement.Body, frame, out outcome, out failure);

            case BoundReturnStatement returnStatement:
            {
                object? returnValue = null;
                if (returnStatement.Values.Count > 0
                    && !TryEvaluateExpression(returnStatement.Values[0], frame, out returnValue, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }

                failure = ComptimeFailure.None;
                outcome = EvaluationOutcome.Return(returnValue);
                return true;
            }

            case BoundBreakStatement:
                outcome = EvaluationOutcome.Break;
                failure = ComptimeFailure.None;
                return true;

            case BoundContinueStatement:
                outcome = EvaluationOutcome.Continue;
                failure = ComptimeFailure.None;
                return true;

            default:
                outcome = EvaluationOutcome.None;
                failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, statement.Span, $"statement '{statement.Kind}' is not supported during comptime evaluation.");
                return false;
        }
    }

    private bool TryExecuteAssignment(
        BoundAssignmentStatement assignment,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        if (assignment.Target is not BoundSymbolAssignmentTarget symbolTarget)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, assignment.Target.Span, "only local variable assignment is supported during comptime evaluation.");
            return false;
        }

        if (symbolTarget.Symbol is not VariableSymbol variable || variable.ScopeKind != VariableScopeKind.Local)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.ForbiddenSymbolAccess, assignment.Target.Span, $"'{symbolTarget.Symbol.Name}' cannot be assigned during comptime evaluation.");
            return false;
        }

        if (!TryEvaluateExpression(assignment.Value, frame, out object? assignedValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        object? finalValue = assignedValue;
        if (assignment.OperatorKind != TokenKind.Equal)
        {
            if (!frame.TryGetValue(symbolTarget.Symbol, out object? currentValue))
            {
                outcome = EvaluationOutcome.None;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, assignment.Target.Span, $"'{symbolTarget.Symbol.Name}' does not have a compile-time value.");
                return false;
            }

            BoundBinaryOperatorKind operation = assignment.OperatorKind switch
            {
                TokenKind.PlusEqual => BoundBinaryOperatorKind.Add,
                TokenKind.MinusEqual => BoundBinaryOperatorKind.Subtract,
                TokenKind.PercentEqual => BoundBinaryOperatorKind.Modulo,
                TokenKind.AmpersandEqual => BoundBinaryOperatorKind.BitwiseAnd,
                TokenKind.PipeEqual => BoundBinaryOperatorKind.BitwiseOr,
                TokenKind.CaretEqual => BoundBinaryOperatorKind.BitwiseXor,
                TokenKind.LessLessEqual => BoundBinaryOperatorKind.ShiftLeft,
                TokenKind.GreaterGreaterEqual => BoundBinaryOperatorKind.ShiftRight,
                _ => BoundBinaryOperatorKind.Add,
            };

            if (!TryApplyCompoundAssignment(operation, currentValue, assignedValue, variable.Type, assignment.Span, out finalValue, out failure))
            {
                outcome = EvaluationOutcome.None;
                return false;
            }
        }

        if (!NormalizeLiteral(finalValue, variable.Type, assignment.Value.Span, out object? normalizedValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        frame[symbolTarget.Symbol] = normalizedValue;
        outcome = EvaluationOutcome.None;
        return true;
    }

    private static bool TryApplyCompoundAssignment(
        BoundBinaryOperatorKind operation,
        object? leftValue,
        object? rightValue,
        TypeSymbol targetType,
        TextSpan span,
        out object? result,
        out ComptimeFailure failure)
    {
        if (leftValue is not IConvertible || rightValue is not IConvertible)
        {
            result = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, span, "compound assignment requires integer operands.");
            return false;
        }

        long left = Convert.ToInt64(leftValue, CultureInfo.InvariantCulture);
        long right = Convert.ToInt64(rightValue, CultureInfo.InvariantCulture);
        long raw = operation switch
        {
            BoundBinaryOperatorKind.Add => left + right,
            BoundBinaryOperatorKind.Subtract => left - right,
            BoundBinaryOperatorKind.Modulo when right != 0 => left % right,
            BoundBinaryOperatorKind.BitwiseAnd => left & right,
            BoundBinaryOperatorKind.BitwiseOr => left | right,
            BoundBinaryOperatorKind.BitwiseXor => left ^ right,
            BoundBinaryOperatorKind.ShiftLeft => left << (int)right,
            BoundBinaryOperatorKind.ShiftRight => left >> (int)right,
            _ => long.MinValue,
        };

        if (operation == BoundBinaryOperatorKind.Modulo && right == 0)
        {
            result = null;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, span, "modulo by zero is not evaluable at compile time.");
            return false;
        }

        return NormalizeLiteral(raw, targetType, span, out result, out failure);
    }

    private bool TryExecuteIfStatement(
        BoundIfStatement ifStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(ifStatement.Condition, frame, out object? conditionValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        if (conditionValue is not bool conditionBool)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, ifStatement.Condition.Span, "if conditions must be bool.");
            return false;
        }

        if (!TryExecuteStatement(conditionBool ? ifStatement.ThenBody : ifStatement.ElseBody ?? EmptyBlock.Instance, frame, out outcome, out failure))
            return false;

        return true;
    }

    private bool TryExecuteWhileStatement(
        BoundWhileStatement whileStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        while (true)
        {
            if (!TryEvaluateExpression(whileStatement.Condition, frame, out object? conditionValue, out failure))
            {
                outcome = EvaluationOutcome.None;
                return false;
            }

            if (conditionValue is not bool conditionBool)
            {
                outcome = EvaluationOutcome.None;
                failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, whileStatement.Condition.Span, "while conditions must be bool.");
                return false;
            }

            if (!conditionBool)
                break;

            if (!TryExecuteBlock(whileStatement.Body, frame, out outcome, out failure))
                return false;

            if (outcome.Kind == EvaluationOutcomeKind.Return)
                return true;
            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;

            if (!SpendFuel(whileStatement.Span, out failure))
            {
                outcome = EvaluationOutcome.None;
                return false;
            }
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryExecuteForStatement(
        BoundForStatement forStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(forStatement.Iterable, frame, out object? iterableValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        if (iterableValue is not IConvertible)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, forStatement.Iterable.Span, "for loops require a compile-time integer count.");
            return false;
        }

        long count = Convert.ToInt64(iterableValue, CultureInfo.InvariantCulture);
        for (long index = 0; index < count; index++)
        {
            if (forStatement.IndexVariable is not null)
                frame[forStatement.IndexVariable] = unchecked((uint)index);

            if (!TryExecuteBlock(forStatement.Body, frame, out outcome, out failure))
                return false;

            if (outcome.Kind == EvaluationOutcomeKind.Return)
                return true;
            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryExecuteLoopStatement(
        BoundLoopStatement loopStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        while (true)
        {
            if (!TryExecuteBlock(loopStatement.Body, frame, out outcome, out failure))
                return false;

            if (outcome.Kind == EvaluationOutcomeKind.Return)
                return true;
            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;

            if (!SpendFuel(loopStatement.Span, out failure))
            {
                outcome = EvaluationOutcome.None;
                return false;
            }
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryExecuteRepLoopStatement(
        BoundRepLoopStatement repLoopStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(repLoopStatement.Count, frame, out object? countValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        if (countValue is not IConvertible)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, repLoopStatement.Count.Span, "rep loop counts must be compile-time integers.");
            return false;
        }

        long count = Convert.ToInt64(countValue, CultureInfo.InvariantCulture);
        if (count == 0)
        {
            while (true)
            {
                if (!TryExecuteBlock(repLoopStatement.Body, frame, out outcome, out failure))
                    return false;

                if (outcome.Kind == EvaluationOutcomeKind.Return)
                    return true;
                if (outcome.Kind == EvaluationOutcomeKind.Break)
                    break;

                if (!SpendFuel(repLoopStatement.Span, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }
            }
        }
        else
        {
            for (long iteration = 0; iteration < count; iteration++)
            {
                if (!TryExecuteBlock(repLoopStatement.Body, frame, out outcome, out failure))
                    return false;

                if (outcome.Kind == EvaluationOutcomeKind.Return)
                    return true;
                if (outcome.Kind == EvaluationOutcomeKind.Break)
                    break;

                if (iteration + 1 < count && !SpendFuel(repLoopStatement.Span, out failure))
                {
                    outcome = EvaluationOutcome.None;
                    return false;
                }
            }
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool TryExecuteRepForStatement(
        BoundRepForStatement repForStatement,
        Dictionary<Symbol, object?> frame,
        out EvaluationOutcome outcome,
        out ComptimeFailure failure)
    {
        if (!TryEvaluateExpression(repForStatement.Start, frame, out object? startValue, out failure)
            || !TryEvaluateExpression(repForStatement.End, frame, out object? endValue, out failure))
        {
            outcome = EvaluationOutcome.None;
            return false;
        }

        if (startValue is not IConvertible || endValue is not IConvertible)
        {
            outcome = EvaluationOutcome.None;
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, repForStatement.Span, "rep for bounds must be compile-time integers.");
            return false;
        }

        long start = Convert.ToInt64(startValue, CultureInfo.InvariantCulture);
        long end = Convert.ToInt64(endValue, CultureInfo.InvariantCulture);
        for (long index = start; index < end; index++)
        {
            frame[repForStatement.Variable] = index;
            if (!TryExecuteBlock(repForStatement.Body, frame, out outcome, out failure))
                return false;

            if (outcome.Kind == EvaluationOutcomeKind.Return)
                return true;
            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;
        }

        outcome = EvaluationOutcome.None;
        failure = ComptimeFailure.None;
        return true;
    }

    private bool SpendFuel(TextSpan span, out ComptimeFailure failure)
    {
        Fuel--;
        if (Fuel >= 0)
        {
            failure = ComptimeFailure.None;
            return true;
        }

        failure = new ComptimeFailure(ComptimeFailureKind.FuelExhausted, span, "comptime evaluation ran out of fuel.");
        return false;
    }

    private static bool NormalizeLiteral(object? rawValue, TypeSymbol targetType, TextSpan span, out object? normalizedValue, out ComptimeFailure failure)
    {
        if (targetType.IsVoid)
        {
            normalizedValue = null;
            failure = ComptimeFailure.None;
            return true;
        }

        if (targetType.IsUnknown)
        {
            normalizedValue = rawValue;
            failure = ComptimeFailure.None;
            return true;
        }

        if (!ComptimeTypeFacts.TryNormalizeValue(rawValue, targetType, out normalizedValue))
        {
            failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");
            return false;
        }

        failure = ComptimeFailure.None;
        return true;
    }

    private static bool FailNotEvaluable(TextSpan span, string detail, out object? value, out ComptimeFailure failure)
    {
        value = null;
        failure = new ComptimeFailure(ComptimeFailureKind.NotEvaluable, span, detail);
        return false;
    }

    private enum EvaluationOutcomeKind
    {
        None,
        Return,
        Break,
        Continue,
    }

    private readonly record struct EvaluationOutcome(EvaluationOutcomeKind Kind, object? Value)
    {
        public static EvaluationOutcome None => new(EvaluationOutcomeKind.None, null);
        public static EvaluationOutcome Break => new(EvaluationOutcomeKind.Break, null);
        public static EvaluationOutcome Continue => new(EvaluationOutcomeKind.Continue, null);
        public static EvaluationOutcome Return(object? value) => new(EvaluationOutcomeKind.Return, value);
    }

    private sealed class EmptyBlock : BoundStatement
    {
        public static readonly EmptyBlock Instance = new();

        private EmptyBlock()
            : base(BoundNodeKind.BlockStatement, new TextSpan(0, 0))
        {
        }
    }
}
