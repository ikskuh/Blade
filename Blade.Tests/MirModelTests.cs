using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class MirModelTests
{
    private static readonly TextSpan Span = new(0, 0);

    private static StoragePlace CreatePlace(string name)
    {
        VariableSymbol symbol = new(name, BuiltinTypes.U32, isConst: false, VariableStorageClass.Reg, VariableScopeKind.GlobalStorage, isExtern: false, fixedAddress: null, alignment: null);
        return new StoragePlace(symbol, StoragePlaceKind.AllocatableGlobalRegister, fixedAddress: null, staticInitializer: null);
    }

    [Test]
    public void RewriteUses_CoversMirInstructionVariants()
    {
        MirValueId v0 = new(0);
        MirValueId v1 = new(1);
        MirValueId v2 = new(2);
        MirValueId v3 = new(3);
        MirValueId v4 = new(4);
        StoragePlace place = CreatePlace("slot");
        Dictionary<MirValueId, MirValueId> mapping = new()
        {
            [v1] = new MirValueId(10),
            [v2] = new MirValueId(20),
            [v3] = new MirValueId(30),
            [v4] = new MirValueId(40),
        };

        MirLoadSymbolInstruction loadSymbol = new(v0, BuiltinTypes.U32, "global_symbol", Span);
        Assert.That(loadSymbol.Uses, Is.Empty);
        Assert.That(loadSymbol.RewriteUses(mapping), Is.SameAs(loadSymbol));

        MirCopyInstruction copy = new(v0, BuiltinTypes.U32, v1, Span);
        Assert.That(copy.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(copy));
        Assert.That(((MirCopyInstruction)copy.RewriteUses(mapping)).Source, Is.EqualTo(mapping[v1]));

        MirUnaryInstruction unary = new(v0, BuiltinTypes.U32, BoundUnaryOperatorKind.Negation, v1, Span);
        Assert.That(unary.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(unary));
        Assert.That(((MirUnaryInstruction)unary.RewriteUses(mapping)).Operand, Is.EqualTo(mapping[v1]));

        MirSelectInstruction select = new(v0, BuiltinTypes.U32, v1, v2, v3, Span);
        Assert.That(select.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(select));
        MirSelectInstruction rewrittenSelect = (MirSelectInstruction)select.RewriteUses(mapping);
        Assert.That(rewrittenSelect.Condition, Is.EqualTo(mapping[v1]));
        Assert.That(rewrittenSelect.WhenTrue, Is.EqualTo(mapping[v2]));
        Assert.That(rewrittenSelect.WhenFalse, Is.EqualTo(mapping[v3]));

        MirCallInstruction call = new(v0, BuiltinTypes.U32, "callee", [v1, v2], Span);
        Assert.That(call.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(call));
        Assert.That(((MirCallInstruction)call.RewriteUses(mapping)).Arguments, Is.EqualTo(new[] { mapping[v1], mapping[v2] }));

        MirIntrinsicCallInstruction intrinsic = new(v0, BuiltinTypes.U32, "encod", [v1, v2], Span);
        Assert.That(intrinsic.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(intrinsic));
        Assert.That(((MirIntrinsicCallInstruction)intrinsic.RewriteUses(mapping)).Arguments, Is.EqualTo(new[] { mapping[v1], mapping[v2] }));

        MirUpdatePlaceInstruction updatePlace = new(place, BoundBinaryOperatorKind.Add, v1, Span);
        Assert.That(updatePlace.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(updatePlace));
        Assert.That(((MirUpdatePlaceInstruction)updatePlace.RewriteUses(mapping)).Value, Is.EqualTo(mapping[v1]));

        MirYieldToInstruction yieldTo = new("pin", [v1, v2], Span);
        Assert.That(yieldTo.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(yieldTo));
        Assert.That(((MirYieldToInstruction)yieldTo.RewriteUses(mapping)).Arguments, Is.EqualTo(new[] { mapping[v1], mapping[v2] }));
    }

    [Test]
    public void RewriteUses_CoversMirTerminatorsAndModelProperties()
    {
        MirValueId condition = new(1);
        MirValueId t0 = new(2);
        MirValueId f0 = new(3);
        Dictionary<MirValueId, MirValueId> mapping = new()
        {
            [condition] = new MirValueId(11),
            [t0] = new MirValueId(12),
            [f0] = new MirValueId(13),
        };

        MirBranchTerminator branch = new(condition, "bb_true", "bb_false", [t0], [f0], Span);
        MirBranchTerminator rewrittenBranch = (MirBranchTerminator)branch.RewriteUses(mapping);
        Assert.That(rewrittenBranch.Condition, Is.EqualTo(mapping[condition]));
        Assert.That(rewrittenBranch.TrueArguments, Is.EqualTo(new[] { mapping[t0] }));
        Assert.That(rewrittenBranch.FalseArguments, Is.EqualTo(new[] { mapping[f0] }));

        MirUnreachableTerminator unreachable = new(Span);
        Assert.That(unreachable.Uses, Is.Empty);
        Assert.That(unreachable.RewriteUses(mapping), Is.SameAs(unreachable));

        MirValueId value = new(4);
        MirInlineAsmInstruction inlineAsm = new(
            AsmVolatility.NonVolatile,
            "MOV {x}, {x}",
            flagOutput: null,
            parsedLines: [],
            bindings:
            [
                new MirInlineAsmBinding("x", value, place: null, InlineAsmBindingAccess.ReadWrite),
                new MirInlineAsmBinding("y", new MirValueId(5), place: null, InlineAsmBindingAccess.Write),
            ],
            Span);

        Assert.That(inlineAsm.Uses, Is.EqualTo(new[] { value }));
        Assert.That(inlineAsm.RewriteUses(new Dictionary<MirValueId, MirValueId>()), Is.SameAs(inlineAsm));
    }
}
