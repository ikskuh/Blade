using System;
using Blade;

namespace Blade.Semantics;

public abstract class BladeValue(TypeSymbol type, object value)
{
    public static ComptimeBladeValue Void { get; } = new((ComptimeTypeSymbol)BuiltinTypes.Void, VoidValue.Instance);
    public static ComptimeBladeValue Undefined { get; } = new((ComptimeTypeSymbol)BuiltinTypes.UndefinedLiteral, UndefinedValue.Instance);

    public TypeSymbol Type { get; } = Requires.NotNull(type);
    public object Value { get; } = ValidateValue(type, value);

    private static object ValidateValue(TypeSymbol type, object value)
    {
        Requires.NotNull(type);
        Requires.NotNull(value);

        if (!type.IsLegalRuntimeObject(value))
            throw new ArgumentException($"Value '{value}' is not legal for type '{type.Name}'.", nameof(value));

        return value;
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
