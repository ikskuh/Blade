using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.Tests;

[TestFixture]
public sealed class RuntimeTypeAndValueTests
{
    [TestCase(BuiltinTypeCase.Bit, 1)]
    [TestCase(BuiltinTypeCase.Nit, 1)]
    [TestCase(BuiltinTypeCase.Nib, 4)]
    [TestCase(BuiltinTypeCase.U8, 8)]
    [TestCase(BuiltinTypeCase.I8, 8)]
    [TestCase(BuiltinTypeCase.U16, 16)]
    [TestCase(BuiltinTypeCase.I16, 16)]
    [TestCase(BuiltinTypeCase.U32, 32)]
    [TestCase(BuiltinTypeCase.I32, 32)]
    [TestCase(BuiltinTypeCase.Uint, 32)]
    [TestCase(BuiltinTypeCase.Int, 32)]
    public void ScalarWidthBits_ReturnsExpectedWidth(BuiltinTypeCase typeCase, int expectedWidth)
    {
        RuntimeTypeSymbol type = GetBuiltinRuntimeType(typeCase);

        Assert.That(type.ScalarWidthBits, Is.EqualTo(expectedWidth));
    }

    [Test]
    public void PointerTypeNames_IncludeQualifiersInNormalizedOrder()
    {
        PointerTypeSymbol pointer = new(BuiltinTypes.U32, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);
        MultiPointerTypeSymbol multiPointer = new(BuiltinTypes.U8, isConst: false, isVolatile: true, alignment: 8, storageClass: VariableStorageClass.Reg);

        Assert.That(pointer.Name, Is.EqualTo("*hub const volatile align(4) u32"));
        Assert.That(multiPointer.Name, Is.EqualTo("[*]reg volatile align(8) u8"));
        Assert.That(multiPointer.ScalarWidthBits, Is.EqualTo(32));
    }

    [Test]
    public void ArrayTypeNames_ReflectKnownAndUnknownLengths()
    {
        ArrayTypeSymbol known = new(BuiltinTypes.U8, 4);
        ArrayTypeSymbol unknown = new(BuiltinTypes.U16);

        Assert.That(known.Name, Is.EqualTo("[4]u8"));
        Assert.That(unknown.Name, Is.EqualTo("[u16]"));
    }

    [Test]
    public void StructTypeSymbol_UsesProvidedMembers()
    {
        Dictionary<string, AggregateMemberSymbol> members = new(StringComparer.Ordinal)
        {
            ["lo"] = new AggregateMemberSymbol("lo", BuiltinTypes.U16, byteOffset: 0, bitOffset: 0, bitWidth: 0, isBitfield: false),
            ["hi"] = new AggregateMemberSymbol("hi", BuiltinTypes.U16, byteOffset: 2, bitOffset: 0, bitWidth: 0, isBitfield: false),
        };

        StructTypeSymbol pair = new(
            "Pair",
            new Dictionary<string, TypeSymbol>
            {
                ["lo"] = BuiltinTypes.U16,
                ["hi"] = BuiltinTypes.U16,
            },
            members,
            sizeBytes: 4,
            alignmentBytes: 2);

        Assert.That(pair.Members.Keys, Is.EquivalentTo(new[] { "lo", "hi" }));
        Assert.That(pair.Members["lo"].ByteOffset, Is.EqualTo(0));
        Assert.That(pair.Members["hi"].ByteOffset, Is.EqualTo(2));
    }

    [Test]
    public void BitfieldFieldWidthBits_ReturnExpectedResults()
    {
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new(
            "Flags",
            BuiltinTypes.U32,
            new Dictionary<string, TypeSymbol> { ["flag"] = BuiltinTypes.Bool, ["mode"] = enumType },
            new Dictionary<string, AggregateMemberSymbol>
            {
                ["flag"] = new AggregateMemberSymbol("flag", BuiltinTypes.Bool, 0, 0, 1, isBitfield: true),
                ["mode"] = new AggregateMemberSymbol("mode", enumType, 0, 1, 16, isBitfield: true),
            });

        Assert.That(BuiltinTypes.Bool.BitfieldFieldWidthBits, Is.EqualTo(1));
        Assert.That(BuiltinTypes.U16.BitfieldFieldWidthBits, Is.EqualTo(16));
        Assert.That(enumType.BitfieldFieldWidthBits, Is.EqualTo(16));
        Assert.That(bitfieldType.BitfieldFieldWidthBits, Is.EqualTo(32));
    }

    [Test]
    public void RuntimeTypeLayout_UsesInstanceProperties()
    {
        StructTypeSymbol structType = new("S", new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: 4, alignmentBytes: 2);
        UnionTypeSymbol unionType = new("U", new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: 8, alignmentBytes: 4);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false);
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U16, 3);

        Assert.Multiple(() =>
        {
            Assert.That(structType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(structType.SizeBytes, Is.EqualTo(4));
            Assert.That(unionType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(unionType.SizeBytes, Is.EqualTo(8));
            Assert.That(enumType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(enumType.SizeBytes, Is.EqualTo(2));
            Assert.That(bitfieldType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(bitfieldType.SizeBytes, Is.EqualTo(4));
            Assert.That(pointerType.AlignmentBytes, Is.EqualTo(4));
            Assert.That(pointerType.SizeBytes, Is.EqualTo(4));
            Assert.That(arrayType.AlignmentBytes, Is.EqualTo(2));
            Assert.That(arrayType.SizeBytes, Is.EqualTo(6));
            Assert.That(BuiltinTypes.Bool.AlignmentBytes, Is.EqualTo(1));
            Assert.That(BuiltinTypes.Nib.SizeBytes, Is.EqualTo(1));
            Assert.That(arrayType.GetSizeInMemorySpace(VariableStorageClass.Hub), Is.EqualTo(6));
            Assert.That(arrayType.GetSizeInMemorySpace(VariableStorageClass.Reg), Is.EqualTo(2));
            Assert.That(arrayType.GetAlignmentInMemorySpace(VariableStorageClass.Hub), Is.EqualTo(2));
            Assert.That(arrayType.GetAlignmentInMemorySpace(VariableStorageClass.Lut), Is.EqualTo(1));
        });
    }

    [Test]
    public void RuntimeTypeLegality_RejectsRepairStylePayloads()
    {
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long> { ["Idle"] = 0 }, isOpen: true);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U16, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(pointerType.IsLegalRuntimeObject(257), Is.False);
            Assert.That(enumType.IsLegalRuntimeObject(258), Is.False);
            Assert.That(bitfieldType.IsLegalRuntimeObject(0x1_0001), Is.False);
            Assert.That(BuiltinTypes.I8.IsLegalRuntimeObject(255), Is.False);
            Assert.That(BuiltinTypes.I32.IsLegalRuntimeObject(0xFFFF_FFFFu), Is.False);
            Assert.That(BuiltinTypes.Bool.IsLegalRuntimeObject(1), Is.False);
        });
    }

    [Test]
    public void IntegerRuntimeLegality_UsesNumericRangeInsteadOfClrType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BuiltinTypes.I16.IsLegalRuntimeObject((byte)255), Is.True);
            Assert.That(BuiltinTypes.U8.IsLegalRuntimeObject(255u), Is.True);
            Assert.That(BuiltinTypes.U8.IsLegalRuntimeObject(256u), Is.False);
            Assert.That(BuiltinTypes.I8.IsLegalRuntimeObject((short)-129), Is.False);
        });
    }

    [Test]
    public void SignedIntegerAndScalarCastProperties_ReturnExpectedResults()
    {
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(BuiltinTypes.Nit.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I8.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I16.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.I32.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.Int.IsSignedInteger, Is.True);
            Assert.That(BuiltinTypes.U32.IsSignedInteger, Is.False);

            Assert.That(new PointerTypeSymbol(BuiltinTypes.U32, isConst: false).IsScalarCastType, Is.True);
            Assert.That(enumType.IsScalarCastType, Is.True);
            Assert.That(bitfieldType.IsScalarCastType, Is.True);
            Assert.That(BuiltinTypes.String.IsLegalRuntimeObject("x"), Is.True);
        });
    }

    [Test]
    public void BladeValueTypes_ValidatePayloadKinds()
    {
        RuntimeBladeValue runtimeValue = new(BuiltinTypes.I8, (sbyte)(-1));
        RuntimeBladeValue widenedRuntimeValue = new(BuiltinTypes.I16, (byte)7);
        ComptimeBladeValue stringValue = new((ComptimeTypeSymbol)BuiltinTypes.String, "hello");
        ComptimeBladeValue voidValue = new((ComptimeTypeSymbol)BuiltinTypes.Void, VoidValue.Instance);
        ComptimeBladeValue undefinedValue = new((ComptimeTypeSymbol)BuiltinTypes.UndefinedLiteral, UndefinedValue.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(runtimeValue.Type, Is.SameAs(BuiltinTypes.I8));
            Assert.That(runtimeValue.Value, Is.EqualTo((sbyte)(-1)));
            Assert.That(widenedRuntimeValue.Type, Is.SameAs(BuiltinTypes.I16));
            Assert.That(widenedRuntimeValue.Value, Is.TypeOf<byte>());
            Assert.That(widenedRuntimeValue.Value, Is.EqualTo((byte)7));
            Assert.That(stringValue.Type, Is.SameAs(BuiltinTypes.String));
            Assert.That(stringValue.Value, Is.EqualTo("hello"));
            Assert.That(voidValue.Value, Is.SameAs(VoidValue.Instance));
            Assert.That(undefinedValue.Value, Is.SameAs(UndefinedValue.Instance));
            Assert.That(() => new RuntimeBladeValue(BuiltinTypes.U8, 256u), Throws.ArgumentException);
        });
    }

    private static RuntimeTypeSymbol GetBuiltinRuntimeType(BuiltinTypeCase typeCase)
    {
        return typeCase switch
        {
            BuiltinTypeCase.Bit => BuiltinTypes.Bit,
            BuiltinTypeCase.Nit => BuiltinTypes.Nit,
            BuiltinTypeCase.Nib => BuiltinTypes.Nib,
            BuiltinTypeCase.U8 => BuiltinTypes.U8,
            BuiltinTypeCase.I8 => BuiltinTypes.I8,
            BuiltinTypeCase.U16 => BuiltinTypes.U16,
            BuiltinTypeCase.I16 => BuiltinTypes.I16,
            BuiltinTypeCase.U32 => BuiltinTypes.U32,
            BuiltinTypeCase.I32 => BuiltinTypes.I32,
            BuiltinTypeCase.Uint => BuiltinTypes.Uint,
            BuiltinTypeCase.Int => BuiltinTypes.Int,
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    public enum BuiltinTypeCase
    {
        Bit,
        Nit,
        Nib,
        U8,
        I8,
        U16,
        I16,
        U32,
        I32,
        Uint,
        Int,
    }
}
