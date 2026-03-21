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
    public ArrayTypeSymbol(TypeSymbol elementType, int? length = null)
        : base(BuildName(elementType, length))
    {
        ElementType = Requires.NotNull(elementType);
        Length = length;
    }

    public TypeSymbol ElementType { get; }
    public int? Length { get; }

    private static string BuildName(TypeSymbol elementType, int? length)
    {
        return length is int knownLength
            ? $"[{knownLength}]{Requires.NotNull(elementType).Name}"
            : $"[{Requires.NotNull(elementType).Name}]";
    }
}

public sealed class AggregateMemberSymbol
{
    public AggregateMemberSymbol(string name, TypeSymbol type, int byteOffset, int bitOffset, int bitWidth, bool isBitfield)
    {
        Name = Requires.NotNull(name);
        Type = Requires.NotNull(type);
        ByteOffset = byteOffset;
        BitOffset = bitOffset;
        BitWidth = bitWidth;
        IsBitfield = isBitfield;
    }

    public string Name { get; }
    public TypeSymbol Type { get; }
    public int ByteOffset { get; }
    public int BitOffset { get; }
    public int BitWidth { get; }
    public bool IsBitfield { get; }
}

public abstract class PointerLikeTypeSymbol : TypeSymbol
{
    protected PointerLikeTypeSymbol(string prefix, TypeSymbol pointeeType, bool isConst, bool isVolatile, int? alignment, VariableStorageClass storageClass)
        : base(BuildName(prefix, pointeeType, isConst, isVolatile, alignment, storageClass))
    {
        PointeeType = Requires.NotNull(pointeeType);
        IsConst = isConst;
        IsVolatile = isVolatile;
        Alignment = alignment;
        StorageClass = storageClass;
    }

    public TypeSymbol PointeeType { get; }
    public bool IsConst { get; }
    public bool IsVolatile { get; }
    public int? Alignment { get; }
    public VariableStorageClass StorageClass { get; }

    private static string BuildName(
        string prefix,
        TypeSymbol pointeeType,
        bool isConst,
        bool isVolatile,
        int? alignment,
        VariableStorageClass storageClass)
    {
        string storageText = storageClass switch
        {
            VariableStorageClass.Reg => "reg ",
            VariableStorageClass.Lut => "lut ",
            VariableStorageClass.Hub => "hub ",
            _ => string.Empty,
        };

        List<string> parts = [$"{prefix}{storageText}".TrimEnd()];
        if (isConst)
            parts.Add("const");
        if (isVolatile)
            parts.Add("volatile");
        if (alignment is int knownAlignment)
            parts.Add(FormattableString.Invariant($"align({knownAlignment})"));
        parts.Add(Requires.NotNull(pointeeType).Name);
        return string.Join(' ', parts);
    }
}

public sealed class PointerTypeSymbol : PointerLikeTypeSymbol
{
    public PointerTypeSymbol(
        TypeSymbol pointeeType,
        bool isConst,
        bool isVolatile = false,
        int? alignment = null,
        VariableStorageClass storageClass = VariableStorageClass.Automatic)
        : base("*", pointeeType, isConst, isVolatile, alignment, storageClass)
    {
    }
}

public sealed class MultiPointerTypeSymbol : PointerLikeTypeSymbol
{
    public MultiPointerTypeSymbol(
        TypeSymbol pointeeType,
        bool isConst,
        bool isVolatile = false,
        int? alignment = null,
        VariableStorageClass storageClass = VariableStorageClass.Automatic)
        : base("[*]", pointeeType, isConst, isVolatile, alignment, storageClass)
    {
    }
}

public sealed class StructTypeSymbol : TypeSymbol
{
    public StructTypeSymbol(
        string name,
        IReadOnlyDictionary<string, TypeSymbol> fields,
        IReadOnlyDictionary<string, AggregateMemberSymbol>? members = null,
        int sizeBytes = 0,
        int alignmentBytes = 1)
        : base(name)
    {
        Fields = Requires.NotNull(fields);
        Members = members ?? BuildMembers(fields);
        SizeBytes = sizeBytes;
        AlignmentBytes = alignmentBytes;
    }

    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; }
    public IReadOnlyDictionary<string, AggregateMemberSymbol> Members { get; }
    public int SizeBytes { get; }
    public int AlignmentBytes { get; }

    private static IReadOnlyDictionary<string, AggregateMemberSymbol> BuildMembers(IReadOnlyDictionary<string, TypeSymbol> fields)
    {
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal);
        foreach ((string fieldName, TypeSymbol fieldType) in fields)
            members[fieldName] = new AggregateMemberSymbol(fieldName, fieldType, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false);
        return members;
    }
}

public sealed class UnionTypeSymbol : TypeSymbol
{
    public UnionTypeSymbol(
        string name,
        IReadOnlyDictionary<string, TypeSymbol> fields,
        IReadOnlyDictionary<string, AggregateMemberSymbol> members,
        int sizeBytes,
        int alignmentBytes)
        : base(name)
    {
        Fields = Requires.NotNull(fields);
        Members = Requires.NotNull(members);
        SizeBytes = sizeBytes;
        AlignmentBytes = alignmentBytes;
    }

    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; }
    public IReadOnlyDictionary<string, AggregateMemberSymbol> Members { get; }
    public int SizeBytes { get; }
    public int AlignmentBytes { get; }
}

public sealed class EnumTypeSymbol : TypeSymbol
{
    public EnumTypeSymbol(
        string name,
        TypeSymbol backingType,
        IReadOnlyDictionary<string, long> members,
        bool isOpen)
        : base(name)
    {
        BackingType = Requires.NotNull(backingType);
        Members = Requires.NotNull(members);
        IsOpen = isOpen;
    }

    public TypeSymbol BackingType { get; }
    public IReadOnlyDictionary<string, long> Members { get; }
    public bool IsOpen { get; }
}

public sealed class BitfieldTypeSymbol : TypeSymbol
{
    public BitfieldTypeSymbol(
        string name,
        TypeSymbol backingType,
        IReadOnlyDictionary<string, TypeSymbol> fields,
        IReadOnlyDictionary<string, AggregateMemberSymbol> members)
        : base(name)
    {
        BackingType = Requires.NotNull(backingType);
        Fields = Requires.NotNull(fields);
        Members = Requires.NotNull(members);
    }

    public TypeSymbol BackingType { get; }
    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; }
    public IReadOnlyDictionary<string, AggregateMemberSymbol> Members { get; }
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
        if (type is EnumTypeSymbol enumType)
            return TryGetIntegerWidth(enumType.BackingType, out width);

        if (type is BitfieldTypeSymbol bitfieldType)
            return TryGetIntegerWidth(bitfieldType.BackingType, out width);

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
        if (type is PointerLikeTypeSymbol)
        {
            width = 32;
            return true;
        }

        return TryGetIntegerWidth(type, out width);
    }

    public static bool TryGetBitfieldFieldWidth(TypeSymbol type, out int width)
    {
        if (ReferenceEquals(type, BuiltinTypes.Bool))
        {
            width = 1;
            return true;
        }

        return TryGetIntegerWidth(type, out width);
    }

    public static bool TryGetAlignmentBytes(TypeSymbol type, out int alignmentBytes)
    {
        if (type is StructTypeSymbol structType)
        {
            alignmentBytes = Math.Max(1, structType.AlignmentBytes);
            return true;
        }

        if (type is UnionTypeSymbol unionType)
        {
            alignmentBytes = Math.Max(1, unionType.AlignmentBytes);
            return true;
        }

        if (type is BitfieldTypeSymbol bitfieldType)
            return TryGetAlignmentBytes(bitfieldType.BackingType, out alignmentBytes);

        if (type is EnumTypeSymbol enumType)
            return TryGetAlignmentBytes(enumType.BackingType, out alignmentBytes);

        if (type is PointerLikeTypeSymbol)
        {
            alignmentBytes = 4;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.Bool)
            || ReferenceEquals(type, BuiltinTypes.Bit)
            || ReferenceEquals(type, BuiltinTypes.Nit)
            || ReferenceEquals(type, BuiltinTypes.Nib)
            || ReferenceEquals(type, BuiltinTypes.U8)
            || ReferenceEquals(type, BuiltinTypes.I8))
        {
            alignmentBytes = 1;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.U16) || ReferenceEquals(type, BuiltinTypes.I16))
        {
            alignmentBytes = 2;
            return true;
        }

        if (TryGetIntegerWidth(type, out int width))
        {
            alignmentBytes = Math.Max(1, width / 8);
            return true;
        }

        if (type is ArrayTypeSymbol array
            && array.Length is int knownLength
            && TryGetAlignmentBytes(array.ElementType, out alignmentBytes)
            && knownLength >= 0)
        {
            return true;
        }

        alignmentBytes = 0;
        return false;
    }

    public static bool TryGetSizeBytes(TypeSymbol type, out int sizeBytes)
    {
        if (type is StructTypeSymbol structType)
        {
            sizeBytes = Math.Max(0, structType.SizeBytes);
            return true;
        }

        if (type is UnionTypeSymbol unionType)
        {
            sizeBytes = Math.Max(0, unionType.SizeBytes);
            return true;
        }

        if (type is BitfieldTypeSymbol bitfieldType)
            return TryGetSizeBytes(bitfieldType.BackingType, out sizeBytes);

        if (type is EnumTypeSymbol enumType)
            return TryGetSizeBytes(enumType.BackingType, out sizeBytes);

        if (type is PointerLikeTypeSymbol)
        {
            sizeBytes = 4;
            return true;
        }

        if (type is ArrayTypeSymbol array
            && array.Length is int knownLength
            && TryGetSizeBytes(array.ElementType, out int elementSize))
        {
            sizeBytes = elementSize * knownLength;
            return true;
        }

        if (ReferenceEquals(type, BuiltinTypes.Bool))
        {
            sizeBytes = 1;
            return true;
        }

        if (TryGetIntegerWidth(type, out int width))
        {
            sizeBytes = Math.Max(1, (width + 7) / 8);
            return true;
        }

        sizeBytes = 0;
        return false;
    }

    public static bool TryGetSizeInMemorySpace(TypeSymbol type, VariableStorageClass storageClass, out int size)
    {
        if (storageClass is VariableStorageClass.Reg or VariableStorageClass.Lut)
        {
            if (!TryGetSizeBytes(type, out int sizeBytes))
            {
                size = 0;
                return false;
            }

            size = Math.Max(1, (sizeBytes + 3) / 4);
            return true;
        }

        return TryGetSizeBytes(type, out size);
    }

    public static bool TryGetAlignmentInMemorySpace(TypeSymbol type, VariableStorageClass storageClass, out int alignment)
    {
        if (storageClass is VariableStorageClass.Reg or VariableStorageClass.Lut)
        {
            if (!TryGetSizeBytes(type, out _))
            {
                alignment = 0;
                return false;
            }

            alignment = 1;
            return true;
        }

        return TryGetAlignmentBytes(type, out alignment);
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

        if (value is string)
            return false;

        if (!TryGetScalarWidth(targetType, out int width))
            return false;

        uint rawBits = unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        uint maskedBits = width >= 32 ? rawBits : rawBits & ((1u << width) - 1u);

        if (targetType is PointerLikeTypeSymbol)
        {
            normalized = maskedBits;
            return true;
        }

        if (targetType is EnumTypeSymbol enumType)
            return TryNormalizeValue(value, enumType.BackingType, out normalized);

        if (targetType is BitfieldTypeSymbol bitfieldType)
            return TryNormalizeValue(value, bitfieldType.BackingType, out normalized);

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
