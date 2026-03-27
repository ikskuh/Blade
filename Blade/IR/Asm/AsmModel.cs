using System;
using System.Collections.Generic;
using Blade;
using Blade.IR;
using Blade.IR.Lir;
using Blade.Semantics;

namespace Blade.IR.Asm;

internal sealed class AsmSpecialRegisterSymbol : IAsmSymbol
{
    public AsmSpecialRegisterSymbol(P2Register register)
    {
        Register = register;
    }

    public P2Register Register { get; }
    public string Name => Register.ToString();
    public SymbolType SymbolType => SymbolType.RegVariable;
}

internal sealed class AsmTextSymbol : IAsmSymbol
{
    public AsmTextSymbol(string name, SymbolType symbolType = SymbolType.ControlFlowLabel)
    {
        Name = Requires.NotNullOrWhiteSpace(name);
        SymbolType = symbolType;
    }

    public string Name { get; }
    public SymbolType SymbolType { get; }
}

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

public sealed class AsmFunction : IAsmSymbol
{
    public AsmFunction(AsmFunction sourceFunction, IReadOnlyList<AsmNode> nodes)
        : this(Requires.NotNull(sourceFunction).SourceFunction, sourceFunction.CcTier, nodes)
    {
    }

    public AsmFunction(LirFunction sourceFunction, CallingConventionTier ccTier, IReadOnlyList<AsmNode> nodes)
    {
        SourceFunction = Requires.NotNull(sourceFunction);
        CcTier = ccTier;
        Nodes = nodes;
    }

    public LirFunction SourceFunction { get; }
    public FunctionSymbol Symbol => SourceFunction.Symbol;
    public string Name => SourceFunction.Name;
    public bool IsEntryPoint => SourceFunction.IsEntryPoint;
    public CallingConventionTier CcTier { get; }
    public IReadOnlyList<AsmNode> Nodes { get; }
    public SymbolType SymbolType => SymbolType.Function;
}

public abstract class AsmNode
{
}

/// <summary>
/// Marks the beginning of a volatile inline asm region.
/// Acts as an optimization barrier: copy propagation aliases are cleared,
/// and no cross-region instruction fusion or reordering is permitted.
/// Individual instructions within the region are marked IsNonElidable.
/// </summary>
public sealed class AsmVolatileRegionBeginNode : AsmNode;

/// <summary>
/// Marks the end of a volatile inline asm region.
/// </summary>
public sealed class AsmVolatileRegionEndNode : AsmNode;

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
    public AsmLabelNode(string label)
        : this(new ControlFlowLabelSymbol(label))
    {
    }

    public AsmLabelNode(ControlFlowLabelSymbol label)
    {
        Label = Requires.NotNull(label);
    }

    public ControlFlowLabelSymbol Label { get; }
    public string Name => Label.Name;
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
            case AsmRegisterOperand:
            case AsmPlaceOperand:
            case AsmLabelRefOperand:
            case AsmPhysicalRegisterOperand:
                // TODO: Extend operand-shape validation for register/place/label-ref/physical-register
                // forms once metadata exposes all required distinctions for these operand kinds.
                break;
            default:
                Assert.Unreachable();
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
        : this(new VirtualAsmRegister(registerId))
    {
    }

    public AsmRegisterOperand(VirtualAsmRegister register)
    {
        Register = Requires.NotNull(register);
    }

    public VirtualAsmRegister Register { get; }
    public int RegisterId => Register.DebugId;

    public override string Format() => Register.ToString();
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
    public AsmSymbolOperand(string symbolName, AsmSymbolAddressingMode addressingMode)
        : this(new AsmTextSymbol(symbolName), addressingMode)
    {
    }

    public AsmSymbolOperand(P2SpecialRegister register)
        : this(new AsmSpecialRegisterSymbol(new P2Register(register)), AsmSymbolAddressingMode.Register)
    {
    }

    public AsmSymbolOperand(IAsmSymbol symbol, AsmSymbolAddressingMode addressingMode)
    {
        Symbol = Requires.NotNull(symbol);
        AddressingMode = addressingMode;
    }

    public AsmSymbolOperand(ControlFlowLabelSymbol label, AsmSymbolAddressingMode addressingMode)
        : this((IAsmSymbol)label, addressingMode)
    {
    }

    public IAsmSymbol Symbol { get; }
    public string Name => Symbol.Name;
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
    public AsmLabelRefOperand(string label)
        : this(new ControlFlowLabelSymbol(label))
    {
    }

    public AsmLabelRefOperand(ControlFlowLabelSymbol label)
    {
        Label = Requires.NotNull(label);
    }

    public ControlFlowLabelSymbol Label { get; }
    public string Name => Label.Name;

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
    public AsmPhysicalRegisterOperand(int address, string _)
        : this(new P2Register(address))
    {
    }

    public AsmPhysicalRegisterOperand(P2Register register)
    {
        Register = register;
    }

    public P2Register Register { get; }
    public int Address => Register.Address;
    public string Name => Register.ToString();

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
