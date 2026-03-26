using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class LowLevelSurfaceTests
{
    [Test]
    public void InlineAssemblyBindingAnalysis_ComputesPreciseTypedAccesses()
    {
        IReadOnlyDictionary<string, InlineAsmBindingAccess> access = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines:
            [
                new InlineAssemblyValidator.AsmLine { Mnemonic = P2Mnemonic.ADD, Operands = ["{dst}", "{src}"] },
                new InlineAssemblyValidator.AsmLine { Mnemonic = P2Mnemonic.MOV, Operands = ["{dst}", "#0x10"] },
            ],
            bindingNames: ["dst", "src", "unused"]);

        Assert.That(access["dst"], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(access["src"], Is.EqualTo(InlineAsmBindingAccess.Read));
        Assert.That(access["unused"], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
    }

    [Test]
    public void InlineAssemblyBindingAnalysis_FallsBackToReadWriteWhenLoweringIsUnsafe()
    {
        // Volatile asm now gets precise per-operand analysis (not conservative ReadWrite).
        IReadOnlyDictionary<string, InlineAsmBindingAccess> volatileAccess = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines: [new InlineAssemblyValidator.AsmLine { Mnemonic = P2Mnemonic.MOV, Operands = ["{x}", "{y}"] }],
            bindingNames: ["x", "y"]);

        Assert.That(volatileAccess["x"], Is.EqualTo(InlineAsmBindingAccess.Write));
        Assert.That(volatileAccess["y"], Is.EqualTo(InlineAsmBindingAccess.Read));

        // Unknown mnemonic (null) still falls back to ReadWrite via CanLowerTypedLosslessly.
        IReadOnlyDictionary<string, InlineAsmBindingAccess> unknownMnemonic = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines: [new InlineAssemblyValidator.AsmLine { Mnemonic = null, Operands = ["{x}"] }],
            bindingNames: ["x"]);

        Assert.That(unknownMnemonic["x"], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Read), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Write), Is.False);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Write), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Read), Is.False);
    }

    [Test]
    public void LowLevelModelTypes_ExposeExpectedSurface()
    {
        AsmPhysicalRegisterOperand physical = new(0x1F8, "PTRA");
        Assert.That(physical.Address, Is.EqualTo(0x1F8));
        Assert.That(physical.Format(), Is.EqualTo("PTRA"));

        LirUnreachableTerminator lirUnreachable = new(new TextSpan(4, 2));
        Assert.That(lirUnreachable.Span, Is.EqualTo(new TextSpan(4, 2)));

        BoundErrorStatement errorStatement = new(new TextSpan(7, 1));
        Assert.That(errorStatement.Kind, Is.EqualTo(BoundNodeKind.ErrorStatement));
        Assert.That(errorStatement.Span, Is.EqualTo(new TextSpan(7, 1)));
    }
}
