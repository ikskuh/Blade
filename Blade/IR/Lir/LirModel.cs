using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR.Lir;

public readonly record struct LirVirtualRegister(int Id)
{
    public override string ToString() => $"%r{Id}";
}

public sealed class LirModule
{
    public LirModule(IReadOnlyList<LirFunction> functions)
        : this([], functions)
    {
    }

    public LirModule(IReadOnlyList<StoragePlace> storagePlaces, IReadOnlyList<LirFunction> functions)
    {
        StoragePlaces = storagePlaces;
        Functions = functions;
    }

    public IReadOnlyList<StoragePlace> StoragePlaces { get; }
    public IReadOnlyList<LirFunction> Functions { get; }
}

public sealed class LirFunction
{
    public LirFunction(
        string name,
        bool isEntryPoint,
        FunctionKind kind,
        IReadOnlyList<TypeSymbol> returnTypes,
        IReadOnlyList<LirBlock> blocks,
        IReadOnlyList<ReturnSlot>? returnSlots = null)
    {
        Name = name;
        IsEntryPoint = isEntryPoint;
        Kind = kind;
        ReturnTypes = returnTypes;
        ReturnSlots = returnSlots ?? [];
        Blocks = blocks;
    }

    public string Name { get; }
    public bool IsEntryPoint { get; }
    public FunctionKind Kind { get; }
    public IReadOnlyList<TypeSymbol> ReturnTypes { get; }
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; }
    public IReadOnlyList<LirBlock> Blocks { get; }
}

public sealed class LirBlock
{
    public LirBlock(
        string label,
        IReadOnlyList<LirBlockParameter> parameters,
        IReadOnlyList<LirInstruction> instructions,
        LirTerminator terminator)
    {
        Label = label;
        Parameters = parameters;
        Instructions = instructions;
        Terminator = terminator;
    }

    public string Label { get; }
    public IReadOnlyList<LirBlockParameter> Parameters { get; }
    public IReadOnlyList<LirInstruction> Instructions { get; }
    public LirTerminator Terminator { get; }
}

public sealed class LirBlockParameter
{
    public LirBlockParameter(LirVirtualRegister register, string name, TypeSymbol type)
    {
        Register = register;
        Name = name;
        Type = type;
    }

    public LirVirtualRegister Register { get; }
    public string Name { get; }
    public TypeSymbol Type { get; }
}

public abstract class LirOperand
{
}

public sealed class LirRegisterOperand : LirOperand
{
    public LirRegisterOperand(LirVirtualRegister register)
    {
        Register = register;
    }

    public LirVirtualRegister Register { get; }
}

public sealed class LirImmediateOperand : LirOperand
{
    public LirImmediateOperand(object? value, TypeSymbol type)
    {
        Value = value;
        Type = type;
    }

    public object? Value { get; }
    public TypeSymbol Type { get; }
}

public sealed class LirSymbolOperand : LirOperand
{
    public LirSymbolOperand(string symbol)
    {
        Symbol = symbol;
    }

    public string Symbol { get; }
}

public sealed class LirPlaceOperand : LirOperand
{
    public LirPlaceOperand(StoragePlace place)
    {
        Place = place;
    }

    public StoragePlace Place { get; }
}

public abstract class LirInstruction
{
    protected LirInstruction(
        string opcode,
        LirVirtualRegister? destination,
        TypeSymbol? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        string? predicate,
        bool writesC,
        bool writesZ,
        TextSpan span)
    {
        Opcode = opcode;
        Destination = destination;
        ResultType = resultType;
        Operands = operands;
        HasSideEffects = hasSideEffects;
        Predicate = predicate;
        WritesC = writesC;
        WritesZ = writesZ;
        Span = span;
    }

    public string Opcode { get; }
    public LirVirtualRegister? Destination { get; }
    public TypeSymbol? ResultType { get; }
    public IReadOnlyList<LirOperand> Operands { get; }
    public bool HasSideEffects { get; }
    public string? Predicate { get; }
    public bool WritesC { get; }
    public bool WritesZ { get; }
    public TextSpan Span { get; }
}

public sealed class LirOpInstruction : LirInstruction
{
    public LirOpInstruction(
        string opcode,
        LirVirtualRegister? destination,
        TypeSymbol? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        string? predicate,
        bool writesC,
        bool writesZ,
        TextSpan span)
        : base(opcode, destination, resultType, operands, hasSideEffects, predicate, writesC, writesZ, span)
    {
    }
}

public sealed class LirInlineAsmBinding
{
    public LirInlineAsmBinding(string name, LirOperand operand, InlineAsmBindingAccess access)
    {
        Name = name;
        Operand = operand;
        Access = access;
    }

    public string Name { get; }
    public LirOperand Operand { get; }
    public InlineAsmBindingAccess Access { get; }
}

public sealed class LirInlineAsmInstruction : LirInstruction
{
    public LirInlineAsmInstruction(
        AsmVolatility volatility,
        string body,
        string? flagOutput,
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines,
        IReadOnlyList<LirInlineAsmBinding> bindings,
        TextSpan span)
        : base(
            opcode: "inlineasm",
            destination: null,
            resultType: null,
            operands: [],
            hasSideEffects: true,
            predicate: null,
            writesC: false,
            writesZ: false,
            span)
    {
        Volatility = volatility;
        Body = body;
        FlagOutput = flagOutput;
        ParsedLines = parsedLines;
        Bindings = bindings;
    }

    public AsmVolatility Volatility { get; }
    public string Body { get; }
    public string? FlagOutput { get; }
    public IReadOnlyList<InlineAssemblyValidator.AsmLine> ParsedLines { get; }
    public IReadOnlyList<LirInlineAsmBinding> Bindings { get; }
}

public abstract class LirTerminator
{
    protected LirTerminator(TextSpan span)
    {
        Span = span;
    }

    public TextSpan Span { get; }
}

public sealed class LirGotoTerminator : LirTerminator
{
    public LirGotoTerminator(string targetLabel, IReadOnlyList<LirOperand> arguments, TextSpan span)
        : base(span)
    {
        TargetLabel = targetLabel;
        Arguments = arguments;
    }

    public string TargetLabel { get; }
    public IReadOnlyList<LirOperand> Arguments { get; }
}

public sealed class LirBranchTerminator : LirTerminator
{
    public LirBranchTerminator(
        LirOperand condition,
        string trueLabel,
        string falseLabel,
        IReadOnlyList<LirOperand> trueArguments,
        IReadOnlyList<LirOperand> falseArguments,
        TextSpan span,
        MirFlag? conditionFlag = null)
        : base(span)
    {
        Condition = condition;
        TrueLabel = trueLabel;
        FalseLabel = falseLabel;
        TrueArguments = trueArguments;
        FalseArguments = falseArguments;
        ConditionFlag = conditionFlag;
    }

    public LirOperand Condition { get; }
    public string TrueLabel { get; }
    public string FalseLabel { get; }
    public IReadOnlyList<LirOperand> TrueArguments { get; }
    public IReadOnlyList<LirOperand> FalseArguments { get; }

    /// <summary>
    /// When set, the branch uses the hardware flag directly instead of testing a register.
    /// </summary>
    public MirFlag? ConditionFlag { get; }
}

public sealed class LirReturnTerminator : LirTerminator
{
    public LirReturnTerminator(IReadOnlyList<LirOperand> values, TextSpan span)
        : base(span)
    {
        Values = values;
    }

    public IReadOnlyList<LirOperand> Values { get; }
}

public sealed class LirUnreachableTerminator : LirTerminator
{
    public LirUnreachableTerminator(TextSpan span)
        : base(span)
    {
    }
}
