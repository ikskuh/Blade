using System;
using System.Collections.Generic;
using Blade;

namespace Blade.Semantics;

public abstract class TypeSymbol(string name)
{
    public string Name { get; } = Requires.NotNull(name);

    public abstract bool IsLegalRuntimeObject(object value);

    public override string ToString() => Name;
}

public abstract class RuntimeTypeSymbol(string name) : TypeSymbol(name)
{
    public abstract int SizeBytes { get; }
    public abstract int AlignmentBytes { get; }

    public virtual int? ScalarWidthBits => null;
    public virtual bool IsScalarCastType => ScalarWidthBits is not null;
    public virtual bool IsSignedInteger => false;
    public virtual int? BitfieldFieldWidthBits => ScalarWidthBits;

    public int GetSizeInMemorySpace(VariableStorageClass storageClass)
    {
        return storageClass is VariableStorageClass.Reg or VariableStorageClass.Lut
            ? Math.Max(1, (SizeBytes + 3) / 4)
            : SizeBytes;
    }

    public int GetAlignmentInMemorySpace(VariableStorageClass storageClass)
    {
        return storageClass is VariableStorageClass.Reg or VariableStorageClass.Lut
            ? 1
            : AlignmentBytes;
    }

    public int GetPointerElementStride(VariableStorageClass storageClass)
    {
        int stride = GetSizeInMemorySpace(storageClass);
        Assert.Invariant(stride > 0, $"Runtime type '{Name}' must have positive storage size.");
        return stride;
    }
}

public abstract class ComptimeTypeSymbol(string name) : TypeSymbol(name)
{
    public override bool IsLegalRuntimeObject(object value) => false;
}

public abstract class ScalarTypeSymbol(string name, int bitWidth, int sizeBytes, int alignmentBytes) : RuntimeTypeSymbol(name)
{
    public int BitWidth { get; } = Requires.Positive(bitWidth);
    public override int SizeBytes { get; } = Requires.Positive(sizeBytes);
    public override int AlignmentBytes { get; } = Requires.Positive(alignmentBytes);
    public override int? ScalarWidthBits => BitWidth;
}

public sealed class BoolTypeSymbol() : ScalarTypeSymbol("bool", bitWidth: 1, sizeBytes: 1, alignmentBytes: 1)
{
    public override bool IsScalarCastType => false;

    public override bool IsLegalRuntimeObject(object value) => value is bool;
}

public sealed class IntegerTypeSymbol(string name, int bitWidth, bool isSigned)
    : ScalarTypeSymbol(name, bitWidth, sizeBytes: Math.Max(1, (bitWidth + 7) / 8), alignmentBytes: Math.Max(1, (bitWidth + 7) / 8))
{
    public override bool IsSignedInteger { get; } = isSigned;
    public long MinValue { get; } = isSigned ? -(1L << (bitWidth - 1)) : 0L;
    public long MaxValue { get; } = isSigned ? (1L << (bitWidth - 1)) - 1L : (1L << bitWidth) - 1L;

    public override bool IsLegalRuntimeObject(object value) => (value is long l) && IsLegalRuntimeObject(l);

    private bool IsLegalRuntimeObject(long value) => value >= MinValue && value <= MaxValue;
}

public abstract class PointerLikeTypeSymbol(
    string prefix,
    TypeSymbol pointeeType,
    bool isConst,
    bool isVolatile,
    int? alignment,
    VariableStorageClass storageClass)
    : ScalarTypeSymbol(BuildName(prefix, pointeeType, isConst, isVolatile, alignment, storageClass), bitWidth: 32, sizeBytes: 4, alignmentBytes: 4)
{
    public RuntimeTypeSymbol PointeeType { get; } = pointeeType as RuntimeTypeSymbol
        ?? throw new ArgumentException("Pointer pointee type must be a runtime type.", nameof(pointeeType));

    public bool IsConst { get; } = isConst;
    public bool IsVolatile { get; } = isVolatile;
    public int? Alignment { get; } = alignment;
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override bool IsLegalRuntimeObject(object value) => value is uint;

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

public sealed class PointerTypeSymbol(
    TypeSymbol pointeeType,
    bool isConst,
    bool isVolatile = false,
    int? alignment = null,
    VariableStorageClass storageClass = VariableStorageClass.Automatic)
    : PointerLikeTypeSymbol("*", pointeeType, isConst, isVolatile, alignment, storageClass);

public sealed class MultiPointerTypeSymbol(
    TypeSymbol pointeeType,
    bool isConst,
    bool isVolatile = false,
    int? alignment = null,
    VariableStorageClass storageClass = VariableStorageClass.Automatic)
    : PointerLikeTypeSymbol("[*]", pointeeType, isConst, isVolatile, alignment, storageClass);

public sealed class ArrayTypeSymbol(TypeSymbol elementType, int? length = null) : RuntimeTypeSymbol(BuildName(elementType, length))
{
    public TypeSymbol ElementType { get; } = Requires.NotNull(elementType);

    public int? Length { get; } = length;

    public override int SizeBytes => GetRuntimeElementType().SizeBytes * (Length ?? 1);
    public override int AlignmentBytes => GetRuntimeElementType().AlignmentBytes;

    public override bool IsLegalRuntimeObject(object value)
    {
        if (value is not IReadOnlyList<RuntimeBladeValue> values)
            return false;

        if (Length is int knownLength && values.Count != knownLength)
            return false;

        foreach (RuntimeBladeValue element in values)
        {
            if (!ReferenceEquals(element.Type, GetRuntimeElementType()))
                return false;
        }

        return true;
    }

    private static string BuildName(TypeSymbol elementType, int? length)
    {
        return length is int knownLength
            ? $"[{knownLength}]{Requires.NotNull(elementType).Name}"
            : $"[{Requires.NotNull(elementType).Name}]";
    }

    private RuntimeTypeSymbol GetRuntimeElementType()
    {
        return ElementType as RuntimeTypeSymbol
            ?? throw new InvalidOperationException($"Array type '{Name}' does not have a runtime element type.");
    }
}

public sealed class AggregateMemberSymbol(string name, TypeSymbol type, int byteOffset, int bitOffset, int bitWidth, bool isBitfield)
{
    public string Name { get; } = Requires.NotNull(name);
    public TypeSymbol Type { get; } = Requires.NotNull(type);
    public int ByteOffset { get; } = byteOffset;
    public int BitOffset { get; } = bitOffset;
    public int BitWidth { get; } = bitWidth;
    public bool IsBitfield { get; } = isBitfield;
}

public abstract class AggregateTypeSymbol(
    string name,
    IReadOnlyDictionary<string, TypeSymbol> fields,
    IReadOnlyDictionary<string, AggregateMemberSymbol> members,
    int sizeBytes,
    int alignmentBytes)
    : RuntimeTypeSymbol(name)
{
    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; } = Requires.NotNull(fields);
    public IReadOnlyDictionary<string, AggregateMemberSymbol> Members { get; } = Requires.NotNull(members);
    public override int SizeBytes { get; } = sizeBytes;
    public override int AlignmentBytes { get; } = alignmentBytes;

    public override bool IsLegalRuntimeObject(object value)
    {
        if (value is not IReadOnlyDictionary<string, BladeValue> values)
            return false;

        foreach ((string fieldName, TypeSymbol fieldType) in Fields)
        {
            if (!values.TryGetValue(fieldName, out BladeValue? fieldValue))
                return false;

            if (!ReferenceEquals(fieldValue.Type, fieldType))
                return false;
        }

        return true;
    }
}

public sealed class StructTypeSymbol(
    string name,
    IReadOnlyDictionary<string, TypeSymbol> fields,
    IReadOnlyDictionary<string, AggregateMemberSymbol> members,
    int sizeBytes,
    int alignmentBytes)
    : AggregateTypeSymbol(name, fields, members, sizeBytes, alignmentBytes);

public sealed class UnionTypeSymbol(
    string name,
    IReadOnlyDictionary<string, TypeSymbol> fields,
    IReadOnlyDictionary<string, AggregateMemberSymbol> members,
    int sizeBytes,
    int alignmentBytes)
    : AggregateTypeSymbol(name, fields, members, sizeBytes, alignmentBytes);

public sealed class EnumTypeSymbol(string name, TypeSymbol backingType, IReadOnlyDictionary<string, long> members, bool isOpen) : RuntimeTypeSymbol(name)
{
    public RuntimeTypeSymbol BackingType { get; } = backingType as RuntimeTypeSymbol
        ?? throw new ArgumentException("Enum backing type must be a runtime type.", nameof(backingType));

    public IReadOnlyDictionary<string, long> Members { get; } = Requires.NotNull(members);
    public bool IsOpen { get; } = isOpen;

    public override int SizeBytes => BackingType.SizeBytes;
    public override int AlignmentBytes => BackingType.AlignmentBytes;
    public override int? ScalarWidthBits => BackingType.ScalarWidthBits;
    public override bool IsScalarCastType => BackingType.IsScalarCastType;
    public override bool IsSignedInteger => BackingType.IsSignedInteger;
    public override int? BitfieldFieldWidthBits => BackingType.BitfieldFieldWidthBits;

    public override bool IsLegalRuntimeObject(object value) => BackingType.IsLegalRuntimeObject(value);
}

public sealed class BitfieldTypeSymbol(
    string name,
    TypeSymbol backingType,
    IReadOnlyDictionary<string, TypeSymbol> fields,
    IReadOnlyDictionary<string, AggregateMemberSymbol> members)
    : RuntimeTypeSymbol(name)
{
    public RuntimeTypeSymbol BackingType { get; } = backingType as RuntimeTypeSymbol
        ?? throw new ArgumentException("Bitfield backing type must be a runtime type.", nameof(backingType));

    public IReadOnlyDictionary<string, TypeSymbol> Fields { get; } = Requires.NotNull(fields);
    public IReadOnlyDictionary<string, AggregateMemberSymbol> Members { get; } = Requires.NotNull(members);

    public override int SizeBytes => BackingType.SizeBytes;
    public override int AlignmentBytes => BackingType.AlignmentBytes;
    public override int? ScalarWidthBits => BackingType.ScalarWidthBits;
    public override bool IsScalarCastType => BackingType.IsScalarCastType;
    public override bool IsSignedInteger => BackingType.IsSignedInteger;

    public override int? BitfieldFieldWidthBits => BackingType switch
    {
        BoolTypeSymbol => 1,
        IntegerTypeSymbol integer => integer.BitWidth,
        EnumTypeSymbol enumType => enumType.BitfieldFieldWidthBits,
        _ => null,
    };

    public override bool IsLegalRuntimeObject(object value) => BackingType.IsLegalRuntimeObject(value);
}

public sealed class FunctionTypeSymbol(FunctionSymbol function) : ComptimeTypeSymbol($"fn {Requires.NotNull(function).Name}")
{
    public FunctionSymbol Function { get; } = Requires.NotNull(function);

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, Function);
}

public sealed class ModuleTypeSymbol(ModuleSymbol module) : ComptimeTypeSymbol($"module {Requires.NotNull(module).Name}")
{
    public ModuleSymbol Module { get; } = Requires.NotNull(module);

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, Module);
}

public sealed class UnknownTypeSymbol() : ComptimeTypeSymbol("<unknown>");

public sealed class UndefinedLiteralTypeSymbol : ComptimeTypeSymbol
{
    public static UndefinedLiteralTypeSymbol Instance { get; } = new();

    private UndefinedLiteralTypeSymbol()
        : base("undefined")
    {
    }

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, UndefinedValue.Instance);
}

public sealed class IntegerLiteralTypeSymbol() : ComptimeTypeSymbol("<int-literal>")
{
    public override bool IsLegalRuntimeObject(object value) => value is long;
}

public sealed class VoidTypeSymbol : ComptimeTypeSymbol
{
    public static VoidTypeSymbol Instance { get; } = new();

    private VoidTypeSymbol()
        : base("void")
    {
    }

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, VoidValue.Instance);
}

public sealed class StringTypeSymbol() : ComptimeTypeSymbol("string")
{
    public override bool IsLegalRuntimeObject(object value) => value is string;
}

public sealed class RangeTypeSymbol() : ComptimeTypeSymbol("range");

public static class BuiltinTypes
{
    public static readonly ComptimeTypeSymbol Unknown = new UnknownTypeSymbol();
    public static readonly ComptimeTypeSymbol UndefinedLiteral = UndefinedLiteralTypeSymbol.Instance;
    public static readonly ComptimeTypeSymbol IntegerLiteral = new IntegerLiteralTypeSymbol();
    public static readonly BoolTypeSymbol Bool = new();
    public static readonly IntegerTypeSymbol Bit = new("bit", bitWidth: 1, isSigned: false);
    public static readonly IntegerTypeSymbol Nit = new("nit", bitWidth: 1, isSigned: true);
    public static readonly IntegerTypeSymbol Nib = new("nib", bitWidth: 4, isSigned: false);
    public static readonly IntegerTypeSymbol U8 = new("u8", bitWidth: 8, isSigned: false);
    public static readonly IntegerTypeSymbol I8 = new("i8", bitWidth: 8, isSigned: true);
    public static readonly IntegerTypeSymbol U16 = new("u16", bitWidth: 16, isSigned: false);
    public static readonly IntegerTypeSymbol I16 = new("i16", bitWidth: 16, isSigned: true);
    public static readonly IntegerTypeSymbol U32 = new("u32", bitWidth: 32, isSigned: false);
    public static readonly IntegerTypeSymbol I32 = new("i32", bitWidth: 32, isSigned: true);
    public static readonly IntegerTypeSymbol Uint = new("uint", bitWidth: 32, isSigned: false);
    public static readonly IntegerTypeSymbol Int = new("int", bitWidth: 32, isSigned: true);
    public static readonly ComptimeTypeSymbol Void = VoidTypeSymbol.Instance;
    public static readonly ComptimeTypeSymbol String = new StringTypeSymbol();
    public static readonly ComptimeTypeSymbol Range = new RangeTypeSymbol();

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
        ["string"] = String,
        ["range"] = Range,
    };

    public static bool TryGet(string name, out TypeSymbol type)
    {
        return Builtins.TryGetValue(name, out type!);
    }
}
