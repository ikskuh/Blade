using System.Collections.Generic;

namespace Blade.Semantics;

public enum InlineAsmAddressingMode
{
    Direct,
    Immediate,
}

public abstract class InlineAsmLine
{
    protected InlineAsmLine(string rawText)
    {
        RawText = Requires.NotNull(rawText);
    }

    public string RawText { get; }
}

public sealed class InlineAsmLabelLine : InlineAsmLine
{
    public InlineAsmLabelLine(ControlFlowLabelSymbol label, string rawText)
        : base(rawText)
    {
        Label = Requires.NotNull(label);
    }

    public ControlFlowLabelSymbol Label { get; }
}

public sealed class InlineAsmInstructionLine : InlineAsmLine
{
    public InlineAsmInstructionLine(
        P2ConditionCode? condition,
        P2Mnemonic mnemonic,
        IReadOnlyList<InlineAsmOperand> operands,
        P2FlagEffect? flagEffect,
        string rawText)
        : base(rawText)
    {
        Condition = condition;
        Mnemonic = mnemonic;
        Operands = Requires.NotNull(operands);
        FlagEffect = flagEffect;
    }

    public P2ConditionCode? Condition { get; }
    public P2Mnemonic Mnemonic { get; }
    public IReadOnlyList<InlineAsmOperand> Operands { get; }
    public P2FlagEffect? FlagEffect { get; }
}

public abstract class InlineAsmOperand
{
}

public sealed class InlineAsmBindingSlot
{
    public InlineAsmBindingSlot(string placeholderText)
    {
        PlaceholderText = Requires.NotNullOrWhiteSpace(placeholderText);
    }

    public string PlaceholderText { get; }

    public override string ToString() => PlaceholderText;
}

public sealed class InlineAsmBindingRefOperand : InlineAsmOperand
{
    public InlineAsmBindingRefOperand(InlineAsmBindingSlot slot)
    {
        Slot = Requires.NotNull(slot);
    }

    public InlineAsmBindingSlot Slot { get; }
}

public sealed class InlineAsmImmediateOperand : InlineAsmOperand
{
    public InlineAsmImmediateOperand(long value)
    {
        Value = value;
    }

    public long Value { get; }
}

public sealed class InlineAsmCurrentAddressOperand : InlineAsmOperand
{
    public InlineAsmCurrentAddressOperand(InlineAsmAddressingMode addressingMode)
    {
        AddressingMode = addressingMode;
    }

    public InlineAsmAddressingMode AddressingMode { get; }
}

public sealed class InlineAsmLabelOperand : InlineAsmOperand
{
    public InlineAsmLabelOperand(ControlFlowLabelSymbol label, InlineAsmAddressingMode addressingMode)
    {
        Label = Requires.NotNull(label);
        AddressingMode = addressingMode;
    }

    public ControlFlowLabelSymbol Label { get; }
    public InlineAsmAddressingMode AddressingMode { get; }
}

public sealed class InlineAsmSpecialRegisterOperand : InlineAsmOperand
{
    public InlineAsmSpecialRegisterOperand(P2SpecialRegister register)
    {
        Register = register;
    }

    public P2SpecialRegister Register { get; }
}

public sealed class InlineAsmSymbolOperand : InlineAsmOperand
{
    public InlineAsmSymbolOperand(IAsmSymbol symbol, InlineAsmAddressingMode addressingMode)
    {
        Symbol = Requires.NotNull(symbol);
        AddressingMode = addressingMode;
    }

    public IAsmSymbol Symbol { get; }
    public InlineAsmAddressingMode AddressingMode { get; }
}
