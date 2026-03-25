using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
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
        LirVirtualRegister? destination,
        TypeSymbol? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        string? predicate,
        bool writesC,
        bool writesZ,
        TextSpan span)
    {
        Destination = destination;
        ResultType = resultType;
        Operands = operands;
        HasSideEffects = hasSideEffects;
        Predicate = predicate;
        WritesC = writesC;
        WritesZ = writesZ;
        Span = span;
    }

    public LirVirtualRegister? Destination { get; }
    public TypeSymbol? ResultType { get; }
    public IReadOnlyList<LirOperand> Operands { get; }
    public bool HasSideEffects { get; }
    public string? Predicate { get; }
    public bool WritesC { get; }
    public bool WritesZ { get; }
    public TextSpan Span { get; }

    public abstract string DisplayName { get; }
    public string Opcode => DisplayName;
}

public abstract class LirOperation
{
    public abstract string DisplayName { get; }

    protected static string StorageClassSuffix(VariableStorageClass storageClass)
    {
        return storageClass switch
        {
            VariableStorageClass.Lut => "lut",
            VariableStorageClass.Hub => "hub",
            _ => "reg",
        };
    }
}

public sealed class LirConstOperation : LirOperation
{
    public override string DisplayName => "const";
}

public sealed class LirMovOperation : LirOperation
{
    public override string DisplayName => "mov";
}

public sealed class LirLoadSymbolOperation : LirOperation
{
    public override string DisplayName => "load.sym";
}

public sealed class LirLoadPlaceOperation : LirOperation
{
    public override string DisplayName => "load.place";
}

public sealed class LirUnaryOperation : LirOperation
{
    public LirUnaryOperation(BoundUnaryOperatorKind operatorKind)
    {
        OperatorKind = operatorKind;
    }

    public BoundUnaryOperatorKind OperatorKind { get; }

    public override string DisplayName => $"unary.{OperatorKind}";
}

public sealed class LirBinaryOperation : LirOperation
{
    public LirBinaryOperation(BoundBinaryOperatorKind operatorKind)
    {
        OperatorKind = operatorKind;
    }

    public BoundBinaryOperatorKind OperatorKind { get; }

    public override string DisplayName => $"binary.{OperatorKind}";
}

public sealed class LirConvertOperation : LirOperation
{
    public override string DisplayName => "convert";
}

public sealed class LirRangeOperation : LirOperation
{
    public override string DisplayName => "range";
}

public sealed class LirStructLiteralOperation : LirOperation
{
    public LirStructLiteralOperation(IReadOnlyList<AggregateMemberSymbol> members)
    {
        Members = members;
    }

    public IReadOnlyList<AggregateMemberSymbol> Members { get; }

    public override string DisplayName
    {
        get
        {
            if (Members.Count == 0)
                return "structlit";

            List<string> parts = new(1 + Members.Count) { "structlit" };
            foreach (AggregateMemberSymbol member in Members)
                parts.Add(member.Name);
            return string.Join(".", parts);
        }
    }
}

public sealed class LirLoadMemberOperation : LirOperation
{
    public LirLoadMemberOperation(AggregateMemberSymbol member)
    {
        Member = member;
    }

    public AggregateMemberSymbol Member { get; }

    public override string DisplayName => $"load.member.{Member.Name}.{Member.ByteOffset}";
}

public sealed class LirLoadIndexOperation : LirOperation
{
    public LirLoadIndexOperation(VariableStorageClass storageClass)
    {
        StorageClass = storageClass;
    }

    public VariableStorageClass StorageClass { get; }

    public override string DisplayName => $"load.index.{StorageClassSuffix(StorageClass)}";
}

public sealed class LirLoadDerefOperation : LirOperation
{
    public LirLoadDerefOperation(VariableStorageClass storageClass)
    {
        StorageClass = storageClass;
    }

    public VariableStorageClass StorageClass { get; }

    public override string DisplayName => $"load.deref.{StorageClassSuffix(StorageClass)}";
}

public sealed class LirBitfieldExtractOperation : LirOperation
{
    public LirBitfieldExtractOperation(AggregateMemberSymbol member)
    {
        Member = member;
    }

    public AggregateMemberSymbol Member { get; }

    public override string DisplayName => $"bitfield.extract.{Member.BitOffset}.{Member.BitWidth}";
}

public sealed class LirBitfieldInsertOperation : LirOperation
{
    public LirBitfieldInsertOperation(AggregateMemberSymbol member)
    {
        Member = member;
    }

    public AggregateMemberSymbol Member { get; }

    public override string DisplayName => $"bitfield.insert.{Member.BitOffset}.{Member.BitWidth}";
}

public sealed class LirInsertMemberOperation : LirOperation
{
    public LirInsertMemberOperation(AggregateMemberSymbol member)
    {
        Member = member;
    }

    public AggregateMemberSymbol Member { get; }

    public override string DisplayName => $"insert.member.{Member.Name}.{Member.ByteOffset}";
}

public sealed class LirSelectOperation : LirOperation
{
    public override string DisplayName => "select";
}

public sealed class LirCallOperation : LirOperation
{
    public override string DisplayName => "call";
}

public sealed class LirCallExtractFlagOperation : LirOperation
{
    public LirCallExtractFlagOperation(MirFlag flag)
    {
        Flag = flag;
    }

    public MirFlag Flag { get; }

    public override string DisplayName => Flag == MirFlag.C ? "call.extractC" : "call.extractZ";
}

public sealed class LirIntrinsicOperation : LirOperation
{
    public override string DisplayName => "intrinsic";
}

public sealed class LirStoreIndexOperation : LirOperation
{
    public LirStoreIndexOperation(VariableStorageClass storageClass)
    {
        StorageClass = storageClass;
    }

    public VariableStorageClass StorageClass { get; }

    public override string DisplayName => $"store.index.{StorageClassSuffix(StorageClass)}";
}

public sealed class LirStoreDerefOperation : LirOperation
{
    public LirStoreDerefOperation(VariableStorageClass storageClass)
    {
        StorageClass = storageClass;
    }

    public VariableStorageClass StorageClass { get; }

    public override string DisplayName => $"store.deref.{StorageClassSuffix(StorageClass)}";
}

public sealed class LirStorePlaceOperation : LirOperation
{
    public override string DisplayName => "store.place";
}

public sealed class LirUpdatePlaceOperation : LirOperation
{
    public LirUpdatePlaceOperation(BoundBinaryOperatorKind operatorKind)
    {
        OperatorKind = operatorKind;
    }

    public BoundBinaryOperatorKind OperatorKind { get; }

    public override string DisplayName => $"update.place.{OperatorKind}";
}

public sealed class LirYieldOperation : LirOperation
{
    public override string DisplayName => "yield";
}

public sealed class LirYieldToOperation : LirOperation
{
    public LirYieldToOperation(string targetFunctionName)
    {
        TargetFunctionName = targetFunctionName;
    }

    public string TargetFunctionName { get; }

    public override string DisplayName => $"yieldto:{TargetFunctionName}";
}

public sealed class LirRepSetupOperation : LirOperation
{
    public override string DisplayName => "rep.setup";
}

public sealed class LirRepIterOperation : LirOperation
{
    public override string DisplayName => "rep.iter";
}

public sealed class LirRepForSetupOperation : LirOperation
{
    public override string DisplayName => "repfor.setup";
}

public sealed class LirRepForIterOperation : LirOperation
{
    public override string DisplayName => "repfor.iter";
}

public sealed class LirNoIrqBeginOperation : LirOperation
{
    public override string DisplayName => "noirq.begin";
}

public sealed class LirNoIrqEndOperation : LirOperation
{
    public override string DisplayName => "noirq.end";
}

public sealed class LirErrorStatementOperation : LirOperation
{
    public override string DisplayName => "error.statement";
}

public sealed class LirErrorStoreOperation : LirOperation
{
    public override string DisplayName => "store.error";
}

public sealed class LirOpInstruction : LirInstruction
{
    public LirOpInstruction(
        LirOperation operation,
        LirVirtualRegister? destination,
        TypeSymbol? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        string? predicate,
        bool writesC,
        bool writesZ,
        TextSpan span)
        : base(destination, resultType, operands, hasSideEffects, predicate, writesC, writesZ, span)
    {
        Operation = operation;
    }

    public LirOperation Operation { get; }

    public override string DisplayName => Operation.DisplayName;
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

    public override string DisplayName => "inlineasm";
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
