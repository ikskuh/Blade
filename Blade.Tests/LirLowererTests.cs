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
        FunctionSymbol targetFunction = new("too_many_results", FunctionKind.Default)
        {
            ReturnSlots =
            [
                new ReturnSlot(BuiltinTypes.U32, ReturnPlacement.Register),
                new ReturnSlot(BuiltinTypes.Bool, ReturnPlacement.FlagC),
                new ReturnSlot(BuiltinTypes.Bool, ReturnPlacement.FlagZ),
                new ReturnSlot(BuiltinTypes.Bool, ReturnPlacement.FlagZ),
            ],
        };
        MirCallInstruction call = new(
            MirValue(0),
            BuiltinTypes.U32,
            targetFunction,
            [],
            Span,
            [
                (MirValue(1), BuiltinTypes.Bool),
                (MirValue(2), BuiltinTypes.Bool),
                (MirValue(3), BuiltinTypes.Bool),
            ]);
        MirBlock block = new(MirBlockRef("bb0"), [], [call], new MirReturnTerminator([], Span));
        MirFunction function = CreateMirFunction("$top", isEntryPoint: true, FunctionKind.Default, [], [block]);
        MirModule module = new([], [], [function]);

        UnreachableException? ex = Assert.Throws<UnreachableException>(() => LirLowerer.Lower(module));

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex!.Message, Does.Contain("too_many_results"));
        Assert.That(ex.Message, Does.Contain("only C and Z flag result slots are available"));
    }
}
