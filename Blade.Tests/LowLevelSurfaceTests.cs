using System.Collections.Generic;
using System.Linq;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.Diagnostics;
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
        InlineAsmVarBindingSlot dst = new("dst");
        InlineAsmVarBindingSlot src = new("src");
        InlineAsmVarBindingSlot unused = new("unused");
        IReadOnlyDictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> access = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines:
            [
                new InlineAsmInstructionLine(
                    condition: null,
                    mnemonic: P2Mnemonic.ADD,
                    operands:
                    [
                        new InlineAsmBindingRefOperand(dst),
                        new InlineAsmBindingRefOperand(src),
                    ],
                    flagEffect: null,
                    trailingComment: null),
                new InlineAsmInstructionLine(
                    condition: null,
                    mnemonic: P2Mnemonic.MOV,
                    operands:
                    [
                        new InlineAsmBindingRefOperand(dst),
                        new InlineAsmImmediateOperand(0x10),
                    ],
                    flagEffect: null,
                    trailingComment: null),
            ],
            bindings: [dst, src, unused]);

        Assert.That(access[dst], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(access[src], Is.EqualTo(InlineAsmBindingAccess.Read));
        Assert.That(access[unused], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
    }

    [Test]
    public void InlineAssemblyBindingAnalysis_FallsBackToReadWriteWhenLoweringIsUnsafe()
    {
        InlineAsmVarBindingSlot x = new("x");
        InlineAsmVarBindingSlot y = new("y");

        // Volatile asm now gets precise per-operand analysis (not conservative ReadWrite).
        IReadOnlyDictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> volatileAccess = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines:
            [
                new InlineAsmInstructionLine(
                    condition: null,
                    mnemonic: P2Mnemonic.MOV,
                    operands:
                    [
                        new InlineAsmBindingRefOperand(x),
                        new InlineAsmBindingRefOperand(y),
                    ],
                    flagEffect: null,
                    trailingComment: null),
            ],
            bindings: [x, y]);

        Assert.That(volatileAccess[x], Is.EqualTo(InlineAsmBindingAccess.Write));
        Assert.That(volatileAccess[y], Is.EqualTo(InlineAsmBindingAccess.Read));

        // Invalid instruction form still falls back to ReadWrite.
        IReadOnlyDictionary<InlineAsmBindingSlot, InlineAsmBindingAccess> unknownMnemonic = InlineAssemblyBindingAnalysis.ComputeBindingAccess(
            parsedLines:
            [
                new InlineAsmInstructionLine(
                    condition: null,
                    mnemonic: P2Mnemonic.RET,
                    operands: [new InlineAsmBindingRefOperand(x)],
                    flagEffect: null,
                    trailingComment: null),
            ],
            bindings: [x]);

        Assert.That(unknownMnemonic[x], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Read), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Write), Is.False);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Write), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Read), Is.False);
    }

[Test]
    public void LowLevelModelTypes_ExposeExpectedSurface()
    {
        AsmPhysicalRegisterOperand physical = new(new P2Register(0x1F8));
        Assert.That(physical.Address, Is.EqualTo(0x1F8));
        Assert.That(physical.Format(), Is.EqualTo("PTRA"));

        LirUnreachableTerminator lirUnreachable = new(new TextSpan(4, 2));
        Assert.That(lirUnreachable.Span, Is.EqualTo(new TextSpan(4, 2)));

    }
}
