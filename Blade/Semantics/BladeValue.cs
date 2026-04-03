using System;
using Blade;

namespace Blade.Semantics;

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
    public static ComptimeBladeValue IntegerLiteral(long value) => new((ComptimeTypeSymbol)BuiltinTypes.IntegerLiteral, value);
    public static ComptimeBladeValue StringLiteral(string value) => new((ComptimeTypeSymbol)BuiltinTypes.String, Requires.NotNull(value));
    public static RuntimeBladeValue PointerValue(PointerLikeTypeSymbol type, uint value) => new(Requires.NotNull(type), value);

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

    public bool TryGetPointer(out uint result)
    {
        if (Value is uint pointerValue)
        {
            result = pointerValue;
            return true;
        }

        result = 0U;
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
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? "<?>",
        };
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
