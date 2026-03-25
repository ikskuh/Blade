using System.Collections.Generic;
using Blade.Semantics;

namespace Blade.Tests;

[TestFixture]
public class TypeFactsTests
{
    [TestCaseSource(nameof(WidthCases))]
    public void TryGetIntegerWidth_ReturnsExpectedWidth(TypeSymbol type, bool expectedSuccess, int expectedWidth)
    {
        bool success = TypeFacts.TryGetIntegerWidth(type, out int width);
        Assert.That(success, Is.EqualTo(expectedSuccess));
        Assert.That(width, Is.EqualTo(expectedWidth));
    }

    private static IEnumerable<object[]> WidthCases()
    {
        yield return new object[] { BuiltinTypes.IntegerLiteral, true, 32 };
        yield return new object[] { BuiltinTypes.Bit, true, 1 };
        yield return new object[] { BuiltinTypes.Nit, true, 1 };
        yield return new object[] { BuiltinTypes.Nib, true, 4 };
        yield return new object[] { BuiltinTypes.U8, true, 8 };
        yield return new object[] { BuiltinTypes.I8, true, 8 };
        yield return new object[] { BuiltinTypes.U16, true, 16 };
        yield return new object[] { BuiltinTypes.I16, true, 16 };
        yield return new object[] { BuiltinTypes.U32, true, 32 };
        yield return new object[] { BuiltinTypes.I32, true, 32 };
        yield return new object[] { BuiltinTypes.Uint, true, 32 };
        yield return new object[] { BuiltinTypes.Int, true, 32 };
        yield return new object[] { BuiltinTypes.Bool, false, 0 };
        yield return new object[] { BuiltinTypes.Void, false, 0 };
        yield return new object[] { BuiltinTypes.String, false, 0 };
        yield return new object[] { new EnumTypeSymbol("Mode", BuiltinTypes.U8, new Dictionary<string, long> { ["Idle"] = 0 }, isOpen: false), true, 8 };
        yield return new object[] { new BitfieldTypeSymbol(
            "Flags",
            BuiltinTypes.U32,
            new Dictionary<string, TypeSymbol> { ["flag"] = BuiltinTypes.Bool },
            new Dictionary<string, AggregateMemberSymbol> { ["flag"] = new("flag", BuiltinTypes.Bool, 0, 0, 1, isBitfield: true) }), true, 32 };
    }

    [Test]
    public void PointerTypeNames_IncludeQualifiersInNormalizedOrder()
    {
        PointerTypeSymbol pointer = new(BuiltinTypes.U32, isConst: true, isVolatile: true, alignment: 4, storageClass: VariableStorageClass.Hub);
        MultiPointerTypeSymbol multiPointer = new(BuiltinTypes.U8, isConst: false, isVolatile: true, alignment: 8, storageClass: VariableStorageClass.Reg);

        Assert.That(pointer.Name, Is.EqualTo("*hub const volatile align(4) u32"));
        Assert.That(multiPointer.Name, Is.EqualTo("[*]reg volatile align(8) u8"));
    }

    [Test]
    public void TryGetScalarWidth_ReturnsPointerWidthForMultiPointer()
    {
        MultiPointerTypeSymbol pointer = new(BuiltinTypes.U16, isConst: false, storageClass: VariableStorageClass.Hub);

        bool success = TypeFacts.TryGetScalarWidth(pointer, out int width);

        Assert.That(success, Is.True);
        Assert.That(width, Is.EqualTo(32));
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
    public void TryGetBitfieldFieldWidth_ReturnsExpectedResults()
    {
        bool boolSuccess = TypeFacts.TryGetBitfieldFieldWidth(BuiltinTypes.Bool, out int boolWidth);
        bool integerSuccess = TypeFacts.TryGetBitfieldFieldWidth(BuiltinTypes.U16, out int integerWidth);
        bool unknownSuccess = TypeFacts.TryGetBitfieldFieldWidth(BuiltinTypes.String, out int unknownWidth);

        Assert.That(boolSuccess, Is.True);
        Assert.That(boolWidth, Is.EqualTo(1));
        Assert.That(integerSuccess, Is.True);
        Assert.That(integerWidth, Is.EqualTo(16));
        Assert.That(unknownSuccess, Is.False);
        Assert.That(unknownWidth, Is.EqualTo(0));
    }

    [Test]
    public void TryGetAlignmentBytes_CoversCompositeAndScalarCases()
    {
        StructTypeSymbol structType = new(
            "S",
            new Dictionary<string, TypeSymbol>(),
            new Dictionary<string, AggregateMemberSymbol>(),
            sizeBytes: 0,
            alignmentBytes: 0);
        UnionTypeSymbol unionType = new("U", new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: 0, alignmentBytes: 0);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U16, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false);
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U16, 3);
        ArrayTypeSymbol unsizedArrayType = new(BuiltinTypes.U16);

        Assert.Multiple(() =>
        {
            Assert.That(TypeFacts.TryGetAlignmentBytes(structType, out int structAlignment), Is.True);
            Assert.That(structAlignment, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetAlignmentBytes(unionType, out int unionAlignment), Is.True);
            Assert.That(unionAlignment, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetAlignmentBytes(enumType, out int enumAlignment), Is.True);
            Assert.That(enumAlignment, Is.EqualTo(2));
            Assert.That(TypeFacts.TryGetAlignmentBytes(bitfieldType, out int bitfieldAlignment), Is.True);
            Assert.That(bitfieldAlignment, Is.EqualTo(4));
            Assert.That(TypeFacts.TryGetAlignmentBytes(pointerType, out int pointerAlignment), Is.True);
            Assert.That(pointerAlignment, Is.EqualTo(4));
            Assert.That(TypeFacts.TryGetAlignmentBytes(BuiltinTypes.U8, out int byteAlignment), Is.True);
            Assert.That(byteAlignment, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetAlignmentBytes(BuiltinTypes.U16, out int halfwordAlignment), Is.True);
            Assert.That(halfwordAlignment, Is.EqualTo(2));
            Assert.That(TypeFacts.TryGetAlignmentBytes(arrayType, out int arrayAlignment), Is.True);
            Assert.That(arrayAlignment, Is.EqualTo(2));
            Assert.That(TypeFacts.TryGetAlignmentBytes(unsizedArrayType, out int unsizedAlignment), Is.False);
            Assert.That(unsizedAlignment, Is.EqualTo(0));
            Assert.That(TypeFacts.TryGetAlignmentBytes(BuiltinTypes.String, out int unknownAlignment), Is.False);
            Assert.That(unknownAlignment, Is.EqualTo(0));
        });
    }

    [Test]
    public void TryGetSizeBytes_CoversCompositeAndScalarCases()
    {
        StructTypeSymbol structType = new(
            "S",
            new Dictionary<string, TypeSymbol>(),
            new Dictionary<string, AggregateMemberSymbol>(),
            sizeBytes: -4,
            alignmentBytes: 1);
        UnionTypeSymbol unionType = new("U", new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>(), sizeBytes: -2, alignmentBytes: 1);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U16, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false);
        ArrayTypeSymbol arrayType = new(BuiltinTypes.U16, 3);

        Assert.Multiple(() =>
        {
            Assert.That(TypeFacts.TryGetSizeBytes(structType, out int structSize), Is.True);
            Assert.That(structSize, Is.EqualTo(0));
            Assert.That(TypeFacts.TryGetSizeBytes(unionType, out int unionSize), Is.True);
            Assert.That(unionSize, Is.EqualTo(0));
            Assert.That(TypeFacts.TryGetSizeBytes(enumType, out int enumSize), Is.True);
            Assert.That(enumSize, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetSizeBytes(bitfieldType, out int bitfieldSize), Is.True);
            Assert.That(bitfieldSize, Is.EqualTo(2));
            Assert.That(TypeFacts.TryGetSizeBytes(pointerType, out int pointerSize), Is.True);
            Assert.That(pointerSize, Is.EqualTo(4));
            Assert.That(TypeFacts.TryGetSizeBytes(arrayType, out int arraySize), Is.True);
            Assert.That(arraySize, Is.EqualTo(6));
            Assert.That(TypeFacts.TryGetSizeBytes(BuiltinTypes.Bool, out int boolSize), Is.True);
            Assert.That(boolSize, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetSizeBytes(BuiltinTypes.Nib, out int nibSize), Is.True);
            Assert.That(nibSize, Is.EqualTo(1));
            Assert.That(TypeFacts.TryGetSizeBytes(BuiltinTypes.String, out int unknownSize), Is.False);
            Assert.That(unknownSize, Is.EqualTo(0));
        });
    }

    [Test]
    public void TryNormalizeValue_CoversPointerEnumBitfieldSignedAndFailureCases()
    {
        PointerTypeSymbol pointerType = new(BuiltinTypes.U32, isConst: false);
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long> { ["Idle"] = 0 }, isOpen: true);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U16, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(TypeFacts.TryNormalizeValue(null, BuiltinTypes.U8, out object? nullValue), Is.True);
            Assert.That(nullValue, Is.Null);

            Assert.That(TypeFacts.TryNormalizeValue(257, pointerType, out object? pointerValue), Is.True);
            Assert.That(pointerValue, Is.EqualTo(257u));

            Assert.That(TypeFacts.TryNormalizeValue(258, enumType, out object? enumValue), Is.True);
            Assert.That(enumValue, Is.EqualTo(2u));

            Assert.That(TypeFacts.TryNormalizeValue(0x1_0001, bitfieldType, out object? bitfieldValue), Is.True);
            Assert.That(bitfieldValue, Is.EqualTo(1u));

            Assert.That(TypeFacts.TryNormalizeValue(255, BuiltinTypes.I8, out object? signedByteValue), Is.True);
            Assert.That(signedByteValue, Is.EqualTo(-1));

            Assert.That(TypeFacts.TryNormalizeValue(0xFFFF_FFFFu, BuiltinTypes.I32, out object? signedWordValue), Is.True);
            Assert.That(signedWordValue, Is.EqualTo(-1));

            Assert.That(TypeFacts.TryNormalizeValue(1, BuiltinTypes.String, out object? failedValue), Is.False);
            Assert.That(failedValue, Is.EqualTo(1));
        });
    }

    [Test]
    public void SignedIntegerAndScalarCastHelpers_ReturnExpectedResults()
    {
        EnumTypeSymbol enumType = new("Mode", BuiltinTypes.U8, new Dictionary<string, long>(), isOpen: false);
        BitfieldTypeSymbol bitfieldType = new("Flags", BuiltinTypes.U32, new Dictionary<string, TypeSymbol>(), new Dictionary<string, AggregateMemberSymbol>());

        Assert.Multiple(() =>
        {
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.Nit), Is.True);
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.I8), Is.True);
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.I16), Is.True);
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.I32), Is.True);
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.Int), Is.True);
            Assert.That(TypeFacts.IsSignedInteger(BuiltinTypes.U32), Is.False);

            Assert.That(TypeFacts.IsScalarCastType(new PointerTypeSymbol(BuiltinTypes.U32, isConst: false)), Is.True);
            Assert.That(TypeFacts.IsScalarCastType(enumType), Is.True);
            Assert.That(TypeFacts.IsScalarCastType(bitfieldType), Is.True);
            Assert.That(TypeFacts.IsScalarCastType(BuiltinTypes.String), Is.False);
        });
    }
}
