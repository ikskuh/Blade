using Blade;

namespace Blade.Tests;

[TestFixture]
public class P2InstructionMetadataTests
{
    [Test]
    public void TryGetInstructionForm_ReturnsKnownForms()
    {
        Assert.That(P2InstructionMetadata.TryGetInstructionForm("ADD", 2, out P2InstructionFormInfo add), Is.True);
        Assert.That(add.Mnemonic, Is.EqualTo("ADD"));
        Assert.That(add.OperandCount, Is.EqualTo(2));
        Assert.That(add.DestinationAccess, Is.EqualTo(P2OperandAccess.ReadWrite));
        Assert.That(add.DefinesDestination, Is.True);
        Assert.That(add.ReadsDestination, Is.True);
        Assert.That(add.IsControlFlow, Is.False);

        Assert.That(P2InstructionMetadata.TryGetInstructionForm("missing", 2, out _), Is.False);
        Assert.That(P2InstructionMetadata.TryGetInstructionForm("ADD", 7, out _), Is.False);
    }

    [Test]
    public void ConditionModczAndFlagHelpers_RecognizeCanonicalAndAliasValues()
    {
        Assert.That(P2InstructionMetadata.IsValidConditionPrefix("if_c"), Is.True);
        Assert.That(P2InstructionMetadata.IsCanonicalConditionPrefix("IF_C"), Is.True);
        Assert.That(P2InstructionMetadata.IsCanonicalConditionPrefix("if_c"), Is.True);
        Assert.That(P2InstructionMetadata.IsValidConditionPrefix("if_missing"), Is.False);

        Assert.That(P2InstructionMetadata.IsValidModczOperand("_set"), Is.True);
        Assert.That(P2InstructionMetadata.IsCanonicalModczOperand("_set"), Is.True);
        Assert.That(P2InstructionMetadata.IsValidModczOperand("_bogus"), Is.False);

        Assert.That(P2InstructionMetadata.IsValidFlagEffect("WC"), Is.True);
        Assert.That(P2InstructionMetadata.TryParseFlagEffect("orz", out P2FlagEffect effect), Is.True);
        Assert.That(effect, Is.EqualTo(P2FlagEffect.ORZ));
        Assert.That(P2InstructionMetadata.TryParseFlagEffect("bogus", out P2FlagEffect invalid), Is.False);
        Assert.That(invalid, Is.EqualTo(P2FlagEffect.None));
    }

    [Test]
    public void FlagAndControlFlowQueries_ReturnExpectedResults()
    {
        Assert.That(P2InstructionMetadata.AllowsFlagEffect("ADD", 2, null), Is.True);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect("ADD", 2, "WC"), Is.True);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect("ADD", 2, "ORC"), Is.False);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect("missing", 2, "WC"), Is.False);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect("ADD", 2, "bogus"), Is.False);

        Assert.That(P2InstructionMetadata.IsCall("CALL", 1), Is.True);
        Assert.That(P2InstructionMetadata.IsReturn("RET", 0), Is.True);
        Assert.That(P2InstructionMetadata.IsControlFlow("JMP", 1), Is.True);
        Assert.That(P2InstructionMetadata.HasNoRegisterEffect("NOP", 0), Is.True);
        Assert.That(P2InstructionMetadata.IsPureRegisterLocal("ABS", 1), Is.True);
        Assert.That(P2InstructionMetadata.RequiresImmediateAddressPrefix("CALL", 1, 0), Is.True);
        Assert.That(P2InstructionMetadata.RequiresImmediateAddressPrefix("CALL", 1, 1), Is.False);
        Assert.That(P2InstructionMetadata.IsSpecialRegisterName("PTRA"), Is.True);
        Assert.That(P2InstructionMetadata.IsSpecialRegisterName("NOT_A_REGISTER"), Is.False);
    }

    [Test]
    public void GetOperandAccess_HandlesCallsBranchesAndOutOfRangeCases()
    {
        Assert.That(P2InstructionMetadata.GetOperandAccess("missing", 1, 0), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess("ADD", 2, -1), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess("ADD", 2, 2), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess("RET", 0, 0), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess("NOP", 0, 0), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess("CALL", 1, 0), Is.EqualTo(P2OperandAccess.Read));
        Assert.That(P2InstructionMetadata.GetOperandAccess("JMP", 1, 0), Is.EqualTo(P2OperandAccess.Read));
        Assert.That(P2InstructionMetadata.GetOperandAccess("ADD", 2, 0), Is.EqualTo(P2OperandAccess.ReadWrite));
        Assert.That(P2InstructionMetadata.GetOperandAccess("ADD", 2, 1), Is.EqualTo(P2OperandAccess.Read));
    }
}
