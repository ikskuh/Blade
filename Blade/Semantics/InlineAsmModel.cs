using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Blade.Semantics;

public enum InlineAsmAddressingMode
{
    Direct,
    Immediate,
}

public abstract class InlineAsmLine(string? trailingComment)
{
    /// <summary>
    /// Optional `// ...` comment trailing this line in the original source.
    /// Preserved only for dump/codegen output; no semantic meaning.
    /// </summary>
    public string? TrailingComment { get; } = trailingComment;
}

public sealed class InlineAsmCommentLine(string comment) : InlineAsmLine(null)
{
    public string Comment { get; } = Requires.NotNull(comment);
}

public sealed class InlineAsmLabelLine(ControlFlowLabelSymbol label, string? trailingComment)
    : InlineAsmLine(trailingComment)
{
    public ControlFlowLabelSymbol Label { get; } = Requires.NotNull(label);
}

public sealed class InlineAsmInstructionLine(
    P2ConditionCode? condition,
    P2Mnemonic mnemonic,
    IReadOnlyList<InlineAsmOperand> operands,
    P2FlagEffect? flagEffect,
    string? trailingComment) : InlineAsmLine(trailingComment)
{
    public P2ConditionCode? Condition { get; } = condition;
    public P2Mnemonic Mnemonic { get; } = mnemonic;
    public IReadOnlyList<InlineAsmOperand> Operands { get; } = Requires.NotNull(operands);
    public P2FlagEffect? FlagEffect { get; } = flagEffect;
}

public enum InlineAsmDataDirective
{
    Byte,
    Word,
    [SuppressMessage("Design", "CA1720:Identifier 'Long' contains type name ", Justification = "LONG is the PASM2 data directive")]
    Long,
}

public sealed class InlineAsmDataLine(
    InlineAsmDataDirective directive,
    IReadOnlyList<InlineAsmDataValue> values,
    string? trailingComment) : InlineAsmLine(trailingComment)
{
    public InlineAsmDataDirective Directive { get; } = directive;
    public IReadOnlyList<InlineAsmDataValue> Values { get; } = Requires.NotNull(values);
}

public abstract class InlineAsmDataValue
{
    public abstract InlineAsmAddressingMode AddressingMode { get; }
}

public sealed class InlineAsmDataBindingValue(InlineAsmBindingSlot slot, InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public InlineAsmBindingSlot Slot { get; } = Requires.NotNull(slot);
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmDataIntegerValue(long value, InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public long Value { get; } = value;
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmDataCurrentAddressValue(InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmDataLabelValue(ControlFlowLabelSymbol label, InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public ControlFlowLabelSymbol Label { get; } = Requires.NotNull(label);
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmDataSpecialRegisterValue(P2SpecialRegister register, InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public P2SpecialRegister Register { get; } = register;
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmDataRawSymbolValue(string name, InlineAsmAddressingMode addressingMode) : InlineAsmDataValue
{
    public string Name { get; } = Requires.NotNullOrWhiteSpace(name);
    public override InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public abstract class InlineAsmOperand
{
}

public abstract class InlineAsmBindingSlot
{
    public abstract string PlaceholderText { get; }

    [ExcludeFromCodeCoverage]
    public override string ToString() => PlaceholderText;
}

public sealed class InlineAsmVarBindingSlot(string placeholderText) : InlineAsmBindingSlot
{

    /// <summary>
    /// Original placeholder spelling for diagnostics and dumps only.
    /// Binding identity is the slot object itself, not this text.
    /// </summary>
    public override string PlaceholderText { get; } = Requires.NotNullOrWhiteSpace(placeholderText);
}

public sealed class InlineAsmTempBindingSlot : InlineAsmBindingSlot
{
    public InlineAsmTempBindingSlot(int tempId)
    {
        Requires.NonNegative(tempId);
        TempId = tempId;
    }

    public int TempId { get; }
    public override string PlaceholderText => "%" + TempId.ToString(CultureInfo.InvariantCulture);
}

public sealed class InlineAsmBindingRefOperand(InlineAsmBindingSlot slot) : InlineAsmOperand
{
    public InlineAsmBindingSlot Slot { get; } = Requires.NotNull(slot);
}

public sealed class InlineAsmImmediateOperand(long value) : InlineAsmOperand
{
    public long Value { get; } = value;
}

public sealed class InlineAsmCurrentAddressOperand(InlineAsmAddressingMode addressingMode) : InlineAsmOperand
{
    public InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmLabelOperand(ControlFlowLabelSymbol label, InlineAsmAddressingMode addressingMode) : InlineAsmOperand
{
    public ControlFlowLabelSymbol Label { get; } = Requires.NotNull(label);
    public InlineAsmAddressingMode AddressingMode { get; } = addressingMode;
}

public sealed class InlineAsmSpecialRegisterOperand(P2SpecialRegister register) : InlineAsmOperand
{
    public P2SpecialRegister Register { get; } = register;
}
