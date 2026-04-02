using System;
using System.Collections.Generic;
using System.Globalization;
using Blade.Semantics.Bound;
using Blade.Source;
using Blade.Syntax;

namespace Blade.Semantics;

internal enum ComptimeFailureKind
{
    NotEvaluable,
    UnsupportedConstruct,
    ForbiddenSymbolAccess,
    FuelExhausted,
}

internal readonly record struct ComptimeFailure(ComptimeFailureKind Kind, TextSpan Span, string Detail)
{

}

internal readonly record struct ComptimeSupportResult(bool IsSupported, ComptimeFailure Failure);

internal sealed class ComptimeResult
{
    public static readonly ComptimeResult Void = new(BladeValue.Void);
    public static readonly ComptimeResult Undefined = new(BladeValue.Undefined);
    public static readonly ComptimeResult True = new(true);
    public static readonly ComptimeResult False = new(false);

    private readonly BladeValue? value;
    private readonly ComptimeFailure? failure;

    private ComptimeResult(BladeValue value)
    {
        this.value = Requires.NotNull(value);
    }

    public ComptimeResult(bool value)
        : this(new RuntimeBladeValue(BuiltinTypes.Bool, value))
    {
    }

    public ComptimeResult(long value)
        : this(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, value))
    {
    }

    public ComptimeResult(int value)
        : this(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, value))
    {
    }

    public ComptimeResult(uint value)
        : this(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, value))
    {
    }

    public ComptimeResult(string value)
        : this(new ComptimeBladeValue((ComptimeTypeSymbol)BuiltinTypes.String, value))
    {
    }

    public ComptimeResult(ComptimeFailureKind kind, TextSpan span, string detail)
    {
        failure = new ComptimeFailure(kind, span, detail);
    }

    public ComptimeResult(ComptimeFailure failure)
        : this(failure.Kind, failure.Span, failure.Detail)
    {
    }

    public BladeValue Value
    {
        get
        {
            Assert.Invariant(value is not null, "Failed comptime results do not have a value.");
            return value;
        }
    }

    public bool TryGetBool(out bool result)
    {
        return TryGetPayload(out result);
    }

    public bool TryGetLong(out long result)
    {
        return TryGetPayload(out result);
    }

    public bool TryGetInt(out int result)
    {
        return TryGetPayload(out result);
    }

    public bool TryGetUInt(out uint result)
    {
        return TryGetPayload(out result);
    }

    public bool TryGetString(out string result)
    {
        return TryGetPayload(out result);
    }

    public bool TryGetFailure(out ComptimeFailure result)
    {
        result = failure ?? default;
        return failure is not null;
    }

    /// <summary>
    /// Converts the result into a long if possible. Only applies to numeric types (int, uint, long).
    /// </summary>
    /// <param name="result"></param>
    /// <returns></returns>
    public bool TryConvertToLong(out long result)
    {
        if (IsFailed)
        {
            result = 0L;
            return false;
        }

        object payload = Value.Value;
        switch (payload)
        {
            case sbyte sbyteValue:
                result = sbyteValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            default:
                result = 0L;
                return false;
        }
    }

    public TypeSymbol? Type => value?.Type;

    public bool IsFailed => failure is not null;
    public bool IsUndefined => !IsFailed && ReferenceEquals(Value.Type, BuiltinTypes.UndefinedLiteral);
    public bool IsVoid => !IsFailed && ReferenceEquals(Value.Type, BuiltinTypes.Void);

    public bool IsNumeric => !IsFailed && Value.Value is sbyte or byte or short or ushort or int or uint or long;

    private bool TryGetPayload<T>(out T result)
    {
        if (IsFailed || Value.Value is not T typedValue)
        {
            result = default!;
            return false;
        }

        result = typedValue;
        return true;
    }
}


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
                if (TryConvertConvertibleToInt64(convertible, out long integerValue))
                {
                    normalized = integerValue != 0;
                    return true;
                }
            }

            normalized = null;
            return false;
        }

        if (ReferenceEquals(targetType, BuiltinTypes.IntegerLiteral))
        {
            if (value is bool or string)
            {
                normalized = null;
                return false;
            }

            if (value is IConvertible convertible && TryConvertConvertibleToInt64(convertible, out long integerValue))
            {
                normalized = integerValue;
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

        if (targetType is RuntimeTypeSymbol runtimeType && value is not null)
            return runtimeType.TryNormalizeRuntimeObject(value, out normalized!);

        normalized = null;
        return false;
    }

    private static bool TryConvertConvertibleToInt64(IConvertible convertible, out long converted)
    {
        try
        {
            converted = Convert.ToInt64(convertible, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
        }
        catch (InvalidCastException)
        {
        }
        catch (OverflowException)
        {
        }

        converted = 0;
        return false;
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
                return AnalyzeStatement(repLoopStatement.Body);

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
                    if (unary.Operator.Kind is BoundUnaryOperatorKind.AddressOf)
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
            VariableSymbol variable when variable.IsGlobalStorage && variable.IsConst => Supported(),
            ParameterSymbol => Supported(),
            VariableSymbol variable => Forbidden(symbolExpression.Span, variable.Name),
            _ => Unsupported(symbolExpression.Span, $"symbol '{symbolExpression.Symbol.Name}' is not supported during comptime evaluation."),
        };
    }

    private static ComptimeSupportResult Supported() => new(true, default);

    private static ComptimeSupportResult Unsupported(TextSpan span, string detail)
        => new(false, new ComptimeFailure(ComptimeFailureKind.UnsupportedConstruct, span, detail));

    private static ComptimeSupportResult Forbidden(TextSpan span, string name)
        => new(false, new ComptimeFailure(ComptimeFailureKind.ForbiddenSymbolAccess, span, $"'{name}' cannot be accessed during comptime evaluation."));
}

internal sealed class ComptimeEvaluator
{
    private static readonly BoundBlockStatement EmptyBlock = new([], new TextSpan(0, 0));

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

    public ComptimeResult TryEvaluateExpression(BoundExpression expression)
    {
        return TryEvaluateExpression(expression, new Dictionary<Symbol, ComptimeResult>());
    }

    public ComptimeResult TryEvaluateExpression(
        BoundExpression expression,
        IReadOnlyDictionary<Symbol, ComptimeResult> initialFrame)
    {
        Dictionary<Symbol, ComptimeResult> frame = new(initialFrame.Count);
        foreach ((Symbol symbol, ComptimeResult symbolValue) in initialFrame)
            frame[symbol] = symbolValue;

        return TryEvaluateExpression(expression, frame);
    }

    private ComptimeResult TryEvaluateExpression(
        BoundExpression expression,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        return expression switch
        {
            BoundLiteralExpression literal => CreateValueResult(literal.Value, literal.Type),
            BoundEnumLiteralExpression enumLiteral => new ComptimeResult(enumLiteral.Value),
            BoundSymbolExpression symbolExpression => TryEvaluateSymbol(symbolExpression, frame),
            BoundUnaryExpression unary => TryEvaluateUnary(unary, frame),
            BoundBinaryExpression binary => TryEvaluateBinary(binary, frame),
            BoundCallExpression call => TryEvaluateCall(call, frame),
            BoundIfExpression ifExpression => TryEvaluateIfExpression(ifExpression, frame),
            BoundConversionExpression conversion => TryEvaluateConverted(conversion.Expression, conversion.Type, frame, conversion.Span),
            BoundCastExpression cast => TryEvaluateConverted(cast.Expression, cast.Type, frame, cast.Span),
            BoundBitcastExpression bitcast => TryEvaluateConverted(bitcast.Expression, bitcast.Type, frame, bitcast.Span),
            _ => new ComptimeResult(ComptimeFailureKind.UnsupportedConstruct, expression.Span, $"expression '{expression.Kind}' is not supported during comptime evaluation."),
        };
    }

    private ComptimeResult TryEvaluateSymbol(
        BoundSymbolExpression symbolExpression,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        if (!frame.TryGetValue(symbolExpression.Symbol, out ComptimeResult? value))
        {
            return symbolExpression.Symbol switch
            {
                VariableSymbol variable => new ComptimeResult(ComptimeFailureKind.ForbiddenSymbolAccess, symbolExpression.Span, $"'{variable.Name}' cannot be accessed during comptime evaluation."),
                _ => new ComptimeResult(ComptimeFailureKind.UnsupportedConstruct, symbolExpression.Span, $"symbol '{symbolExpression.Symbol.Name}' is not supported during comptime evaluation."),
            };
        }

        Assert.Invariant(value is not null);
        return value;
    }

    private ComptimeResult TryEvaluateUnary(
        BoundUnaryExpression unary,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        ComptimeResult operandResult = TryEvaluateExpression(unary.Operand, frame);
        if (operandResult.IsFailed)
            return operandResult;

        switch (unary.Operator.Kind)
        {
            case BoundUnaryOperatorKind.LogicalNot when operandResult.TryGetBool(out bool boolOperand):
                return boolOperand ? ComptimeResult.False : ComptimeResult.True;

            case BoundUnaryOperatorKind.Negation:
            {
                ComptimeResult converted = TryConvertToInt64(operandResult, unary.Operand.Span, "unary operand is not a compile-time integer.");
                if (converted.IsFailed)
                    return converted;

                converted.TryGetLong(out long negatedValue);
                return new ComptimeResult(-negatedValue);
            }

            case BoundUnaryOperatorKind.BitwiseNot:
            {
                ComptimeResult converted = TryConvertToInt64(operandResult, unary.Operand.Span, "unary operand is not a compile-time integer.");
                if (converted.IsFailed)
                    return converted;

                converted.TryGetLong(out long bitwiseValue);
                return new ComptimeResult(~bitwiseValue);
            }

            case BoundUnaryOperatorKind.UnaryPlus:
            {
                ComptimeResult converted = TryConvertToInt64(operandResult, unary.Operand.Span, "unary operand is not a compile-time integer.");
                if (converted.IsFailed)
                    return converted;

                converted.TryGetLong(out long positiveValue);
                return new ComptimeResult(positiveValue);
            }

            default:
                return new ComptimeResult(ComptimeFailureKind.UnsupportedConstruct, unary.Span, $"operator '{unary.Operator.Kind}' is not supported during comptime evaluation.");
        }
    }

    private ComptimeResult TryEvaluateBinary(
        BoundBinaryExpression binary,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        if (binary.Operator.Kind == BoundBinaryOperatorKind.LogicalAnd)
        {
            ComptimeResult logicalLeftResult = TryEvaluateExpression(binary.Left, frame);
            if (logicalLeftResult.IsFailed)
                return logicalLeftResult;

            if (!logicalLeftResult.TryGetBool(out bool leftBool))
                return new ComptimeResult(ComptimeFailureKind.NotEvaluable, binary.Left.Span, "logical expressions require bool operands.");

            if (!leftBool)
                return ComptimeResult.False;

            ComptimeResult logicalRightResult = TryEvaluateExpression(binary.Right, frame);
            if (logicalRightResult.IsFailed)
                return logicalRightResult;

            return logicalRightResult.TryGetBool(out bool rightBool)
                ? new ComptimeResult(rightBool)
                : new ComptimeResult(ComptimeFailureKind.NotEvaluable, binary.Right.Span, "logical expressions require bool operands.");
        }

        if (binary.Operator.Kind == BoundBinaryOperatorKind.LogicalOr)
        {
            ComptimeResult logicalLeftResult = TryEvaluateExpression(binary.Left, frame);
            if (logicalLeftResult.IsFailed)
                return logicalLeftResult;

            if (!logicalLeftResult.TryGetBool(out bool leftBool))
                return new ComptimeResult(ComptimeFailureKind.NotEvaluable, binary.Left.Span, "logical expressions require bool operands.");

            if (leftBool)
                return ComptimeResult.True;

            ComptimeResult logicalRightResult = TryEvaluateExpression(binary.Right, frame);
            if (logicalRightResult.IsFailed)
                return logicalRightResult;

            return logicalRightResult.TryGetBool(out bool rightBool)
                ? new ComptimeResult(rightBool)
                : new ComptimeResult(ComptimeFailureKind.NotEvaluable, binary.Right.Span, "logical expressions require bool operands.");
        }

        ComptimeResult leftResult = TryEvaluateExpression(binary.Left, frame);
        if (leftResult.IsFailed)
            return leftResult;

        ComptimeResult rightResult = TryEvaluateExpression(binary.Right, frame);
        if (rightResult.IsFailed)
            return rightResult;

        if (leftResult.TryGetBool(out bool leftBoolValue) && rightResult.TryGetBool(out bool rightBoolValue))
        {
            bool? boolResult = binary.Operator.Kind switch
            {
                BoundBinaryOperatorKind.Equals => leftBoolValue == rightBoolValue,
                BoundBinaryOperatorKind.NotEquals => leftBoolValue != rightBoolValue,
                _ => null,
            };

            if (boolResult is not null)
                return new ComptimeResult(boolResult.Value);
        }

        ComptimeResult leftConverted = TryConvertToInt64(leftResult, binary.Left.Span, "binary operands are not compile-time integers.");
        if (leftConverted.IsFailed)
            return leftConverted;

        ComptimeResult rightConverted = TryConvertToInt64(rightResult, binary.Right.Span, "binary operands are not compile-time integers.");
        if (rightConverted.IsFailed)
            return rightConverted;

        leftConverted.TryGetLong(out long left);
        rightConverted.TryGetLong(out long right);

        return binary.Operator.Kind switch
        {
            BoundBinaryOperatorKind.Add => new ComptimeResult(left + right),
            BoundBinaryOperatorKind.Subtract => new ComptimeResult(left - right),
            BoundBinaryOperatorKind.Multiply => new ComptimeResult(left * right),
            BoundBinaryOperatorKind.Divide when right == 0 => FailNotEvaluable(binary.Span, "division by zero is not evaluable at compile time."),
            BoundBinaryOperatorKind.Divide => new ComptimeResult(left / right),
            BoundBinaryOperatorKind.Modulo when right == 0 => FailNotEvaluable(binary.Span, "modulo by zero is not evaluable at compile time."),
            BoundBinaryOperatorKind.Modulo => new ComptimeResult(left % right),
            BoundBinaryOperatorKind.BitwiseAnd => new ComptimeResult(left & right),
            BoundBinaryOperatorKind.BitwiseOr => new ComptimeResult(left | right),
            BoundBinaryOperatorKind.BitwiseXor => new ComptimeResult(left ^ right),
            BoundBinaryOperatorKind.ShiftLeft or BoundBinaryOperatorKind.ArithmeticShiftLeft => new ComptimeResult(left << (int)right),
            BoundBinaryOperatorKind.ShiftRight or BoundBinaryOperatorKind.ArithmeticShiftRight => new ComptimeResult(left >> (int)right),
            BoundBinaryOperatorKind.RotateLeft => new ComptimeResult((long)(((uint)left << (int)right) | ((uint)left >> (32 - ((int)right & 31))))),
            BoundBinaryOperatorKind.RotateRight => new ComptimeResult((long)(((uint)left >> (int)right) | ((uint)left << (32 - ((int)right & 31))))),
            BoundBinaryOperatorKind.Equals => new ComptimeResult(left == right),
            BoundBinaryOperatorKind.NotEquals => new ComptimeResult(left != right),
            BoundBinaryOperatorKind.Less => new ComptimeResult(left < right),
            BoundBinaryOperatorKind.LessOrEqual => new ComptimeResult(left <= right),
            BoundBinaryOperatorKind.Greater => new ComptimeResult(left > right),
            BoundBinaryOperatorKind.GreaterOrEqual => new ComptimeResult(left >= right),
            _ => new ComptimeResult(ComptimeFailureKind.UnsupportedConstruct, binary.Span, $"operator '{binary.Operator.Kind}' is not supported during comptime evaluation."),
        };
    }

    private ComptimeResult TryEvaluateIfExpression(
        BoundIfExpression ifExpression,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        ComptimeResult conditionResult = TryEvaluateExpression(ifExpression.Condition, frame);
        if (conditionResult.IsFailed)
            return conditionResult;

        if (!conditionResult.TryGetBool(out bool conditionBool))
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, ifExpression.Condition.Span, "if-expression conditions must be bool.");

        return TryEvaluateExpression(conditionBool ? ifExpression.ThenExpression : ifExpression.ElseExpression, frame);
    }

    private ComptimeResult TryEvaluateConverted(
        BoundExpression expression,
        TypeSymbol targetType,
        Dictionary<Symbol, ComptimeResult> frame,
        TextSpan span)
    {
        ComptimeResult operandResult = TryEvaluateExpression(expression, frame);
        return operandResult.IsFailed
            ? operandResult
            : NormalizeLiteral(operandResult, targetType, span);
    }

    private ComptimeResult TryEvaluateCall(
        BoundCallExpression call,
        Dictionary<Symbol, ComptimeResult> callerFrame)
    {
        if (call.Arguments.Count != call.Function.Parameters.Count)
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, call.Span, $"call to '{call.Function.Name}' does not have a full argument list.");

        BoundBlockStatement? body = _functionBodyResolver(call.Function);
        if (body is null)
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, call.Span, $"function body for '{call.Function.Name}' is unavailable during comptime evaluation.");

        ComptimeSupportResult support = _supportResolver(call.Function);
        if (!support.IsSupported)
            return new ComptimeResult(support.Failure);

        Dictionary<Symbol, ComptimeResult> calleeFrame = new(callerFrame);
        for (int i = 0; i < call.Function.Parameters.Count; i++)
        {
            ComptimeResult argumentValue = TryEvaluateExpression(call.Arguments[i], callerFrame);
            if (argumentValue.IsFailed)
                return argumentValue;

            ParameterSymbol parameter = call.Function.Parameters[i];
            ComptimeResult normalizedArgument = NormalizeLiteral(argumentValue, parameter.Type, call.Arguments[i].Span);
            if (normalizedArgument.IsFailed)
                return normalizedArgument;

            calleeFrame[parameter] = normalizedArgument;
        }

        EvaluationOutcome outcome = TryExecuteBlock(body, calleeFrame);
        if (outcome.Kind == EvaluationOutcomeKind.Failed)
            return outcome.Value;

        if (outcome.Kind != EvaluationOutcomeKind.Return)
        {
            return call.Function.ReturnTypes.Count == 0
                ? ComptimeResult.Void
                : NormalizeLiteral(new ComptimeResult(0L), call.Type, call.Span);
        }

        return call.Function.ReturnTypes.Count == 0
            ? ComptimeResult.Void
            : NormalizeLiteral(outcome.Value, call.Type, call.Span);
    }

    private EvaluationOutcome TryExecuteBlock(
        BoundBlockStatement block,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        foreach (BoundStatement statement in block.Statements)
        {
            EvaluationOutcome outcome = TryExecuteStatement(statement, frame);
            if (outcome.Kind != EvaluationOutcomeKind.None)
                return outcome;
        }

        return EvaluationOutcome.None;
    }

    private EvaluationOutcome TryExecuteStatement(
        BoundStatement statement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                return TryExecuteBlock(block, frame);

            case BoundVariableDeclarationStatement declaration:
            {
                ComptimeResult initializerValue = declaration.Initializer is null
                    ? new ComptimeResult(0L)
                    : TryEvaluateExpression(declaration.Initializer, frame);
                if (initializerValue.IsFailed)
                    return EvaluationOutcome.Failed(initializerValue);

                ComptimeResult normalizedInitializer = NormalizeLiteral(initializerValue, declaration.Symbol.Type, declaration.Span);
                if (normalizedInitializer.IsFailed)
                    return EvaluationOutcome.Failed(normalizedInitializer);

                frame[declaration.Symbol] = normalizedInitializer;
                return EvaluationOutcome.None;
            }

            case BoundAssignmentStatement assignment:
                return TryExecuteAssignment(assignment, frame);

            case BoundExpressionStatement expressionStatement:
            {
                ComptimeResult expressionResult = TryEvaluateExpression(expressionStatement.Expression, frame);
                return expressionResult.IsFailed
                    ? EvaluationOutcome.Failed(expressionResult)
                    : EvaluationOutcome.None;
            }

            case BoundIfStatement ifStatement:
                return TryExecuteIfStatement(ifStatement, frame);

            case BoundWhileStatement whileStatement:
                return TryExecuteWhileStatement(whileStatement, frame);

            case BoundForStatement forStatement:
                return TryExecuteForStatement(forStatement, frame);

            case BoundLoopStatement loopStatement:
                return TryExecuteLoopStatement(loopStatement, frame);

            case BoundRepLoopStatement repLoopStatement:
                return TryExecuteRepLoopStatement(repLoopStatement, frame);

            case BoundRepForStatement repForStatement:
                return TryExecuteRepForStatement(repForStatement, frame);

            case BoundNoirqStatement noirqStatement:
                return TryExecuteBlock(noirqStatement.Body, frame);

            case BoundReturnStatement returnStatement:
            {
                if (returnStatement.Values.Count == 0)
                    return EvaluationOutcome.Return(ComptimeResult.Void);

                ComptimeResult returnValue = TryEvaluateExpression(returnStatement.Values[0], frame);
                return returnValue.IsFailed
                    ? EvaluationOutcome.Failed(returnValue)
                    : EvaluationOutcome.Return(returnValue);
            }

            case BoundBreakStatement:
                return EvaluationOutcome.Break;

            case BoundContinueStatement:
                return EvaluationOutcome.Continue;

            default:
                return EvaluationOutcome.Failed(new ComptimeResult(ComptimeFailureKind.UnsupportedConstruct, statement.Span, $"statement '{statement.Kind}' is not supported during comptime evaluation."));
        }
    }

    private EvaluationOutcome TryExecuteAssignment(
        BoundAssignmentStatement assignment,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        if (assignment.Target is not BoundSymbolAssignmentTarget symbolTarget
            || symbolTarget.Symbol is not VariableSymbol { ScopeKind: VariableScopeKind.Local } variable)
        {
            return EvaluationOutcome.Failed(new ComptimeResult(
                ComptimeFailureKind.UnsupportedConstruct,
                assignment.Span,
                "assignment target is not supported during comptime evaluation."));
        }

        ComptimeResult assignedValue = TryEvaluateExpression(assignment.Value, frame);
        if (assignedValue.IsFailed)
            return EvaluationOutcome.Failed(assignedValue);

        ComptimeResult finalValue;
        if (assignment.OperatorKind == TokenKind.Equal)
        {
            finalValue = NormalizeLiteral(assignedValue, variable.Type, assignment.Value.Span);
        }
        else
        {
            bool foundCurrentValue = frame.TryGetValue(symbolTarget.Symbol, out ComptimeResult? currentValue);
            Assert.Invariant(foundCurrentValue);
            Assert.Invariant(currentValue is not null);

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

            finalValue = TryApplyCompoundAssignment(operation, currentValue, assignedValue, variable.Type, assignment.Span);
        }

        if (finalValue.IsFailed)
            return EvaluationOutcome.Failed(finalValue);

        frame[symbolTarget.Symbol] = finalValue;
        return EvaluationOutcome.None;
    }

    private static ComptimeResult TryApplyCompoundAssignment(
        BoundBinaryOperatorKind operation,
        ComptimeResult leftValue,
        ComptimeResult rightValue,
        TypeSymbol targetType,
        TextSpan span)
    {
        ComptimeResult leftConverted = TryConvertToInt64(leftValue, span, "compound assignment operands are not compile-time integers.");
        if (leftConverted.IsFailed)
            return leftConverted;

        ComptimeResult rightConverted = TryConvertToInt64(rightValue, span, "compound assignment operands are not compile-time integers.");
        if (rightConverted.IsFailed)
            return rightConverted;

        leftConverted.TryGetLong(out long left);
        rightConverted.TryGetLong(out long right);

        if (operation == BoundBinaryOperatorKind.Modulo && right == 0)
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, "modulo by zero is not evaluable at compile time.");

        long raw = operation switch
        {
            BoundBinaryOperatorKind.Add => left + right,
            BoundBinaryOperatorKind.Subtract => left - right,
            BoundBinaryOperatorKind.Modulo => left % right,
            BoundBinaryOperatorKind.BitwiseAnd => left & right,
            BoundBinaryOperatorKind.BitwiseOr => left | right,
            BoundBinaryOperatorKind.BitwiseXor => left ^ right,
            BoundBinaryOperatorKind.ShiftLeft => left << (int)right,
            BoundBinaryOperatorKind.ShiftRight => left >> (int)right,
            _ => Assert.UnreachableValue<long>($"Unsupported compound operation '{operation}'."),
        };

        return NormalizeLiteral(new ComptimeResult(raw), targetType, span);
    }

    private EvaluationOutcome TryExecuteIfStatement(
        BoundIfStatement ifStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        ComptimeResult conditionValue = TryEvaluateExpression(ifStatement.Condition, frame);
        if (conditionValue.IsFailed)
            return EvaluationOutcome.Failed(conditionValue);

        if (!conditionValue.TryGetBool(out bool conditionBool))
            return EvaluationOutcome.Failed(new ComptimeResult(ComptimeFailureKind.NotEvaluable, ifStatement.Condition.Span, "if-statement conditions must be bool."));

        return TryExecuteStatement(conditionBool ? ifStatement.ThenBody : ifStatement.ElseBody ?? EmptyBlock, frame);
    }

    private EvaluationOutcome TryExecuteWhileStatement(
        BoundWhileStatement whileStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        while (true)
        {
            ComptimeResult conditionValue = TryEvaluateExpression(whileStatement.Condition, frame);
            if (conditionValue.IsFailed)
                return EvaluationOutcome.Failed(conditionValue);

            if (!conditionValue.TryGetBool(out bool conditionBool))
                return EvaluationOutcome.Failed(new ComptimeResult(ComptimeFailureKind.NotEvaluable, whileStatement.Condition.Span, "while-statement conditions must be bool."));

            if (!conditionBool)
                break;

            EvaluationOutcome outcome = TryExecuteBlock(whileStatement.Body, frame);
            if (outcome.Kind == EvaluationOutcomeKind.Failed || outcome.Kind == EvaluationOutcomeKind.Return)
                return outcome;

            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;

            ComptimeResult fuel = SpendFuel(whileStatement.Span);
            if (fuel.IsFailed)
                return EvaluationOutcome.Failed(fuel);
        }

        return EvaluationOutcome.None;
    }

    private EvaluationOutcome TryExecuteForStatement(
        BoundForStatement forStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        ComptimeResult iterableValue = TryEvaluateExpression(forStatement.Iterable, frame);
        if (iterableValue.IsFailed)
            return EvaluationOutcome.Failed(iterableValue);

        ComptimeResult iterableCount = TryConvertToInt64(iterableValue, forStatement.Iterable.Span, "for loop iterable must be a compile-time integer.");
        if (iterableCount.IsFailed)
            return EvaluationOutcome.Failed(iterableCount);

        iterableCount.TryGetLong(out long count);
        for (long index = 0; index < count; index++)
        {
            if (forStatement.IndexVariable is not null)
            {
                ComptimeResult loopIndex = NormalizeLiteral(new ComptimeResult(index), forStatement.IndexVariable.Type, forStatement.Iterable.Span);
                if (loopIndex.IsFailed)
                    return EvaluationOutcome.Failed(loopIndex);

                frame[forStatement.IndexVariable] = loopIndex;
            }

            EvaluationOutcome outcome = TryExecuteBlock(forStatement.Body, frame);
            if (outcome.Kind == EvaluationOutcomeKind.Failed || outcome.Kind == EvaluationOutcomeKind.Return)
                return outcome;

            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;
        }

        return EvaluationOutcome.None;
    }

    private EvaluationOutcome TryExecuteLoopStatement(
        BoundLoopStatement loopStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        while (true)
        {
            EvaluationOutcome outcome = TryExecuteBlock(loopStatement.Body, frame);
            if (outcome.Kind == EvaluationOutcomeKind.Failed || outcome.Kind == EvaluationOutcomeKind.Return)
                return outcome;

            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;

            ComptimeResult fuel = SpendFuel(loopStatement.Span);
            if (fuel.IsFailed)
                return EvaluationOutcome.Failed(fuel);
        }

        return EvaluationOutcome.None;
    }

    private EvaluationOutcome TryExecuteRepLoopStatement(
        BoundRepLoopStatement repLoopStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        while (true)
        {
            EvaluationOutcome outcome = TryExecuteBlock(repLoopStatement.Body, frame);
            if (outcome.Kind == EvaluationOutcomeKind.Failed || outcome.Kind == EvaluationOutcomeKind.Return)
                return outcome;

            Assert.Invariant(outcome.Kind != EvaluationOutcomeKind.Break);

            ComptimeResult fuel = SpendFuel(repLoopStatement.Span);
            if (fuel.IsFailed)
                return EvaluationOutcome.Failed(fuel);
        }
    }

    private EvaluationOutcome TryExecuteRepForStatement(
        BoundRepForStatement repForStatement,
        Dictionary<Symbol, ComptimeResult> frame)
    {
        ComptimeResult startValue = TryEvaluateExpression(repForStatement.Start, frame);
        if (startValue.IsFailed)
            return EvaluationOutcome.Failed(startValue);

        ComptimeResult endValue = TryEvaluateExpression(repForStatement.End, frame);
        if (endValue.IsFailed)
            return EvaluationOutcome.Failed(endValue);

        ComptimeResult normalizedStart = TryConvertToInt64(startValue, repForStatement.Start.Span, "rep for bounds must be compile-time integers.");
        if (normalizedStart.IsFailed)
            return EvaluationOutcome.Failed(normalizedStart);

        ComptimeResult normalizedEnd = TryConvertToInt64(endValue, repForStatement.End.Span, "rep for bounds must be compile-time integers.");
        if (normalizedEnd.IsFailed)
            return EvaluationOutcome.Failed(normalizedEnd);

        normalizedStart.TryGetLong(out long start);
        normalizedEnd.TryGetLong(out long end);

        for (long index = start; index < end; index++)
        {
            ComptimeResult loopIndex = NormalizeLiteral(new ComptimeResult(index), repForStatement.Variable.Type, repForStatement.Start.Span);
            if (loopIndex.IsFailed)
                return EvaluationOutcome.Failed(loopIndex);

            frame[repForStatement.Variable] = loopIndex;
            EvaluationOutcome outcome = TryExecuteBlock(repForStatement.Body, frame);
            if (outcome.Kind == EvaluationOutcomeKind.Failed || outcome.Kind == EvaluationOutcomeKind.Return)
                return outcome;

            if (outcome.Kind == EvaluationOutcomeKind.Break)
                break;
        }

        return EvaluationOutcome.None;
    }

    private ComptimeResult SpendFuel(TextSpan span)
    {
        Fuel--;
        return Fuel >= 0
            ? ComptimeResult.Void
            : new ComptimeResult(ComptimeFailureKind.FuelExhausted, span, "comptime evaluation ran out of fuel.");
    }

    private static ComptimeResult NormalizeLiteral(ComptimeResult value, TypeSymbol targetType, TextSpan span)
    {
        if (targetType is VoidTypeSymbol)
            return ComptimeResult.Void;

        if (value.IsUndefined)
            return ComptimeResult.Undefined;

        if (targetType is UnknownTypeSymbol)
            return value;

        if (targetType is UndefinedLiteralTypeSymbol)
        {
            return value.IsUndefined
                ? ComptimeResult.Undefined
                : new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");
        }

        if (targetType is EnumTypeSymbol enumType)
            return NormalizeLiteral(value, enumType.BackingType, span);

        if (ReferenceEquals(targetType, BuiltinTypes.Bool))
        {
            if (value.TryGetBool(out bool boolValue))
                return boolValue ? ComptimeResult.True : ComptimeResult.False;

            if (value.TryConvertToLong(out long integerBoolValue))
                return integerBoolValue != 0 ? ComptimeResult.True : ComptimeResult.False;

            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");
        }

        if (ReferenceEquals(targetType, BuiltinTypes.IntegerLiteral))
        {
            if (!value.TryConvertToLong(out long integerLiteral))
                return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");

            return new ComptimeResult(integerLiteral);
        }

        if (value.TryGetInt(out int intValue))
            return NormalizeConcreteLiteral(intValue, targetType, span);

        if (value.TryGetUInt(out uint uintValue))
            return NormalizeConcreteLiteral(uintValue, targetType, span);

        if (value.TryGetLong(out long longValue))
            return NormalizeConcreteLiteral(longValue, targetType, span);

        return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");
    }

    private static ComptimeResult NormalizeConcreteLiteral<T>(T rawValue, TypeSymbol targetType, TextSpan span)
    {
        if (!ComptimeTypeFacts.TryNormalizeValue(rawValue, targetType, out object? normalizedValue))
            return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, $"value cannot be normalized to '{targetType.Name}'.");

        return CreateValueResult(normalizedValue, targetType);
    }

    private static ComptimeResult CreateValueResult(object? value, TypeSymbol type)
    {
        return value switch
        {
            null when type is VoidTypeSymbol => ComptimeResult.Void,
            null => ComptimeResult.Undefined,
            bool boolValue => boolValue ? ComptimeResult.True : ComptimeResult.False,
            long longValue => new ComptimeResult(longValue),
            int intValue => new ComptimeResult(intValue),
            uint uintValue => new ComptimeResult(uintValue),
            sbyte sbyteValue => new ComptimeResult((int)sbyteValue),
            byte byteValue => new ComptimeResult((uint)byteValue),
            short shortValue => new ComptimeResult((int)shortValue),
            ushort ushortValue => new ComptimeResult((uint)ushortValue),
            ulong ulongValue when ulongValue <= uint.MaxValue => new ComptimeResult((uint)ulongValue),
            ulong ulongValue when ulongValue <= long.MaxValue => new ComptimeResult((long)ulongValue),
            string stringValue => new ComptimeResult(stringValue),
            _ => Assert.UnreachableValue<ComptimeResult>($"Unsupported comptime value '{value}'."),
        };
    }

    private static ComptimeResult FailNotEvaluable(TextSpan span, string detail)
    {
        return new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, detail);
    }

    private static ComptimeResult TryConvertToInt64(ComptimeResult value, TextSpan span, string detail)
    {
        return value.TryConvertToLong(out long converted)
            ? new ComptimeResult(converted)
            : new ComptimeResult(ComptimeFailureKind.NotEvaluable, span, detail);
    }

    private enum EvaluationOutcomeKind
    {
        None,
        Return,
        Break,
        Continue,
        Failed,
    }

    private readonly record struct EvaluationOutcome(EvaluationOutcomeKind Kind, ComptimeResult Value)
    {
        public static EvaluationOutcome None => new(EvaluationOutcomeKind.None, ComptimeResult.Void);
        public static EvaluationOutcome Break => new(EvaluationOutcomeKind.Break, ComptimeResult.Void);
        public static EvaluationOutcome Continue => new(EvaluationOutcomeKind.Continue, ComptimeResult.Void);
        public static EvaluationOutcome Return(ComptimeResult value) => new(EvaluationOutcomeKind.Return, value);
        public static EvaluationOutcome Failed(ComptimeResult failure)
        {
            Assert.Invariant(failure.IsFailed);
            return new EvaluationOutcome(EvaluationOutcomeKind.Failed, failure);
        }
    }
}
