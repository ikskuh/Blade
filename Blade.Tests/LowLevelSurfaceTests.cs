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
        InlineAsmBindingSlot dst = new("dst");
        InlineAsmBindingSlot src = new("src");
        InlineAsmBindingSlot unused = new("unused");
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
                    rawText: "add {dst}, {src}"),
                new InlineAsmInstructionLine(
                    condition: null,
                    mnemonic: P2Mnemonic.MOV,
                    operands:
                    [
                        new InlineAsmBindingRefOperand(dst),
                        new InlineAsmImmediateOperand(0x10),
                    ],
                    flagEffect: null,
                    rawText: "mov {dst}, #0x10"),
            ],
            bindings: [dst, src, unused]);

        Assert.That(access[dst], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(access[src], Is.EqualTo(InlineAsmBindingAccess.Read));
        Assert.That(access[unused], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
    }

    [Test]
    public void InlineAssemblyBindingAnalysis_FallsBackToReadWriteWhenLoweringIsUnsafe()
    {
        InlineAsmBindingSlot x = new("x");
        InlineAsmBindingSlot y = new("y");

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
                    rawText: "mov {x}, {y}"),
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
                    rawText: "ret {x}"),
            ],
            bindings: [x]);

        Assert.That(unknownMnemonic[x], Is.EqualTo(InlineAsmBindingAccess.ReadWrite));
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Read), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesRead(InlineAsmBindingAccess.Write), Is.False);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Write), Is.True);
        Assert.That(InlineAssemblyBindingAnalysis.IncludesWrite(InlineAsmBindingAccess.Read), Is.False);
    }

    [Test]
    public void InlineAssemblyValidator_CanonicalizesTempBindings()
    {
        DiagnosticBag diagnostics = new();
        InlineAssemblyValidator.ValidationResult result = InlineAssemblyValidator.Validate(
            """
            MOV %01, #1
            ADD %1, #2
            """,
            new TextSpan(0, 0),
            new Dictionary<string, InlineAsmBindingSlot>(),
            diagnostics);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.TempBindings.Select(static binding => binding.PlaceholderText), Is.EqualTo(new[] { "%1" }));
        Assert.That(result.ReferencedBindings.Select(static binding => binding.PlaceholderText), Is.EqualTo(new[] { "%1" }));
        InlineAsmInstructionLine firstInstruction = (InlineAsmInstructionLine)result.Lines[0];
        InlineAsmBindingRefOperand firstOperand = (InlineAsmBindingRefOperand)firstInstruction.Operands[0];
        Assert.That(firstOperand.Slot.PlaceholderText, Is.EqualTo("%1"));
    }

    [Test]
    public void InlineAssemblyValidator_WarnsWhenTempIsReadBeforeWrite()
    {
        DiagnosticBag diagnostics = new();

        _ = InlineAssemblyValidator.Validate(
            "ADD %0, #1",
            new TextSpan(0, 0),
            new Dictionary<string, InlineAsmBindingSlot>(),
            diagnostics);

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Code), Does.Contain(DiagnosticCode.W0307_InlineAsmTempReadBeforeWrite));
        Assert.That(diagnostics.Count(static diagnostic => diagnostic.Code == DiagnosticCode.W0307_InlineAsmTempReadBeforeWrite), Is.EqualTo(1));
    }

    [Test]
    public void LowLevelModelTypes_ExposeExpectedSurface()
    {
        AsmPhysicalRegisterOperand physical = new(new P2Register(0x1F8));
        Assert.That(physical.Address, Is.EqualTo(0x1F8));
        Assert.That(physical.Format(), Is.EqualTo("PTRA"));

        LirUnreachableTerminator lirUnreachable = new(new TextSpan(4, 2));
        Assert.That(lirUnreachable.Span, Is.EqualTo(new TextSpan(4, 2)));

        BoundErrorStatement errorStatement = new(new TextSpan(7, 1));
        Assert.That(errorStatement.Kind, Is.EqualTo(BoundNodeKind.ErrorStatement));
        Assert.That(errorStatement.Span, Is.EqualTo(new TextSpan(7, 1)));
    }
}
