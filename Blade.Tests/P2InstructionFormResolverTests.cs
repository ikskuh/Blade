using System.Reflection;
using Blade;
using Blade.IR.Asm;
using Blade.Semantics;

namespace Blade.Tests;

[TestFixture]
public class P2InstructionFormResolverTests
{
    private static readonly Type ResolverType = typeof(P2Mnemonic).Assembly.GetType("Blade.P2InstructionFormResolver", throwOnError: true)!;
    private static readonly Type SyntaxType = typeof(P2Mnemonic).Assembly.GetType("Blade.P2MetadataSyntax", throwOnError: true)!;

    private sealed class TestAsmOperand : AsmOperand
    {
        public override string Format() => "test";
    }

    private sealed class TestInlineAsmOperand : InlineAsmOperand
    {
    }

    [Test]
    public void MetadataSyntax_ParsesCompilerFacingTokens()
    {
        object?[] mnemonicArgs = ["mov", null];
        bool parsedMnemonic = (bool)SyntaxType.GetMethod("TryParseMnemonic", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, mnemonicArgs)!;
        Assert.That(parsedMnemonic, Is.True);
        Assert.That((P2Mnemonic)mnemonicArgs[1]!, Is.EqualTo(P2Mnemonic.MOV));

        object?[] instArgs = ["<INST>", null];
        bool parsedInst = (bool)SyntaxType.GetMethod("TryParseConditionCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, instArgs)!;
        Assert.That(parsedInst, Is.True);
        Assert.That((P2ConditionCode)instArgs[1]!, Is.EqualTo(P2ConditionCode.INST));

        object?[] conditionArgs = ["if_c", null];
        bool parsedCondition = (bool)SyntaxType.GetMethod("TryParseConditionCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, conditionArgs)!;
        Assert.That(parsedCondition, Is.True);
        Assert.That((P2ConditionCode)conditionArgs[1]!, Is.EqualTo(P2ConditionCode.IF_C));
        string prefixText = (string)SyntaxType.GetMethod("GetConditionPrefixText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [(P2ConditionCode)conditionArgs[1]!])!;
        Assert.That(prefixText, Is.EqualTo("IF_C"));

        object?[] flagArgs = ["wz", null];
        bool parsedFlag = (bool)SyntaxType.GetMethod("TryParseFlagEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, flagArgs)!;
        Assert.That(parsedFlag, Is.True);
        Assert.That((P2FlagEffect)flagArgs[1]!, Is.EqualTo(P2FlagEffect.WZ));

        object?[] noneFlagArgs = ["none", null];
        bool parsedNoneFlag = (bool)SyntaxType.GetMethod("TryParseFlagEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, noneFlagArgs)!;
        Assert.That(parsedNoneFlag, Is.False);

        object?[] registerArgs = ["ptra", null];
        bool parsedRegister = (bool)SyntaxType.GetMethod("TryParseSpecialRegister", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, registerArgs)!;
        Assert.That(parsedRegister, Is.True);
        Assert.That((P2SpecialRegister)registerArgs[1]!, Is.EqualTo(P2SpecialRegister.PTRA));
    }

    [Test]
    public void MatchesOperand_CoversAsmOperandShapeBranches()
    {
        P2InstructionOperandInfo optionalRegular = new(P2OperandRole.S, P2OperandType.Regular, 9, P2OperandAccess.Read, P2ImmediateSupport.Optional, P2AugPrefixKind.AUGS);
        P2InstructionOperandInfo requiredBranchTarget = new(P2OperandRole.ADDR, P2OperandType.BranchTarget, 20, P2OperandAccess.Read, P2ImmediateSupport.Required, P2AugPrefixKind.None);
        P2InstructionOperandInfo addressRegister = new(P2OperandRole.AddressRegister, P2OperandType.Regular, 2, P2OperandAccess.None, P2ImmediateSupport.No, P2AugPrefixKind.None);

        Assert.That(InvokeAsmMatch(optionalRegular, new AsmImmediateOperand(1)), Is.True);
        Assert.That(InvokeAsmMatch(optionalRegular, new AsmLabelRefOperand(new ControlFlowLabelSymbol("label"))), Is.True);
        Assert.That(InvokeAsmMatch(optionalRegular, IrTestFactory.AsmRegister(1)), Is.True);
        Assert.That(InvokeAsmMatch(optionalRegular, AsmAltPlaceholderOperand.Register), Is.True);
        Assert.That(InvokeAsmMatch(optionalRegular, AsmAltPlaceholderOperand.Immediate), Is.True);
        Assert.That(InvokeAsmMatch(optionalRegular, new AsmSymbolOperand(new ControlFlowLabelSymbol("label"), AsmSymbolAddressingMode.Register)), Is.True);
        Assert.That(InvokeAsmMatch(requiredBranchTarget, new AsmSymbolOperand(new ControlFlowLabelSymbol("label"), AsmSymbolAddressingMode.Immediate)), Is.True);
        Assert.That(InvokeAsmMatch(addressRegister, new AsmSymbolOperand(P2SpecialRegister.PTRA)), Is.True);
        Assert.That(InvokeAsmMatch(addressRegister, new AsmPhysicalRegisterOperand(new P2Register(P2SpecialRegister.PTRA))), Is.True);
        Assert.That(InvokeAsmMatch(addressRegister, new AsmPhysicalRegisterOperand(new P2Register(1))), Is.False);
        Assert.That(InvokeAsmMatch(addressRegister, new AsmPhysicalRegisterOperand(new P2Register(P2SpecialRegister.DIRA))), Is.False);
        Assert.That(InvokeAsmMatch(optionalRegular, new TestAsmOperand()), Is.False);
    }

    [Test]
    public void MatchesOperand_CoversInlineAsmOperandShapeBranches()
    {
        P2InstructionOperandInfo optionalRegular = new(P2OperandRole.S, P2OperandType.Regular, 9, P2OperandAccess.Read, P2ImmediateSupport.Optional, P2AugPrefixKind.AUGS);
        P2InstructionOperandInfo requiredBranchTarget = new(P2OperandRole.ADDR, P2OperandType.BranchTarget, 20, P2OperandAccess.Read, P2ImmediateSupport.Required, P2AugPrefixKind.None);
        P2InstructionOperandInfo addressRegister = new(P2OperandRole.AddressRegister, P2OperandType.Regular, 2, P2OperandAccess.None, P2ImmediateSupport.No, P2AugPrefixKind.None);

        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmImmediateOperand(1)), Is.True);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Immediate)), Is.True);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmCurrentAddressOperand(InlineAsmAddressingMode.Direct)), Is.True);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmBindingRefOperand(new InlineAsmVarBindingSlot("x"))), Is.True);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmSpecialRegisterOperand(P2SpecialRegister.PTRA)), Is.True);
        Assert.That(InvokeInlineAsmMatch(requiredBranchTarget, new InlineAsmLabelOperand(new ControlFlowLabelSymbol("label"), InlineAsmAddressingMode.Immediate)), Is.True);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new InlineAsmLabelOperand(new ControlFlowLabelSymbol("label"), InlineAsmAddressingMode.Direct)), Is.True);
        Assert.That(InvokeInlineAsmMatch(addressRegister, new InlineAsmSpecialRegisterOperand(P2SpecialRegister.PTRA)), Is.True);
        Assert.That(InvokeInlineAsmMatch(addressRegister, new InlineAsmSpecialRegisterOperand(P2SpecialRegister.DIRA)), Is.False);
        Assert.That(InvokeInlineAsmMatch(optionalRegular, new TestInlineAsmOperand()), Is.False);
    }

    [Test]
    public void Resolve_ReturnsNoMatchWhenNoCandidateMatches()
    {
        object resolution = InvokeAsmResolve(P2Mnemonic.RET, [new TestAsmOperand()]);

        Assert.That(GetResolutionKind(resolution), Is.EqualTo("NoMatch"));
        Assert.That(GetResolutionForm(resolution), Is.Null);
    }

    [Test]
    public void ResolveCore_CanReportAmbiguity()
    {
        P2InstructionOperandInfo operand = new(P2OperandRole.S, P2OperandType.Regular, 9, P2OperandAccess.Read, P2ImmediateSupport.Optional, P2AugPrefixKind.AUGS);
        P2InstructionFormInfo mismatchedCount = new(false, null, Array.Empty<P2InstructionOperandInfo>(), new HashSet<P2WrittenRegister>(), new HashSet<P2FlagEffect>(), P2HwStackEffect.None, false, false, false, false, false, false);
        P2InstructionFormInfo first = new(false, null, new[] { operand }, new HashSet<P2WrittenRegister>(), new HashSet<P2FlagEffect>(), P2HwStackEffect.None, false, false, false, false, false, false);
        P2InstructionFormInfo second = new(false, null, new[] { operand }, new HashSet<P2WrittenRegister>(), new HashSet<P2FlagEffect>(), P2HwStackEffect.None, false, false, false, false, false, false);

        MethodInfo resolveCoreDefinition = ResolverType.GetMethod("ResolveCore", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo resolveCore = resolveCoreDefinition.MakeGenericMethod(typeof(AsmOperand));
        Func<P2InstructionOperandInfo, AsmOperand, int> scorer = static (_, _) => 0;

        object resolution = resolveCore.Invoke(
            null,
            [new[] { mismatchedCount, first, second }, new AsmOperand[] { new TestAsmOperand() }, scorer])!;

        Assert.That(GetResolutionKind(resolution), Is.EqualTo("Ambiguous"));
        Assert.That(GetResolutionForm(resolution), Is.Null);
    }

    private static bool InvokeAsmMatch(P2InstructionOperandInfo operandInfo, AsmOperand operand)
    {
        MethodInfo method = ResolverType.GetMethod("MatchesOperand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(P2InstructionOperandInfo), typeof(AsmOperand)], null)!;
        return (bool)method.Invoke(null, [operandInfo, operand])!;
    }

    private static bool InvokeInlineAsmMatch(P2InstructionOperandInfo operandInfo, InlineAsmOperand operand)
    {
        MethodInfo method = ResolverType.GetMethod("MatchesOperand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(P2InstructionOperandInfo), typeof(InlineAsmOperand)], null)!;
        return (bool)method.Invoke(null, [operandInfo, operand])!;
    }

    private static object InvokeAsmResolve(P2Mnemonic mnemonic, IReadOnlyList<AsmOperand> operands)
    {
        MethodInfo method = ResolverType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(P2Mnemonic), typeof(IReadOnlyList<AsmOperand>)], null)!;
        return method.Invoke(null, [mnemonic, operands])!;
    }

    private static string GetResolutionKind(object resolution)
        => resolution.GetType().GetProperty("Kind")!.GetValue(resolution)!.ToString()!;

    private static object? GetResolutionForm(object resolution)
        => resolution.GetType().GetProperty("Form")!.GetValue(resolution);
}
