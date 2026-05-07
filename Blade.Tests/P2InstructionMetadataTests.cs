using System.Linq;
using Blade;
using Blade.IR.Asm;

namespace Blade.Tests;

[TestFixture]
public class P2InstructionMetadataTests
{
    [Test]
    public void MnemonicExtensions_ReturnKnownFormsAndMetadata()
    {
        IReadOnlyCollection<P2InstructionFormInfo> addForms = P2Mnemonic.ADD.GetInstructionForms(2);
        P2InstructionFormInfo add = addForms.Single();

        Assert.That(add.Operands, Has.Count.EqualTo(2));
        Assert.That(add.Operands[0].Role, Is.EqualTo(P2OperandRole.D));
        Assert.That(add.Operands[0].Type, Is.EqualTo(P2OperandType.Regular));
        Assert.That(add.Operands[0].Access, Is.EqualTo(P2OperandAccess.ReadWrite));
        Assert.That(add.Operands[0].BitWidth, Is.EqualTo(9));
        Assert.That(add.Operands[1].Role, Is.EqualTo(P2OperandRole.S));
        Assert.That(add.Operands[1].SupportsImmediate, Is.EqualTo(P2ImmediateSupport.Optional));
        Assert.That(add.Operands[1].AugPrefix, Is.EqualTo(P2AugPrefixKind.AUGS));
        Assert.That(add.AllowedFlagEffects.Contains(P2FlagEffect.None), Is.True);
        Assert.That(add.AllowedFlagEffects.Contains(P2FlagEffect.WC), Is.True);
        Assert.That(add.AllowedFlagEffects.Contains(P2FlagEffect.ORC), Is.False);
        Assert.That(add.WrittenRegisters.Contains(P2WrittenRegister.D), Is.True);
        Assert.That(add.IsControlFlow, Is.False);

        Assert.That(P2Mnemonic.ADD.GetInstructionForms(7), Is.Empty);
    }

    [Test]
    public void ConditionModczFlagAndRegisterExtensions_ReportExpectedValues()
    {
        Assert.That(Enum.TryParse("IF_C", ignoreCase: true, out P2ConditionCode condition), Is.True);
        Assert.That(condition.GetText(), Is.EqualTo("IF_C"));
        Assert.That(P2ConditionCode.IF_A.GetCanonicalName(), Is.EqualTo(P2ConditionCode.IF_NC_AND_NZ));
        Assert.That(P2ConditionCode.IF_A.IsAlias(), Is.True);
        Assert.That(P2ConditionCode.IF_C.IsAlias(), Is.False);

        Assert.That(Enum.TryParse("_set", ignoreCase: true, out P2ModczOperand modcz), Is.True);
        Assert.That(modcz.GetCanonicalName(), Is.EqualTo(P2ModczOperand._SET));
        Assert.That(P2ModczOperand._E.GetCanonicalName(), Is.EqualTo(P2ModczOperand._Z));
        Assert.That(P2ModczOperand._E.IsAlias(), Is.True);

        Assert.That(P2FlagEffectExtensions.TryParse("orz", out P2FlagEffect effect), Is.True);
        Assert.That(effect, Is.EqualTo(P2FlagEffect.ORZ));
        Assert.That(effect.GetFlagName(), Is.EqualTo(P2FlagName.Z));
        Assert.That(effect.GetOperator(), Is.EqualTo(P2FlagOperator.Or));
        Assert.That(P2FlagEffectExtensions.TryParse("none", out P2FlagEffect noneEffect), Is.True);
        Assert.That(noneEffect, Is.EqualTo(P2FlagEffect.None));
        Assert.That(P2FlagEffect.None.GetFlagName(), Is.EqualTo(P2FlagName.None));
        Assert.That(P2FlagEffect.None.GetOperator(), Is.EqualTo(P2FlagOperator.None));
        Assert.That(P2FlagEffectExtensions.TryParse("bogus", out _), Is.False);

        Assert.That(P2SpecialRegister.PTRA.GetText(), Is.EqualTo("PTRA"));
        Assert.That(P2SpecialRegister.PTRA.GetDescription(), Does.Contain("Pointer A"));
    }

    [Test]
    public void OperandAccessAndImmediateExtensions_ReportCapabilities()
    {
        Assert.That(P2OperandAccess.ReadWrite.IsReading(), Is.True);
        Assert.That(P2OperandAccess.ReadWrite.IsWriting(), Is.True);
        Assert.That(P2OperandAccess.Read.IsReading(), Is.True);
        Assert.That(P2OperandAccess.Read.IsWriting(), Is.False);
        Assert.That(P2OperandAccess.Write.IsReading(), Is.False);
        Assert.That(P2OperandAccess.Write.IsWriting(), Is.True);
        Assert.That(P2OperandAccess.None.IsReading(), Is.False);
        Assert.That(P2OperandAccess.None.IsWriting(), Is.False);

        Assert.That(P2ImmediateSupport.No.SupportsImmediate(), Is.False);
        Assert.That(P2ImmediateSupport.No.RequiresImmediate(), Is.False);
        Assert.That(P2ImmediateSupport.Optional.SupportsImmediate(), Is.True);
        Assert.That(P2ImmediateSupport.Optional.RequiresImmediate(), Is.False);
        Assert.That(P2ImmediateSupport.Required.SupportsImmediate(), Is.True);
        Assert.That(P2ImmediateSupport.Required.RequiresImmediate(), Is.True);
    }

    [Test]
    public void Calld_ExposesDistinctTwoOperandForms()
    {
        IReadOnlyCollection<P2InstructionFormInfo> calldForms = P2Mnemonic.CALLD.GetInstructionForms(2);

        Assert.That(calldForms, Has.Count.EqualTo(2));
        Assert.That(
            calldForms.Select(form => form.Operands[0].Role),
            Is.EquivalentTo(new[] { P2OperandRole.AddressRegister, P2OperandRole.D }));
        Assert.That(
            calldForms.Select(form => form.Operands[1].Role),
            Is.EquivalentTo(new[] { P2OperandRole.ADDR, P2OperandRole.S }));
    }

    [Test]
    public void AsmInstructionNode_ResolvesCalldFormsByOperandShape()
    {
        AsmInstructionNode addressRegisterForm = new(
            P2Mnemonic.CALLD,
            [
                new AsmPhysicalRegisterOperand(new P2Register(P2SpecialRegister.PTRA)),
                new AsmImmediateOperand(0),
            ]);
        AsmInstructionNode dataRegisterForm = new(
            P2Mnemonic.CALLD,
            [
                IrTestFactory.AsmRegister(1),
                new AsmImmediateOperand(0),
            ]);

        Assert.That(addressRegisterForm.Form.Operands[0].Role, Is.EqualTo(P2OperandRole.AddressRegister));
        Assert.That(addressRegisterForm.Form.Operands[1].Role, Is.EqualTo(P2OperandRole.ADDR));
        Assert.That(dataRegisterForm.Form.Operands[0].Role, Is.EqualTo(P2OperandRole.D));
        Assert.That(dataRegisterForm.Form.Operands[1].Role, Is.EqualTo(P2OperandRole.S));
    }

    [Test]
    public void AsmInstructionNode_UsesGenericCalldFormForNonAddressRegister()
    {
        AsmInstructionNode genericRegisterForm = new(
            P2Mnemonic.CALLD,
            [
                new AsmPhysicalRegisterOperand(new P2Register(P2SpecialRegister.DIRA)),
                new AsmImmediateOperand(0),
            ]);

        Assert.That(genericRegisterForm.Form.Operands[0].Role, Is.EqualTo(P2OperandRole.D));
    }
}
