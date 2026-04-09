using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Blade;
using Blade.IR;
using Blade.IR.Lir;
using Blade.Semantics;

namespace Blade.IR.Asm;

internal sealed class AsmSpecialRegisterSymbol(P2Register register) : IAsmSymbol
{
    public P2Register Register { get; } = register;
    public string Name => Register.ToString();
    public SymbolType SymbolType => SymbolType.RegVariable;
}

internal sealed class AsmCurrentAddressSymbol : IAsmSymbol
{
    public static AsmCurrentAddressSymbol Instance { get; } = new();

    private AsmCurrentAddressSymbol()
    {
    }

    public string Name => "$";
    public SymbolType SymbolType => SymbolType.ControlFlowLabel;
}

internal sealed class AsmSpillSlotSymbol(int slot) : IAsmSymbol
{
    public int Slot { get; } = Requires.NonNegative(slot);
    public string Name => $"_r{Slot}";
    public SymbolType SymbolType => SymbolType.RegVariable;
}

internal sealed class AsmSharedConstantSymbol(uint value) : IAsmSymbol
{
    public uint Value { get; } = value;
    public string Name => $"c_{Value}";
    public SymbolType SymbolType => SymbolType.RegVariable;
}

internal sealed class AsmFunctionReferenceSymbol(FunctionSymbol function) : IAsmSymbol
{
    public FunctionSymbol Function { get; } = Requires.NotNull(function);
    public string Name => Function.Name;
    public SymbolType SymbolType => SymbolType.Function;
}

public sealed class AsmModule(
    IReadOnlyList<StoragePlace> storagePlaces,
    IReadOnlyList<AsmDataBlock> dataBlocks,
    IReadOnlyList<AsmFunction> functions)
{
    public IReadOnlyList<StoragePlace> StoragePlaces { get; } = storagePlaces;
    public IReadOnlyList<AsmDataBlock> DataBlocks { get; } = dataBlocks;
    public IReadOnlyList<AsmFunction> Functions { get; } = functions;
}

public enum AsmDataBlockKind
{
    Register,
    Constant,
    Lut,
    External,
    Hub,
}

public abstract class AsmDataDefinition(IAsmSymbol symbol)
{
    public IAsmSymbol Symbol { get; } = Requires.NotNull(symbol);
    public abstract int AlignmentBytes { get; }
}

public sealed class AsmAllocatedStorageDefinition(
    IAsmSymbol symbol,
    VariableStorageClass storageClass,
    RuntimeTypeSymbol elementType,
    IReadOnlyList<AsmOperand>? initialValues = null,
    int count = 1,
    bool useHexFormat = false)
    : AsmDataDefinition(symbol)
{
    public VariableStorageClass StorageClass { get; } = storageClass;
    public RuntimeTypeSymbol ElementType { get; } = Requires.NotNull(elementType);
    public IReadOnlyList<AsmOperand>? InitialValues { get; } = initialValues;
    public int Count { get; } = Requires.Positive(count);
    public bool UseHexFormat { get; } = useHexFormat;

    public override int AlignmentBytes => ElementType.GetAlignmentInMemorySpace(StorageClass);

    public AsmDataDirective Directive => StorageClass is VariableStorageClass.Reg or VariableStorageClass.Lut
        ? AsmDataDirective.Long
        : SelectDirective(ElementType);

    private static AsmDataDirective SelectDirective(RuntimeTypeSymbol type)
    {
        if (type.ScalarWidthBits is int width)
        {
            if (width <= 8)
                return AsmDataDirective.Byte;
            if (width <= 16)
                return AsmDataDirective.Word;
        }

        return type.SizeBytes switch
        {
            <= 1 => AsmDataDirective.Byte,
            <= 2 => AsmDataDirective.Word,
            _ => AsmDataDirective.Long,
        };
    }
}

public sealed class AsmExternalBindingDefinition(StoragePlace place) : AsmDataDefinition(place)
{
    public StoragePlace Place { get; } = Requires.NotNull(place);
    public override int AlignmentBytes => 4;
}

public sealed class AsmDataBlock(AsmDataBlockKind kind, IReadOnlyCollection<AsmDataDefinition> definitions)
{
    public AsmDataBlockKind Kind { get; } = kind;
    public IReadOnlyCollection<AsmDataDefinition> Definitions { get; } = Requires.NotNull(definitions);
}

public enum AsmRegisterConstraintKind
{
    FixedPhysicalRegister,
    TiedStoragePlace,
}

public sealed class AsmRegisterConstraint
{
    public AsmRegisterConstraint(P2Register fixedRegister)
    {
        Kind = AsmRegisterConstraintKind.FixedPhysicalRegister;
        FixedRegister = fixedRegister;
    }

    public AsmRegisterConstraint(StoragePlace tiedPlace)
    {
        Kind = AsmRegisterConstraintKind.TiedStoragePlace;
        TiedPlace = Requires.NotNull(tiedPlace);
    }

    public AsmRegisterConstraintKind Kind { get; }
    public P2Register? FixedRegister { get; }
    public StoragePlace? TiedPlace { get; }
}

public sealed class AsmFunction(
    LirFunction sourceFunction,
    CallingConventionTier ccTier,
    IReadOnlyList<AsmNode> nodes,
    IReadOnlyDictionary<VirtualAsmRegister, AsmRegisterConstraint>? registerConstraints = null,
    IReadOnlyList<StoragePlace>? sharedRegisterPlaces = null) : IAsmSymbol
{
    public AsmFunction(AsmFunction sourceFunction, IReadOnlyList<AsmNode> nodes)
        : this(
            Requires.NotNull(sourceFunction).SourceFunction,
            sourceFunction.CcTier,
            nodes,
            sourceFunction.RegisterConstraints,
            sourceFunction.SharedRegisterPlaces)
    {
    }

    public LirFunction SourceFunction { get; } = Requires.NotNull(sourceFunction);
    public FunctionSymbol Symbol => SourceFunction.Symbol;
    public string Name => SourceFunction.Name;
    public bool IsEntryPoint => SourceFunction.IsEntryPoint;
    public CallingConventionTier CcTier { get; } = ccTier;
    public IReadOnlyList<AsmNode> Nodes { get; } = nodes;
    public IReadOnlyDictionary<VirtualAsmRegister, AsmRegisterConstraint> RegisterConstraints { get; } = registerConstraints ?? new Dictionary<VirtualAsmRegister, AsmRegisterConstraint>();
    public IReadOnlyList<StoragePlace> SharedRegisterPlaces { get; } = sharedRegisterPlaces ?? [];
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

public enum AsmDataDirective
{
    Byte,
    Word,
    [SuppressMessage("Design", "CA1720:Identifier 'Long' contains type name ", Justification = "LONG is the flexspin assembly directive")]
    Long,
}

public sealed class AsmLabelNode(ControlFlowLabelSymbol label) : AsmNode
{
    public AsmLabelNode(string label)
        : this(new ControlFlowLabelSymbol(label))
    {
    }

    public ControlFlowLabelSymbol Label { get; } = Requires.NotNull(label);
    public string Name => Label.Name;
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
        bool isNonElidable = false,
        bool isPhiMove = false)
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
        IsPhiMove = isPhiMove;
    }

    public P2Mnemonic Mnemonic { get; }
    public IReadOnlyList<AsmOperand> Operands { get; }
    public P2ConditionCode? Condition { get; }
    public P2FlagEffect FlagEffect { get; }
    public bool IsNonElidable { get; }
    public bool IsPhiMove { get; }

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
            case AsmLabelRefOperand:
            case AsmPhysicalRegisterOperand:
            case AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Register }:
                // TODO: Extend operand-shape validation for register/label-ref/physical-register
                // forms once metadata exposes all required distinctions for these operand kinds.
                break;
            case AsmAltPlaceholderOperand { Kind: AltPlaceholderKind.Immediate }:
                Assert.Invariant(operandInfo.SupportsImmediateSyntax, $"Operand {operandIndex} of '{form.Mnemonic}' does not allow immediate values.");
                break;
            default:
                Assert.Unreachable(); // pragma: force-coverage
                break; // pragma: force-coverage
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
public sealed class AsmRegisterOperand(VirtualAsmRegister register) : AsmOperand
{
    public VirtualAsmRegister Register { get; } = Requires.NotNull(register);

    public override string Format() => "%r?";
}

/// <summary>
/// Immediate value operand (#value). At ASMIR level, any value is allowed;
/// the legalize pass handles range restrictions.
/// </summary>
public sealed class AsmImmediateOperand(long value) : AsmOperand
{
    public long Value { get; } = value;

    public override string Format() => $"#{Value}";
}

/// <summary>
/// Symbol/label reference operand (#label or symbol name).
/// </summary>
public sealed class AsmSymbolOperand(IAsmSymbol symbol, AsmSymbolAddressingMode addressingMode, int offset = 0) : AsmOperand
{
    public AsmSymbolOperand(P2SpecialRegister register)
        : this(new AsmSpecialRegisterSymbol(new P2Register(register)), AsmSymbolAddressingMode.Register)
    {
    }

    public AsmSymbolOperand(ControlFlowLabelSymbol label, AsmSymbolAddressingMode addressingMode)
        : this((IAsmSymbol)label, addressingMode)
    {
    }

    public IAsmSymbol Symbol { get; } = Requires.NotNull(symbol);
    public string Name => Symbol.Name;
    public AsmSymbolAddressingMode AddressingMode { get; } = addressingMode;
    public int Offset { get; } = offset;

    public override string Format() => AddressingMode switch
    {
        AsmSymbolAddressingMode.Immediate => Offset == 0 ? $"#{Name}" : $"#{Name}{FormatOffset(Offset)}",
        AsmSymbolAddressingMode.Register => Offset == 0 ? Name : $"{Name}{FormatOffset(Offset)}",
        _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
    };

    private static string FormatOffset(int offset)
    {
        if (offset > 0)
            return $" + {offset}";

        return $" - {-offset}";
    }
}

/// <summary>
/// Label-relative reference operand (@label) used by FlexSpin's REP instruction
/// to automatically calculate the instruction count to a target label.
/// </summary>
public sealed class AsmLabelRefOperand(ControlFlowLabelSymbol label) : AsmOperand
{
    public ControlFlowLabelSymbol Label { get; } = Requires.NotNull(label);
    public string Name => Label.Name;

    public override string Format() => $"@{Name}";
}

/// <summary>
/// Physical register operand for post-register-allocation output.
/// Uses named P2 registers (e.g., "r0", "PA", "PB", "PTRA").
/// </summary>
public sealed class AsmPhysicalRegisterOperand(P2Register register) : AsmOperand
{
    public P2Register Register { get; } = register;
    public int Address => Register.Address;
    public string Name => Register.ToString();

    public override string Format() => Name;
}

/// <summary>
/// An operand that is used when the actual value of the operand is provided through any <c>ALTx</c> instruction.
/// </summary>
public sealed class AsmAltPlaceholderOperand : AsmOperand
{
    /// <summary>
    /// The placeholder value for a replaced immediate operand.
    /// </summary>
    public static AsmAltPlaceholderOperand Immediate { get; } = new AsmAltPlaceholderOperand(AltPlaceholderKind.Immediate);

    /// <summary>
    /// The placeholder value for a replaced register operand.
    /// </summary>
    public static AsmAltPlaceholderOperand Register { get; } = new AsmAltPlaceholderOperand(AltPlaceholderKind.Register);

    private AsmAltPlaceholderOperand(AltPlaceholderKind kind)
    {
        Kind = kind;
    }

    public AltPlaceholderKind Kind { get; }

    public override string Format() => Kind switch
    {
        AltPlaceholderKind.Immediate => "#<altered>",
        AltPlaceholderKind.Register => "<altered>",
        _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
    };
}

public enum AltPlaceholderKind
{
    /// <summary>
    /// The placeholder will replace an immediate value.
    /// </summary>
    Immediate,

    /// <summary>
    /// The placeholder will replace a register value
    /// </summary>
    Register,
}

/// <summary>
/// Comment-only pseudo-node for debugging/readability in ASMIR output.
/// </summary>
public sealed class AsmCommentNode(string text) : AsmNode
{
    public string Text { get; } = text;
}
