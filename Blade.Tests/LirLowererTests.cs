using System.Diagnostics;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public sealed class LirLowererTests
{
    private static readonly TextSpan Span = new(0, 0);

    [Test]
    public void Lower_CallWithMoreThanTwoExtraResults_ThrowsInsteadOfPickingAnAbiFallback()
    {
        MirCallInstruction call = new(
            new MirValueId(0),
            BuiltinTypes.U32,
            new FunctionSymbol("too_many_results", FunctionKind.Default),
            [],
            Span,
            [
                (new MirValueId(1), BuiltinTypes.Bool),
                (new MirValueId(2), BuiltinTypes.Bool),
                (new MirValueId(3), BuiltinTypes.Bool),
            ]);
        MirBlock block = new("bb0", [], [call], new MirReturnTerminator([], Span));
        MirFunction function = CreateMirFunction("$top", isEntryPoint: true, FunctionKind.Default, [], [block]);
        MirModule module = new([function]);

        UnreachableException? ex = Assert.Throws<UnreachableException>(() => LirLowerer.Lower(module));

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("too_many_results"));
        Assert.That(ex.Message, Does.Contain("only C and Z flag result slots are available"));
    }
}
