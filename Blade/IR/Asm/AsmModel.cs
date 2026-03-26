using System;
using System.Collections.Generic;
using Blade;
using Blade.IR;

namespace Blade.IR.Asm;

public sealed class AsmModule
{
    public AsmModule(IReadOnlyList<AsmFunction> functions)
        : this([], functions)
    {
    }

    public AsmModule(IReadOnlyList<StoragePlace> storagePlaces, IReadOnlyList<AsmFunction> functions)
    {
        StoragePlaces = storagePlaces;
        Functions = functions;
    }

    public IReadOnlyList<StoragePlace> StoragePlaces { get; }
    public IReadOnlyList<AsmFunction> Functions { get; }
}

public sealed class AsmFunction
{
    public AsmFunction(
        string name,
        bool isEntryPoint,
        CallingConventionTier ccTier,
        IReadOnlyList<AsmNode> nodes)
    {
        Name = name;
        IsEntryPoint = isEntryPoint;
        CcTier = ccTier;
        Nodes = nodes;
    }

    public string Name { get; }
    public bool IsEntryPoint { get; }
    public CallingConventionTier CcTier { get; }
    public IReadOnlyList<AsmNode> Nodes { get; }
}

public abstract class AsmNode
{
}

public sealed class AsmDirectiveNode : AsmNode
{
    public AsmDirectiveNode(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed class AsmLabelNode : AsmNode
{
    public AsmLabelNode(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed class AsmImplicitUseNode : AsmNode
{
    public AsmImplicitUseNode(IReadOnlyList<AsmOperand> operands)
    {
        Operands = operands;
    }

    public IReadOnlyList<AsmOperand> Operands { get; }
}

/// <summary>
/// Represents a PASM2 instruction with real P2 mnemonics, virtual registers,
/// and optional flag effects / condition predicates.
/// </summary>
public sealed class AsmInstructionNode : AsmNode
{
    public AsmInstructionNode(
        P2Mnemonic mnemonic,
        IReadOnlyList<AsmOperand> operands,
        P2ConditionCode? condition = null,
        P2FlagEffect flagEffect = P2FlagEffect.None,
        bool isNonElidable = false)
    {
        IReadOnlyList<AsmOperand> checkedOperands = Requires.NotNull(operands);
        bool hasForm = P2InstructionMetadata.TryGetInstructionForm(mnemonic, checkedOperands.Count, out P2InstructionFormInfo form);
        Assert.Invariant(hasForm, $"Instruction form '{mnemonic}/{checkedOperands.Count}' must exist.");
        for (int i = 0; i < checkedOperands.Count; i++)
        {
            ValidateOperandCompatibility(form, checkedOperands[i], i);
        }

        Mnemonic = mnemonic;
        Operands = checkedOperands;
        Condition = condition;
        FlagEffect = flagEffect;
        IsNonElidable = isNonElidable;
    }

    public P2Mnemonic Mnemonic { get; }
    public IReadOnlyList<AsmOperand> Operands { get; }
    public P2ConditionCode? Condition { get; }
    public P2FlagEffect FlagEffect { get; }
    public bool IsNonElidable { get; }

    public string Opcode => P2InstructionMetadata.GetMnemonicText(Mnemonic);
    public string? Predicate => Condition is P2ConditionCode condition
        ? P2InstructionMetadata.GetConditionPrefixText(condition)
        : null;

    private static void ValidateOperandCompatibility(P2InstructionFormInfo form, AsmOperand operand, int operandIndex)
    {
        P2InstructionOperandInfo operandInfo = form.GetOperandInfo(operandIndex);
        switch (operand)
        {
            case AsmImmediateOperand:
                Assert.Invariant(operandInfo.SupportsImmediateSyntax, $"Operand {operandIndex} of '{form.Mnemonic}' does not allow immediate values.");
                break;
            case AsmSymbolOperand symbol when symbol.AddressingMode == AsmSymbolAddressingMode.Immediate:
                Assert.Invariant(operandInfo.SupportsImmediateSyntax, $"Operand {operandIndex} of '{form.Mnemonic}' does not allow immediate symbols.");
                break;
            case AsmSymbolOperand symbol when symbol.AddressingMode == AsmSymbolAddressingMode.Register:
                Assert.Invariant(!IsImmediateOnlyOperand(operandInfo), $"Operand {operandIndex} of '{form.Mnemonic}' requires immediate syntax.");
                break;
        }
    }

    private static bool IsImmediateOnlyOperand(P2InstructionOperandInfo operandInfo)
        => operandInfo.SupportsImmediateSyntax
            && operandInfo.Access == P2OperandAccess.None
            && operandInfo.Role == P2OperandRole.N;

}

/// <summary>
/// Typed operand for ASMIR instructions.
/// </summary>
public abstract class AsmOperand
{
    public abstract string Format();
}

public enum AsmSymbolAddressingMode
{
    Immediate,
    Register,
}

/// <summary>
/// Virtual register operand (%r0, %r1, ...).
/// </summary>
public sealed class AsmRegisterOperand : AsmOperand
{
    public AsmRegisterOperand(int registerId)
    {
        RegisterId = registerId;
    }

    public int RegisterId { get; }

    public override string Format() => $"%r{RegisterId}";
}

/// <summary>
/// Immediate value operand (#value). At ASMIR level, any value is allowed;
/// the legalize pass handles range restrictions.
/// </summary>
public sealed class AsmImmediateOperand : AsmOperand
{
    public AsmImmediateOperand(long value)
    {
        Value = value;
    }

    public long Value { get; }

    public override string Format() => $"#{Value}";
}

/// <summary>
/// Symbol/label reference operand (#label or symbol name).
/// </summary>
public sealed class AsmSymbolOperand : AsmOperand
{
    public AsmSymbolOperand(P2SpecialRegister register)
        : this(register.ToString(), AsmSymbolAddressingMode.Register)
    {
    }

    public AsmSymbolOperand(string name, AsmSymbolAddressingMode addressingMode)
    {
        Name = name;
        AddressingMode = addressingMode;
    }

    public string Name { get; }
    public AsmSymbolAddressingMode AddressingMode { get; }

    public override string Format() => AddressingMode switch
    {
        AsmSymbolAddressingMode.Immediate => $"#{Name}",
        AsmSymbolAddressingMode.Register => Name,
        _ => Assert.UnreachableValue<string>(),
    };
}

/// <summary>
/// Label-relative reference operand (@label) used by FlexSpin's REP instruction
/// to automatically calculate the instruction count to a target label.
/// </summary>
public sealed class AsmLabelRefOperand : AsmOperand
{
    public AsmLabelRefOperand(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public override string Format() => $"@{Name}";
}

public sealed class AsmPlaceOperand : AsmOperand
{
    public AsmPlaceOperand(StoragePlace place)
    {
        Place = place;
    }

    public StoragePlace Place { get; }

    public override string Format() => Place.EmittedName;
}

/// <summary>
/// Physical register operand for post-register-allocation output.
/// Uses named P2 registers (e.g., "r0", "PA", "PB", "PTRA").
/// </summary>
public sealed class AsmPhysicalRegisterOperand : AsmOperand
{
    public AsmPhysicalRegisterOperand(int address, string name)
    {
        Address = address;
        Name = name;
    }

    public int Address { get; }
    public string Name { get; }

    public override string Format() => Name;
}

/// <summary>
/// Comment-only pseudo-node for debugging/readability in ASMIR output.
/// </summary>
public sealed class AsmCommentNode : AsmNode
{
    public AsmCommentNode(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

/// <summary>
/// Raw inline assembly text node. Emitted verbatim into the final PASM2 output.
/// `{name}` placeholders are resolved during register allocation.
/// </summary>
public sealed class AsmInlineTextNode : AsmNode
{
    public AsmInlineTextNode(
        string text,
        IReadOnlyDictionary<string, AsmOperand>? bindings = null,
        IReadOnlyDictionary<string, string>? localLabels = null)
    {
        Text = text;
        Bindings = bindings ?? new Dictionary<string, AsmOperand>(StringComparer.Ordinal);
        LocalLabels = localLabels ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Text { get; }
    public IReadOnlyDictionary<string, AsmOperand> Bindings { get; }
    public IReadOnlyDictionary<string, string> LocalLabels { get; }
}
