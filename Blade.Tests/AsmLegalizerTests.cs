using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;

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
        AssertAugInstruction(nodes[0], "AUGD", 0x456);
        AssertInstruction(nodes[1], "SETXFRQ", 0x56);
    }

    [Test]
    public void Legalize_UsesAugsForLargeAkpinImmediate()
    {
        IReadOnlyList<AsmNode> nodes = LegalizeNodes(
            new AsmInstructionNode(P2Mnemonic.AKPIN, [new AsmImmediateOperand(0x456)]));

        Assert.That(nodes, Has.Count.EqualTo(2));
        AssertAugInstruction(nodes[0], "AUGS", 0x456);
        AssertInstruction(nodes[1], "AKPIN", 0x56);
    }

    [Test]
    public void Legalize_UsesOperandRoleMetadataForWrpin()
    {
        IReadOnlyList<AsmNode> nodes = LegalizeNodes(
            new AsmInstructionNode(P2Mnemonic.WRPIN, [new AsmImmediateOperand(0x456), new AsmImmediateOperand(0x789)]));

        Assert.That(nodes, Has.Count.EqualTo(3));
        AssertAugInstruction(nodes[0], "AUGD", 0x456);
        AssertAugInstruction(nodes[1], "AUGS", 0x789);

        AsmInstructionNode wrpin = (AsmInstructionNode)nodes[2];
        Assert.That(wrpin.Opcode, Is.EqualTo("WRPIN"));
        Assert.That(((AsmImmediateOperand)wrpin.Operands[0]).Value, Is.EqualTo(0x56));
        Assert.That(((AsmImmediateOperand)wrpin.Operands[1]).Value, Is.EqualTo(0x189));
    }

    [Test]
    public void Legalize_RejectsOversizedImmediateNOperand()
    {
        AsmModule module = CreateModule(
            new AsmInstructionNode(
                P2Mnemonic.GETNIB,
                [AsmRegister(1), new AsmImmediateOperand(0), new AsmImmediateOperand(8)]));

        Assert.That(
            () => AsmLegalizer.Legalize(module),
            Throws.InvalidOperationException.With.Message.Contains("cannot be AUG-extended"));
    }

    [Test]
    public void Legalize_UsesSharedConstantRegisterForHubAddressSymbol()
    {
        var hubValue = CreateStoragePlace("hub_value", storageClass: AddressSpace.Hub);
        AsmModule module = LegalizeModule(
            new AsmInstructionNode(
                P2Mnemonic.MOV,
                [AsmRegister(1), new AsmSymbolOperand(hubValue, AsmSymbolAddressingMode.Immediate, 4)]));

        AsmInstructionNode instruction = (AsmInstructionNode)module.Functions[0].Nodes[0];
        AsmSymbolOperand operand = (AsmSymbolOperand)instruction.Operands[1];
        AsmDataDefinition definition = module.DataBlocks.Single(block => block.Kind == AsmDataBlockKind.Constant).Definitions.Single();

        Assert.That(operand.AddressingMode, Is.EqualTo(AsmSymbolAddressingMode.Register));
        Assert.That(operand.Symbol.Name, Does.Contain("g_hub_value_plus_4"));
        Assert.That(definition.Symbol.Name, Is.EqualTo(operand.Symbol.Name));
    }

    [Test]
    public void Legalize_UsesSharedConstantRegisterForLutVirtualAddressAlias()
    {
        var lutValue = CreateStoragePlace("lut_value", storageClass: AddressSpace.Lut);
        AsmModule module = LegalizeModule(
            new AsmInstructionNode(
                P2Mnemonic.MOV,
                [AsmRegister(1), new AsmSymbolOperand(lutValue, AsmSymbolAddressingMode.Immediate, 2)]));

        AsmInstructionNode instruction = (AsmInstructionNode)module.Functions[0].Nodes[0];
        AsmSymbolOperand operand = (AsmSymbolOperand)instruction.Operands[1];
        AsmDataDefinition definition = module.DataBlocks.Single(block => block.Kind == AsmDataBlockKind.Constant).Definitions.Single();

        Assert.That(operand.AddressingMode, Is.EqualTo(AsmSymbolAddressingMode.Register));
        Assert.That(operand.Symbol.Name, Does.Contain("g_lut_value_vaddr_plus_2"));
        Assert.That(definition.Symbol.Name, Is.EqualTo(operand.Symbol.Name));
    }

    private static IReadOnlyList<AsmNode> LegalizeNodes(AsmInstructionNode instruction)
    {
        AsmModule module = CreateModule(instruction);
        return AsmLegalizer.Legalize(module).Functions[0].Nodes;
    }

    private static AsmModule LegalizeModule(params AsmNode[] nodes)
    {
        return AsmLegalizer.Legalize(CreateModule(nodes));
    }

    private static AsmModule CreateModule(params AsmNode[] nodes)
    {
        AsmFunction function = CreateAsmFunction(
            "test",
            isEntryPoint: true,
            CallingConventionTier.General,
            nodes);
        MirModule mirModule = new(function.OwningImage, [], [], []);
        LirModule lirModule = new(mirModule, [], [], []);
        return new AsmModule(lirModule, [], [], [function]);
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
