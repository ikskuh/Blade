using System;
using System.Collections.Generic;
using System.Globalization;
using Blade;

namespace Blade.Semantics;

public abstract class TypeSymbol
{
    protected TypeSymbol(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public virtual bool IsUnknown => false;
    public virtual bool IsUndefinedLiteral => false;
    public virtual bool IsInteger => false;
    public virtual bool IsBool => false;
    public virtual bool IsVoid => false;

    public override string ToString() => Name;
}

public sealed class PrimitiveTypeSymbol : TypeSymbol
{
    public PrimitiveTypeSymbol(string name, bool isInteger = false, bool isBool = false, bool isVoid = false)
        : base(name)
    {
        IsInteger = isInteger;
        IsBool = isBool;
        IsVoid = isVoid;
    }

    public override bool IsInteger { get; }
    public override bool IsBool { get; }
    public override bool IsVoid { get; }
}

public sealed class ArrayTypeSymbol : TypeSymbol
{
    public ArrayTypeSymbol(TypeSymbol elementType)
        : base($"[{Requires.NotNull(elementType).Name}]")
    {
        ElementType = Requires.NotNull(elementType);
    }

    public TypeSymbol ElementType { get; }
}

public sealed class PointerTypeSymbol : TypeSymbol
{
    public PointerTypeSymbol(TypeSymbol pointeeType, bool isConst, VariableStorageClass storageClass = VariableStorageClass.Automatic)
        : base(BuildName(pointeeType, isConst, storageClass))
    {
        PointeeType = Requires.NotNull(pointeeType);
        IsConst = isConst;
        StorageClass = storageClass;
    }

    public TypeSymbol PointeeType { get; }
    public bool IsConst { get; }
    public VariableStorageClass StorageClass { get; }

    private static string BuildName(TypeSymbol pointeeType, bool isConst, VariableStorageClass storageClass)
    {
        string storageText = storageClass switch
        {
            VariableStorageClass.Reg => "reg ",
            VariableStorageClass.Lut => "lut ",
            VariableStorageClass.Hub => "hub ",
            _ => string.Empty,
        };

        return isConst
            ? $"*{storageText}const {Requires.NotNull(pointeeType).Name}"
            : $"*{storageText}{Requires.NotNull(pointeeType).Name}";
    }
}

public sealed class StructTypeSymbol : TypeSymbol
{
    public StructTypeSymbol(string name, IReadOnlyDictionary<string, TypeSymbol> fields)
        : base(name)
    {
        Fields = Requires.NotNull(fields);
    }

    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; }
}

public sealed class FunctionTypeSymbol : TypeSymbol
{
    public FunctionTypeSymbol(FunctionSymbol function)
        : base($"fn {Requires.NotNull(function).Name}")
    {
        Function = Requires.NotNull(function);
    }

    public FunctionSymbol Function { get; }
}


public sealed class ModuleTypeSymbol : TypeSymbol
{
    public ModuleTypeSymbol(ModuleSymbol module)
        : base($"module {Requires.NotNull(module).Name}")
    {
        Module = Requires.NotNull(module);
    }

    public ModuleSymbol Module { get; }
}

public sealed class UnknownTypeSymbol : TypeSymbol
{
    public UnknownTypeSymbol() : base("<unknown>")
    {
    }

    public override bool IsUnknown => true;
}

public sealed class UndefinedLiteralTypeSymbol : TypeSymbol
{
    public UndefinedLiteralTypeSymbol() : base("undefined")
    {
    }

    public override bool IsUndefinedLiteral => true;
}

public sealed class IntegerLiteralTypeSymbol : TypeSymbol
{
    public IntegerLiteralTypeSymbol() : base("<int-literal>")
    {
    }

    public override bool IsInteger => true;
}

public static class BuiltinTypes
{
    public static readonly TypeSymbol Unknown = new UnknownTypeSymbol();
    public static readonly TypeSymbol UndefinedLiteral = new UndefinedLiteralTypeSymbol();
    public static readonly TypeSymbol IntegerLiteral = new IntegerLiteralTypeSymbol();
    public static readonly PrimitiveTypeSymbol Bool = new("bool", isBool: true);
    public static readonly PrimitiveTypeSymbol Bit = new("bit", isInteger: true);
    public static readonly PrimitiveTypeSymbol Nit = new("nit", isInteger: true);
    public static readonly PrimitiveTypeSymbol Nib = new("nib", isInteger: true);
    public static readonly PrimitiveTypeSymbol U8 = new("u8", isInteger: true);
    public static readonly PrimitiveTypeSymbol I8 = new("i8", isInteger: true);
    public static readonly PrimitiveTypeSymbol U16 = new("u16", isInteger: true);
    public static readonly PrimitiveTypeSymbol I16 = new("i16", isInteger: true);
    public static readonly PrimitiveTypeSymbol U32 = new("u32", isInteger: true);
    public static readonly PrimitiveTypeSymbol I32 = new("i32", isInteger: true);
    public static readonly PrimitiveTypeSymbol Uint = new("uint", isInteger: true);
    public static readonly PrimitiveTypeSymbol Int = new("int", isInteger: true);
    public static readonly PrimitiveTypeSymbol Void = new("void", isVoid: true);
    public static readonly PrimitiveTypeSymbol String = new("string");
    public static readonly PrimitiveTypeSymbol Range = new("range");

    private static readonly Dictionary<string, TypeSymbol> Builtins = new()
    {
        ["bool"] = Bool,
        ["bit"] = Bit,
        ["nit"] = Nit,
        ["nib"] = Nib,
        ["u8"] = U8,
        ["i8"] = I8,
        ["u16"] = U16,
        ["i16"] = I16,
        ["u32"] = U32,
        ["i32"] = I32,
        ["uint"] = Uint,
        ["int"] = Int,
        ["void"] = Void,
    };

    public static bool TryGet(string name, out TypeSymbol type)
    {
        return Builtins.TryGetValue(name, out type!);
    }
}

public static class TypeFacts
{
    public static bool TryGetIntegerWidth(TypeSymbol type, out int width)
    {
        width = 0;
        if (ReferenceEquals(type, BuiltinTypes.IntegerLiteral))
        {
            width = 32;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.Bit) || ReferenceEquals(type, BuiltinTypes.Nit))
        {
            width = 1;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.Nib))
        {
            width = 4;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.U8) || ReferenceEquals(type, BuiltinTypes.I8))
        {
            width = 8;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.U16) || ReferenceEquals(type, BuiltinTypes.I16))
        {
            width = 16;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.U32)
            || ReferenceEquals(type, BuiltinTypes.I32)
            || ReferenceEquals(type, BuiltinTypes.Uint)
            || ReferenceEquals(type, BuiltinTypes.Int))
        {
            width = 32;
            return true;
        }

        return false;
    }

    public static bool TryGetScalarWidth(TypeSymbol type, out int width)
    {
        if (type is PointerTypeSymbol)
        {
            width = 32;
            return true;
        }

        return TryGetIntegerWidth(type, out width);
    }

    public static bool IsSignedInteger(TypeSymbol type)
    {
        return ReferenceEquals(type, BuiltinTypes.Nit)
            || ReferenceEquals(type, BuiltinTypes.I8)
            || ReferenceEquals(type, BuiltinTypes.I16)
            || ReferenceEquals(type, BuiltinTypes.I32)
            || ReferenceEquals(type, BuiltinTypes.Int);
    }

    public static bool IsScalarCastType(TypeSymbol type)
    {
        return TryGetScalarWidth(type, out _);
    }

    public static bool TryNormalizeValue(object? value, TypeSymbol targetType, out object? normalized)
    {
        normalized = value;
        if (value is null)
            return true;

        if (!TryGetScalarWidth(targetType, out int width))
            return false;

        uint rawBits = unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        uint maskedBits = width >= 32 ? rawBits : rawBits & ((1u << width) - 1u);

        if (targetType is PointerTypeSymbol)
        {
            normalized = maskedBits;
            return true;
        }

        if (IsSignedInteger(targetType))
        {
            if (width >= 32)
            {
                normalized = unchecked((int)maskedBits);
                return true;
            }

            int shift = 32 - width;
            normalized = (int)(maskedBits << shift) >> shift;
            return true;
        }

        normalized = maskedBits;
        return true;
    }
}
