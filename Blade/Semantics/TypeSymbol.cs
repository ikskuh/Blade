using System.Collections.Generic;
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
    public PointerTypeSymbol(TypeSymbol pointeeType, bool isConst)
        : base(isConst
            ? $"*const {Requires.NotNull(pointeeType).Name}"
            : $"*{Requires.NotNull(pointeeType).Name}")
    {
        PointeeType = Requires.NotNull(pointeeType);
        IsConst = isConst;
    }

    public TypeSymbol PointeeType { get; }
    public bool IsConst { get; }
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
