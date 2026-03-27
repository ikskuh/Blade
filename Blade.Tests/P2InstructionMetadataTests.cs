using Blade;

namespace Blade.Tests;

[TestFixture]
public class P2InstructionMetadataTests
{
    [Test]
    public void TryGetInstructionForm_ReturnsKnownForms()
    {
        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.ADD, 2, out P2InstructionFormInfo add), Is.True);
        Assert.That(add.Mnemonic, Is.EqualTo("ADD"));
        Assert.That(add.OperandCount, Is.EqualTo(2));
        Assert.That(add.Operand0.Role, Is.EqualTo(P2OperandRole.D));
        Assert.That(add.Operand0.Access, Is.EqualTo(P2OperandAccess.ReadWrite));
        Assert.That(add.Operand0.BitWidth, Is.EqualTo(9));
        Assert.That(add.Operand1.Role, Is.EqualTo(P2OperandRole.S));
        Assert.That(add.Operand1.Access, Is.EqualTo(P2OperandAccess.Read));
        Assert.That(add.Operand1.SupportsImmediateSyntax, Is.True);
        Assert.That(add.Operand1.AugPrefix, Is.EqualTo(P2AugPrefixKind.AUGS));
        Assert.That(add.WrittenRegisters, Is.EqualTo(P2WrittenRegister.D));
        Assert.That(add.IsControlFlow, Is.False);

        Assert.That(P2InstructionMetadata.TryParseMnemonic("missing", out P2Mnemonic missing), Is.False);
        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.ADD, 7, out _), Is.False);
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
        Assert.That(P2InstructionMetadata.AllowsFlagEffect(P2Mnemonic.ADD, 2, null), Is.True);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect(P2Mnemonic.ADD, 2, "WC"), Is.True);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect(P2Mnemonic.ADD, 2, "ORC"), Is.False);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect((P2Mnemonic)(-1), 2, "WC"), Is.False);
        Assert.That(P2InstructionMetadata.AllowsFlagEffect(P2Mnemonic.ADD, 2, "bogus"), Is.False);

        Assert.That(P2InstructionMetadata.IsCall(P2Mnemonic.CALL, 1), Is.True);
        Assert.That(P2InstructionMetadata.IsReturn(P2Mnemonic.RET, 0), Is.True);
        Assert.That(P2InstructionMetadata.IsControlFlow(P2Mnemonic.JMP, 1), Is.True);
        Assert.That(P2InstructionMetadata.HasNoRegisterEffect(P2Mnemonic.NOP, 0), Is.True);
        Assert.That(P2InstructionMetadata.IsPureRegisterLocal(P2Mnemonic.ABS, 1), Is.True);
        Assert.That(P2InstructionMetadata.UsesImmediateSyntax(P2Mnemonic.CALL, 1, 0), Is.True);
        Assert.That(P2InstructionMetadata.UsesImmediateSyntax(P2Mnemonic.CALL, 1, 1), Is.False);
        Assert.That(P2InstructionMetadata.UsesImmediateSymbolSyntax(P2Mnemonic.CALL, 1, 0), Is.True);
        Assert.That(P2InstructionMetadata.UsesImmediateSymbolSyntax(P2Mnemonic.MOV, 2, 1), Is.False);
        Assert.That(P2InstructionMetadata.UsesImmediateSymbolSyntax(P2Mnemonic.JINT, 1, 0), Is.True);
        Assert.That(P2InstructionMetadata.UsesImmediateSyntax(P2Mnemonic.GETNIB, 3, 2), Is.True);
        Assert.That(P2InstructionMetadata.IsSpecialRegisterName("PTRA"), Is.True);
        Assert.That(P2InstructionMetadata.IsSpecialRegisterName("NOT_A_REGISTER"), Is.False);
    }

    [Test]
    public void OperandMetadata_ExposesRolesAugPrefixesAndSideEffects()
    {
        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.SETXFRQ, 1, out P2InstructionFormInfo setxfrq), Is.True);
        Assert.That(setxfrq.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.D, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGD)));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.SETNIB, 1, out P2InstructionFormInfo setnibAlias), Is.True);
        Assert.That(setnibAlias.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.S, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGS)));
        Assert.That(setnibAlias.WrittenRegisters, Is.EqualTo(P2WrittenRegister.D));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.JINT, 1, out P2InstructionFormInfo jint), Is.True);
        Assert.That(jint.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.S, 9, P2OperandAccess.Read, true, true, P2AugPrefixKind.AUGS)));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.AKPIN, 1, out P2InstructionFormInfo akpin), Is.True);
        Assert.That(akpin.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.S, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGS)));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.WRPIN, 2, out P2InstructionFormInfo wrpin), Is.True);
        Assert.That(wrpin.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.D, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGD)));
        Assert.That(wrpin.Operand1, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.S, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGS)));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.GETNIB, 3, out P2InstructionFormInfo getnib), Is.True);
        Assert.That(getnib.Operand0, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.D, 9, P2OperandAccess.Write, false, false, P2AugPrefixKind.None)));
        Assert.That(getnib.Operand1, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.S, 9, P2OperandAccess.Read, true, false, P2AugPrefixKind.AUGS)));
        Assert.That(getnib.Operand2, Is.EqualTo(new P2InstructionOperandInfo(P2OperandRole.N, 3, P2OperandAccess.None, true, false, P2AugPrefixKind.None)));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.CALLPA, 2, out P2InstructionFormInfo callpa), Is.True);
        Assert.That(callpa.WrittenRegisters, Is.EqualTo(P2WrittenRegister.PA));
        Assert.That(callpa.HwStackEffect, Is.EqualTo(P2HwStackEffect.Push));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.DIRH, 1, out P2InstructionFormInfo dirh), Is.True);
        Assert.That(dirh.WrittenRegisters, Is.EqualTo(P2WrittenRegister.DIRA | P2WrittenRegister.DIRB));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.DRVH, 1, out P2InstructionFormInfo drvh), Is.True);
        Assert.That(drvh.WrittenRegisters, Is.EqualTo(P2WrittenRegister.DIRA | P2WrittenRegister.DIRB | P2WrittenRegister.OUTA | P2WrittenRegister.OUTB));

        Assert.That(P2InstructionMetadata.TryGetInstructionForm(P2Mnemonic.RET, 0, out P2InstructionFormInfo ret), Is.True);
        Assert.That(ret.HwStackEffect, Is.EqualTo(P2HwStackEffect.Pop));
    }

    [Test]
    public void GetOperandInfoAndAccess_HandlesCallsBranchesAndOutOfRangeCases()
    {
        Assert.That(P2InstructionMetadata.GetOperandInfo((P2Mnemonic)(-1), 1, 0), Is.EqualTo(default(P2InstructionOperandInfo)));
        Assert.That(P2InstructionMetadata.GetOperandInfo(P2Mnemonic.ADD, 2, -1), Is.EqualTo(default(P2InstructionOperandInfo)));
        Assert.That(P2InstructionMetadata.GetOperandInfo(P2Mnemonic.ADD, 2, 2), Is.EqualTo(default(P2InstructionOperandInfo)));

        Assert.That(P2InstructionMetadata.GetOperandAccess((P2Mnemonic)(-1), 1, 0), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.RET, 0, 0), Is.EqualTo(P2OperandAccess.None));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.CALL, 1, 0), Is.EqualTo(P2OperandAccess.Read));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.JMP, 1, 0), Is.EqualTo(P2OperandAccess.Read));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.ADD, 2, 0), Is.EqualTo(P2OperandAccess.ReadWrite));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.ADD, 2, 1), Is.EqualTo(P2OperandAccess.Read));
        Assert.That(P2InstructionMetadata.GetOperandAccess(P2Mnemonic.GETNIB, 3, 2), Is.EqualTo(P2OperandAccess.None));
    }
}
