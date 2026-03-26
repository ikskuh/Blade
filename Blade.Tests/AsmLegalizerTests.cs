using Blade.IR.Asm;

namespace Blade.Tests;

[TestFixture]
public class AsmLegalizerTests
{
    [Test]
    public void Legalize_UsesAugdForLargeSetxfrqImmediate()
    {
        IReadOnlyList<AsmNode> nodes = LegalizeNodes(
            new AsmInstructionNode(P2Mnemonic.SETXFRQ, [new AsmImmediateOperand(0x456)]));

        Assert.That(nodes, Has.Count.EqualTo(2));
        AssertAugInstruction(nodes[0], "AUGD", 0x456 >> 9);
        AssertInstruction(nodes[1], "SETXFRQ", 0x456 & 0x1FF);
    }

    [Test]
    public void Legalize_UsesAugsForLargeAkpinImmediate()
    {
        IReadOnlyList<AsmNode> nodes = LegalizeNodes(
            new AsmInstructionNode(P2Mnemonic.AKPIN, [new AsmImmediateOperand(0x456)]));

        Assert.That(nodes, Has.Count.EqualTo(2));
        AssertAugInstruction(nodes[0], "AUGS", 0x456 >> 9);
        AssertInstruction(nodes[1], "AKPIN", 0x456 & 0x1FF);
    }

    [Test]
    public void Legalize_UsesOperandRoleMetadataForWrpin()
    {
        IReadOnlyList<AsmNode> nodes = LegalizeNodes(
            new AsmInstructionNode(P2Mnemonic.WRPIN, [new AsmImmediateOperand(0x456), new AsmImmediateOperand(0x789)]));

        Assert.That(nodes, Has.Count.EqualTo(3));
        AssertAugInstruction(nodes[0], "AUGD", 0x456 >> 9);
        AssertAugInstruction(nodes[1], "AUGS", 0x789 >> 9);

        AsmInstructionNode wrpin = (AsmInstructionNode)nodes[2];
        Assert.That(wrpin.Opcode, Is.EqualTo("WRPIN"));
        Assert.That(((AsmImmediateOperand)wrpin.Operands[0]).Value, Is.EqualTo(0x456 & 0x1FF));
        Assert.That(((AsmImmediateOperand)wrpin.Operands[1]).Value, Is.EqualTo(0x789 & 0x1FF));
    }

    [Test]
    public void Legalize_RejectsOversizedImmediateNOperand()
    {
        AsmModule module = CreateModule(
            new AsmInstructionNode(
                P2Mnemonic.GETNIB,
                [new AsmRegisterOperand(1), new AsmImmediateOperand(0), new AsmImmediateOperand(8)]));

        Assert.That(
            () => AsmLegalizer.Legalize(module),
            Throws.InvalidOperationException.With.Message.Contains("cannot be AUG-extended"));
    }

    private static IReadOnlyList<AsmNode> LegalizeNodes(AsmInstructionNode instruction)
    {
        AsmModule module = CreateModule(instruction);
        return AsmLegalizer.Legalize(module).Functions[0].Nodes;
    }

    private static AsmModule CreateModule(params AsmNode[] nodes)
    {
        return new AsmModule(
            [
                new AsmFunction(
                    "test",
                    isEntryPoint: true,
                    CallingConventionTier.General,
                    nodes),
            ]);
    }

    private static void AssertAugInstruction(AsmNode node, string opcode, long expectedValue)
    {
        AsmInstructionNode instruction = (AsmInstructionNode)node;
        Assert.That(instruction.Opcode, Is.EqualTo(opcode));
        Assert.That(instruction.Operands, Has.Count.EqualTo(1));
        Assert.That(((AsmImmediateOperand)instruction.Operands[0]).Value, Is.EqualTo(expectedValue));
    }

    private static void AssertInstruction(AsmNode node, string opcode, long expectedImmediateValue)
    {
        AsmInstructionNode instruction = (AsmInstructionNode)node;
        Assert.That(instruction.Opcode, Is.EqualTo(opcode));
        Assert.That(instruction.Operands, Has.Count.EqualTo(1));
        Assert.That(((AsmImmediateOperand)instruction.Operands[0]).Value, Is.EqualTo(expectedImmediateValue));
    }
}
