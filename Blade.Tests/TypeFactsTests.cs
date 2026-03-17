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
    }
}
