using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Blade;

namespace Blade.Semantics;

public abstract class TypeSymbol(string name) : IEquatable<TypeSymbol>
{
    public string Name { get; } = Requires.NotNull(name);

    public abstract bool IsLegalRuntimeObject(object value);

    public bool Equals(TypeSymbol? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (GetType() != other.GetType())
            return false;

        return EqualsCore(other);
    }

    public sealed override bool Equals(object? obj) => obj is TypeSymbol other && Equals(other);

    public sealed override int GetHashCode() => GetHashCodeCore();

    public static bool operator ==(TypeSymbol? left, TypeSymbol? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(TypeSymbol? left, TypeSymbol? right) => !(left == right);

    protected abstract bool EqualsCore(TypeSymbol other);
    protected abstract int GetHashCodeCore();

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

public sealed class BoolTypeSymbol : ScalarTypeSymbol
{
    public static BoolTypeSymbol Instance { get; } = new();

    private BoolTypeSymbol()
        : base("bool", bitWidth: 1, sizeBytes: 1, alignmentBytes: 1)
    {
    }

    public override bool IsScalarCastType => true;

    public override bool IsLegalRuntimeObject(object value) => value is bool;

    protected override bool EqualsCore(TypeSymbol other) => other is BoolTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(BoolTypeSymbol));
}

public sealed class IntegerTypeSymbol : ScalarTypeSymbol
{
    internal IntegerTypeSymbol(string name, int bitWidth, bool isSigned)
        : base(name, bitWidth, sizeBytes: Math.Max(1, (bitWidth + 7) / 8), alignmentBytes: Math.Max(1, (bitWidth + 7) / 8))
    {
        IsSignedInteger = isSigned;
        MinValue = isSigned ? -(1L << (bitWidth - 1)) : 0L;
        MaxValue = isSigned ? (1L << (bitWidth - 1)) - 1L : (1L << bitWidth) - 1L;
    }

    public override bool IsSignedInteger { get; }
    public long MinValue { get; }
    public long MaxValue { get; }

    public override bool IsLegalRuntimeObject(object value) => (value is long l) && IsLegalRuntimeObject(l);

    private bool IsLegalRuntimeObject(long value) => value >= MinValue && value <= MaxValue;

    protected override bool EqualsCore(TypeSymbol other)
    {
        IntegerTypeSymbol typedOther = (IntegerTypeSymbol)other;
        return BitWidth == typedOther.BitWidth
            && IsSignedInteger == typedOther.IsSignedInteger;
    }

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(IntegerTypeSymbol), BitWidth, IsSignedInteger);
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

    public override bool IsLegalRuntimeObject(object value) => value is PointedValue;

    protected override bool EqualsCore(TypeSymbol other)
    {
        PointerLikeTypeSymbol typedOther = (PointerLikeTypeSymbol)Requires.NotNull(other);
        return IsConst == typedOther.IsConst
            && IsVolatile == typedOther.IsVolatile
            && Alignment == typedOther.Alignment
            && StorageClass == typedOther.StorageClass
            && PointeeType == typedOther.PointeeType;
    }

    protected override int GetHashCodeCore()
    {
        return HashCode.Combine(GetType(), PointeeType, IsConst, IsVolatile, Alignment, StorageClass);
    }

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
            if (element.Type != GetRuntimeElementType())
                return false;
        }

        return true;
    }

    protected override bool EqualsCore(TypeSymbol other)
    {
        ArrayTypeSymbol typedOther = (ArrayTypeSymbol)other;
        return Length == typedOther.Length
            && ElementType == typedOther.ElementType;
    }

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(ArrayTypeSymbol), ElementType, Length);

    private static string BuildName(TypeSymbol elementType, int? length)
    {
        return length is int knownLength
            ? $"[{knownLength}]{Requires.NotNull(elementType).Name}"
            : $"[{Requires.NotNull(elementType).Name}]";
    }

    private RuntimeTypeSymbol GetRuntimeElementType() => Assert.NotNull(ElementType as RuntimeTypeSymbol);
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

            if (fieldValue.Type != fieldType)
                return false;
        }

        return true;
    }

    protected override bool EqualsCore(TypeSymbol other) => ReferenceEquals(this, other);

    protected override int GetHashCodeCore() => RuntimeHelpers.GetHashCode(this);
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

    protected override bool EqualsCore(TypeSymbol other) => ReferenceEquals(this, other);

    protected override int GetHashCodeCore() => RuntimeHelpers.GetHashCode(this);
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

    protected override bool EqualsCore(TypeSymbol other) => ReferenceEquals(this, other);

    protected override int GetHashCodeCore() => RuntimeHelpers.GetHashCode(this);
}

public sealed class FunctionTypeSymbol(FunctionSymbol function) : ComptimeTypeSymbol($"fn {Requires.NotNull(function).Name}")
{
    public FunctionSymbol Function { get; } = Requires.NotNull(function);

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, Function);

    protected override bool EqualsCore(TypeSymbol other) => ReferenceEquals(Function, ((FunctionTypeSymbol)other).Function);

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(FunctionTypeSymbol), RuntimeHelpers.GetHashCode(Function));
}

public sealed class ModuleTypeSymbol(ModuleSymbol module) : ComptimeTypeSymbol($"module {Requires.NotNull(module).Name}")
{
    public ModuleSymbol Module { get; } = Requires.NotNull(module);

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, Module);

    protected override bool EqualsCore(TypeSymbol other) => ReferenceEquals(Module, ((ModuleTypeSymbol)other).Module);

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(ModuleTypeSymbol), RuntimeHelpers.GetHashCode(Module));
}

public sealed class UnknownTypeSymbol : ComptimeTypeSymbol
{
    public static UnknownTypeSymbol Instance { get; } = new();

    private UnknownTypeSymbol()
        : base("<unknown>")
    {
    }

    protected override bool EqualsCore(TypeSymbol other) => other is UnknownTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(UnknownTypeSymbol));
}

public sealed class UndefinedLiteralTypeSymbol : ComptimeTypeSymbol
{
    public static UndefinedLiteralTypeSymbol Instance { get; } = new();

    private UndefinedLiteralTypeSymbol()
        : base("undefined")
    {
    }

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, UndefinedValue.Instance);

    protected override bool EqualsCore(TypeSymbol other) => other is UndefinedLiteralTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(UndefinedLiteralTypeSymbol));
}

public sealed class IntegerLiteralTypeSymbol : ComptimeTypeSymbol
{
    public static IntegerLiteralTypeSymbol Instance { get; } = new();

    private IntegerLiteralTypeSymbol()
        : base("<int-literal>")
    {
    }

    public override bool IsLegalRuntimeObject(object value) => value is long;

    protected override bool EqualsCore(TypeSymbol other) => other is IntegerLiteralTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(IntegerLiteralTypeSymbol));
}

public sealed class VoidTypeSymbol : ComptimeTypeSymbol
{
    public static VoidTypeSymbol Instance { get; } = new();

    private VoidTypeSymbol()
        : base("void")
    {
    }

    public override bool IsLegalRuntimeObject(object value) => ReferenceEquals(value, VoidValue.Instance);

    protected override bool EqualsCore(TypeSymbol other) => other is VoidTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(VoidTypeSymbol));
}

public sealed class RangeTypeSymbol : ComptimeTypeSymbol
{
    public static RangeTypeSymbol Instance { get; } = new();

    private RangeTypeSymbol()
        : base("range")
    {
    }

    protected override bool EqualsCore(TypeSymbol other) => other is RangeTypeSymbol;

    protected override int GetHashCodeCore() => HashCode.Combine(typeof(RangeTypeSymbol));
}

public static class BuiltinTypes
{
    public static readonly UnknownTypeSymbol Unknown = UnknownTypeSymbol.Instance;
    public static readonly ComptimeTypeSymbol UndefinedLiteral = UndefinedLiteralTypeSymbol.Instance;
    public static readonly IntegerLiteralTypeSymbol IntegerLiteral = IntegerLiteralTypeSymbol.Instance;
    public static readonly BoolTypeSymbol Bool = BoolTypeSymbol.Instance;
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
    public static readonly VoidTypeSymbol Void = VoidTypeSymbol.Instance;
    public static readonly RangeTypeSymbol Range = RangeTypeSymbol.Instance;

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
