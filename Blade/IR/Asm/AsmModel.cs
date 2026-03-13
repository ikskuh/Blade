using System;
using System.Collections.Generic;

namespace Blade.IR.Asm;

public sealed class AsmModule
{
    public AsmModule(IReadOnlyList<AsmFunction> functions)
    {
        Functions = functions;
    }

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

/// <summary>
/// Represents a PASM2 instruction with real P2 mnemonics, virtual registers,
/// and optional flag effects / condition predicates.
/// </summary>
public sealed class AsmInstructionNode : AsmNode
{
    public AsmInstructionNode(
        string opcode,
        IReadOnlyList<AsmOperand> operands,
        string? predicate = null,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        Opcode = opcode;
        Operands = operands;
        Predicate = predicate;
        FlagEffect = flagEffect;
    }

    public string Opcode { get; }
    public IReadOnlyList<AsmOperand> Operands { get; }
    public string? Predicate { get; }
    public AsmFlagEffect FlagEffect { get; }
}

/// <summary>
/// Flag effect suffix for P2 instructions.
/// </summary>
[Flags]
public enum AsmFlagEffect
{
    None = 0,
    WC = 1,
    WZ = 2,
    WCZ = WC | WZ,
}

/// <summary>
/// Typed operand for ASMIR instructions.
/// </summary>
public abstract class AsmOperand
{
    public abstract string Format();
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
    public AsmSymbolOperand(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public override string Format() => $"#{Name}";
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
