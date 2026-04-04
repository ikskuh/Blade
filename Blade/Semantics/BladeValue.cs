using System;
using System.Diagnostics.CodeAnalysis;
using Blade;

namespace Blade.Semantics;

public sealed class PointedValue(Symbol symbol, int offset)
{
    public Symbol Symbol { get; } = Requires.NotNull(symbol);
    public int Offset { get; } = offset;

    public PointedValue WithOffset(int offset)
    {
        return new PointedValue(Symbol, offset);
    }
}

public abstract partial class BladeValue(TypeSymbol type, object value)
{
    public static ComptimeBladeValue Void { get; } = new((ComptimeTypeSymbol)BuiltinTypes.Void, VoidValue.Instance);
    public static ComptimeBladeValue Undefined { get; } = new((ComptimeTypeSymbol)BuiltinTypes.UndefinedLiteral, UndefinedValue.Instance);

    public TypeSymbol Type { get; } = Requires.NotNull(type);
    public object Value { get; } = ValidateValue(type, value);

    public bool IsVoid => ReferenceEquals(Type, BuiltinTypes.Void);
    public bool IsUndefined => ReferenceEquals(Type, BuiltinTypes.UndefinedLiteral);

    private static object ValidateValue(TypeSymbol type, object value)
    {
        Requires.NotNull(type);
        Requires.NotNull(value);

        if (!type.IsLegalRuntimeObject(value))
            throw new ArgumentException($"Value '{value}' is not legal for type '{type.Name}'.", nameof(value));

        return value;
    }

    public static RuntimeBladeValue Bool(bool value) => new(BuiltinTypes.Bool, value);
    public static RuntimeBladeValue Bit(long value) => new(BuiltinTypes.Bit, value);
    public static RuntimeBladeValue Nit(long value) => new(BuiltinTypes.Nit, value);
    public static RuntimeBladeValue Nib(long value) => new(BuiltinTypes.Nib, value);
    public static RuntimeBladeValue U8(long value) => new(BuiltinTypes.U8, value);
    public static RuntimeBladeValue I8(long value) => new(BuiltinTypes.I8, value);
    public static RuntimeBladeValue U16(long value) => new(BuiltinTypes.U16, value);
    public static RuntimeBladeValue I16(long value) => new(BuiltinTypes.I16, value);
    public static RuntimeBladeValue U32(long value) => new(BuiltinTypes.U32, value);
    public static RuntimeBladeValue I32(long value) => new(BuiltinTypes.I32, value);
    [SuppressMessage("Design", "CA1720:Identifier contains type name", Justification = "Matches the builtin Blade type name.")]
    public static RuntimeBladeValue Uint(long value) => new(BuiltinTypes.Uint, value);
    [SuppressMessage("Design", "CA1720:Identifier contains type name", Justification = "Matches the builtin Blade type name.")]
    public static RuntimeBladeValue Int(long value) => new(BuiltinTypes.Int, value);
    public static ComptimeBladeValue IntegerLiteral(long value) => new((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, value);
    [SuppressMessage("Design", "CA1720:Identifier contains type name", Justification = "Matches the builtin Blade type name.")]
    public static ComptimeBladeValue String(string value) => new((ComptimeTypeSymbol)BuiltinTypes.String, Requires.NotNull(value));
    [SuppressMessage("Design", "CA1720:Identifier contains type name", Justification = "Pointer values are modeled explicitly in BladeValue.")]
    public static RuntimeBladeValue Pointer(PointerLikeTypeSymbol type, PointedValue value) => new(Requires.NotNull(type), Requires.NotNull(value));

    public bool TryGetBool(out bool result)
    {
        if (Value is bool boolValue)
        {
            result = boolValue;
            return true;
        }

        result = false;
        return false;
    }

    public bool TryGetInteger(out long result)
    {
        if (Value is long integerValue)
        {
            result = integerValue;
            return true;
        }

        result = 0L;
        return false;
    }

    public bool TryGetPointedValue(out PointedValue result)
    {
        if (Value is PointedValue pointedValue)
        {
            result = pointedValue;
            return true;
        }

        result = null!;
        return false;
    }

    public bool TryGetString(out string result)
    {
        if (Value is string stringValue)
        {
            result = stringValue;
            return true;
        }

        result = string.Empty;
        return false;
    }

    public string Format()
    {
        return Value switch
        {
            bool boolean => boolean ? "true" : "false",
            string text => $"\"{text}\"",
            PointedValue pointedValue => FormatPointedValue(pointedValue),
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? "<?>",
        };
    }

    private static string FormatPointedValue(PointedValue pointedValue)
    {
        string baseText = $"&{pointedValue.Symbol.Name}";
        if (pointedValue.Offset == 0)
            return baseText;

        if (pointedValue.Offset > 0)
            return $"{baseText}+{pointedValue.Offset}";

        return $"{baseText}{pointedValue.Offset}";
    }
}

public sealed class RuntimeBladeValue(RuntimeTypeSymbol type, object value) : BladeValue(type, value)
{
    public new RuntimeTypeSymbol Type => (RuntimeTypeSymbol)base.Type;
}

public sealed class ComptimeBladeValue(ComptimeTypeSymbol type, object value) : BladeValue(type, value)
{
    public new ComptimeTypeSymbol Type => (ComptimeTypeSymbol)base.Type;
}

public sealed class VoidValue
{
    public static VoidValue Instance { get; } = new();

    private VoidValue()
    {
    }
}

public sealed class UndefinedValue
{
    public static UndefinedValue Instance { get; } = new();

    private UndefinedValue()
    {
    }
}
