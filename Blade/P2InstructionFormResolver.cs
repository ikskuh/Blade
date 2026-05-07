using System;
using System.Collections.Generic;
using Blade.IR.Asm;
using Blade.Semantics;

namespace Blade;

internal enum P2InstructionFormResolutionKind
{
    Success,
    NoMatch,
    Ambiguous,
}

internal readonly record struct P2InstructionFormResolution
{
    private P2InstructionFormResolution(P2InstructionFormResolutionKind kind, P2InstructionFormInfo? form)
    {
        Kind = kind;
        Form = form;
    }

    public P2InstructionFormResolutionKind Kind { get; }
    public P2InstructionFormInfo? Form { get; }

    public static P2InstructionFormResolution Success(P2InstructionFormInfo form)
        => new(P2InstructionFormResolutionKind.Success, Requires.NotNull(form));

    public static P2InstructionFormResolution NoMatch()
        => new(P2InstructionFormResolutionKind.NoMatch, null);

    public static P2InstructionFormResolution Ambiguous()
        => new(P2InstructionFormResolutionKind.Ambiguous, null);
}

/// <summary>
/// Resolves concrete instruction forms from the generated metadata using operand shapes.
/// </summary>
internal static class P2InstructionFormResolver
{
    public static P2InstructionFormResolution Resolve(P2Mnemonic mnemonic, IReadOnlyList<AsmOperand> operands)
    {
        Requires.NotNull(operands);
        return ResolveCore(mnemonic.GetInstructionForms(operands.Count), operands, GetOperandMatchScore);
    }

    public static P2InstructionFormResolution Resolve(P2Mnemonic mnemonic, IReadOnlyList<InlineAsmOperand> operands)
    {
        Requires.NotNull(operands);
        return ResolveCore(mnemonic.GetInstructionForms(operands.Count), operands, GetOperandMatchScore);
    }

    public static bool MatchesOperand(P2InstructionOperandInfo operandInfo, AsmOperand operand)
        => GetOperandMatchScore(operandInfo, operand) >= 0;

    public static bool MatchesOperand(P2InstructionOperandInfo operandInfo, InlineAsmOperand operand)
        => GetOperandMatchScore(operandInfo, operand) >= 0;

    private static int GetOperandMatchScore(P2InstructionOperandInfo operandInfo, AsmOperand operand)
    {
        Requires.NotNull(operand);

        int roleScore = GetSpecialOperandRoleScore(operandInfo.Role, operand);
        if (roleScore < 0)
            return -1;

        if (operand is AsmImmediateOperand or AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate })
        {
            return operandInfo.SupportsImmediate.SupportsImmediate()
                ? roleScore + GetImmediateSpecificityScore(operandInfo.SupportsImmediate)
                : -1;
        }

        if (operand is AsmLabelRefOperand)
            return !operandInfo.SupportsImmediate.RequiresImmediate() ? roleScore : -1;

        if (operand is AsmRegisterOperand or AsmFlagOperand or AsmPhysicalRegisterOperand or AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register })
        {
            return !operandInfo.SupportsImmediate.RequiresImmediate() && operandInfo.Type != P2OperandType.BranchTarget
                ? roleScore
                : -1;
        }

        if (operand is AsmSymbolOperand symbolOperand)
        {
            return MatchesSymbolOperand(operandInfo, symbolOperand)
                ? roleScore + GetSymbolSpecificityScore(operandInfo, symbolOperand)
                : -1;
        }

        return -1;
    }

    private static int GetOperandMatchScore(P2InstructionOperandInfo operandInfo, InlineAsmOperand operand)
    {
        Requires.NotNull(operand);

        int roleScore = GetSpecialOperandRoleScore(operandInfo.Role, operand);
        if (roleScore < 0)
            return -1;

        if (operand is InlineAsmImmediateOperand)
        {
            return operandInfo.SupportsImmediate.SupportsImmediate()
                ? roleScore + GetImmediateSpecificityScore(operandInfo.SupportsImmediate)
                : -1;
        }

        if (operand is InlineAsmCurrentAddressOperand { AddressingMode: InlineAsmAddressingMode.Immediate })
        {
            return operandInfo.SupportsImmediate.SupportsImmediate()
                ? roleScore + GetImmediateSpecificityScore(operandInfo.SupportsImmediate)
                : -1;
        }

        if (operand is InlineAsmCurrentAddressOperand { AddressingMode: InlineAsmAddressingMode.Direct })
            return !operandInfo.SupportsImmediate.RequiresImmediate() ? roleScore : -1;

        if (operand is InlineAsmBindingRefOperand)
        {
            return !operandInfo.SupportsImmediate.RequiresImmediate() && operandInfo.Type != P2OperandType.BranchTarget
                ? roleScore
                : -1;
        }

        if (operand is InlineAsmSpecialRegisterOperand)
        {
            return !operandInfo.SupportsImmediate.RequiresImmediate() && operandInfo.Type != P2OperandType.BranchTarget
                ? roleScore
                : -1;
        }

        if (operand is InlineAsmLabelOperand labelOperand)
        {
            return MatchesInlineAsmLabelOperand(operandInfo, labelOperand)
                ? roleScore + GetInlineAsmLabelSpecificityScore(operandInfo, labelOperand)
                : -1;
        }

        return -1;
    }

    private static P2InstructionFormResolution ResolveCore<TOperand>(
        IReadOnlyCollection<P2InstructionFormInfo> candidates,
        IReadOnlyList<TOperand> operands,
        Func<P2InstructionOperandInfo, TOperand, int> scorer)
    {
        P2InstructionFormInfo? match = null;
        int bestScore = -1;
        bool ambiguous = false;
        foreach (P2InstructionFormInfo candidate in candidates)
        {
            if (candidate.Operands.Count != operands.Count)
                continue;

            bool allMatch = true;
            int candidateScore = 0;
            for (int i = 0; i < operands.Count; i++)
            {
                int operandScore = scorer(candidate.Operands[i], operands[i]);
                if (operandScore < 0)
                {
                    allMatch = false;
                    break;
                }

                candidateScore += operandScore;
            }

            if (!allMatch)
                continue;

            if (candidateScore > bestScore)
            {
                match = candidate;
                bestScore = candidateScore;
                ambiguous = false;
                continue;
            }

            if (candidateScore == bestScore)
                ambiguous = true;
        }

        if (ambiguous)
            return P2InstructionFormResolution.Ambiguous();

        return match is null
            ? P2InstructionFormResolution.NoMatch()
            : P2InstructionFormResolution.Success(match);
    }

    private static bool MatchesSymbolOperand(P2InstructionOperandInfo operandInfo, AsmSymbolOperand operand)
    {
        if (operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
        {
            return operandInfo.SupportsImmediate.SupportsImmediate();
        }

        return !operandInfo.SupportsImmediate.RequiresImmediate();
    }

    private static bool MatchesInlineAsmLabelOperand(P2InstructionOperandInfo operandInfo, InlineAsmLabelOperand operand)
    {
        if (operand.AddressingMode == InlineAsmAddressingMode.Immediate)
        {
            return operandInfo.SupportsImmediate.SupportsImmediate();
        }

        return !operandInfo.SupportsImmediate.RequiresImmediate();
    }

    private static int GetSpecialOperandRoleScore(P2OperandRole role, AsmOperand operand)
    {
        if (role != P2OperandRole.AddressRegister)
        {
            return 0;
        }

        return operand switch
        {
            AsmPhysicalRegisterOperand physicalRegister when IsAddressRegister(physicalRegister.Register) => 1,
            AsmSymbolOperand { Symbol: AsmSpecialRegisterSymbol specialRegister } when IsAddressRegister(specialRegister.Register) => 1,
            _ => -1,
        };
    }

    private static int GetSpecialOperandRoleScore(P2OperandRole role, InlineAsmOperand operand)
    {
        if (role != P2OperandRole.AddressRegister)
        {
            return 0;
        }

        return operand is InlineAsmSpecialRegisterOperand specialRegisterOperand
            && IsAddressRegister(new P2Register(specialRegisterOperand.Register))
            ? 1
            : -1;
    }

    private static int GetImmediateSpecificityScore(P2ImmediateSupport immediateSupport)
        => immediateSupport == P2ImmediateSupport.Required ? 1 : 0;

    private static int GetSymbolSpecificityScore(P2InstructionOperandInfo operandInfo, AsmSymbolOperand operand)
    {
        int score = operand.Symbol is ControlFlowLabelSymbol && operandInfo.Type == P2OperandType.BranchTarget
            ? 1
            : 0;

        if (operand.AddressingMode == AsmSymbolAddressingMode.Immediate)
            score += GetImmediateSpecificityScore(operandInfo.SupportsImmediate);

        return score;
    }

    private static int GetInlineAsmLabelSpecificityScore(P2InstructionOperandInfo operandInfo, InlineAsmLabelOperand operand)
    {
        int score = operandInfo.Type == P2OperandType.BranchTarget ? 1 : 0;
        if (operand.AddressingMode == InlineAsmAddressingMode.Immediate)
            score += GetImmediateSpecificityScore(operandInfo.SupportsImmediate);

        return score;
    }

    private static bool IsAddressRegister(P2Register register)
    {
        if (!register.IsSpecial)
            return false;

        return (P2SpecialRegister)register.Address is P2SpecialRegister.PA
            or P2SpecialRegister.PB
            or P2SpecialRegister.PTRA
            or P2SpecialRegister.PTRB;
    }
}
