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
}
