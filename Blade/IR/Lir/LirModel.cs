using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Lir;

public sealed class LirModule
{
    public LirModule(IReadOnlyList<LirFunction> functions)
        : this([], [], functions)
    {
    }

    public LirModule(
        IReadOnlyList<StoragePlace> storagePlaces,
        IReadOnlyList<StorageDefinition> storageDefinitions,
        IReadOnlyList<LirFunction> functions)
    {
        StoragePlaces = storagePlaces;
        StorageDefinitions = storageDefinitions;
        Functions = functions;
    }

    public IReadOnlyList<StoragePlace> StoragePlaces { get; }
    public IReadOnlyList<StorageDefinition> StorageDefinitions { get; }
    public IReadOnlyList<LirFunction> Functions { get; }
}

public sealed class LirFunction
{
    public LirFunction(MirFunction sourceFunction, IReadOnlyList<LirBlock> blocks)
    {
        SourceFunction = Requires.NotNull(sourceFunction);
        Blocks = blocks;
    }

    public MirFunction SourceFunction { get; }
    public FunctionSymbol Symbol => SourceFunction.Symbol;
    public string Name => SourceFunction.Name;
    public bool IsEntryPoint => SourceFunction.IsEntryPoint;
    public FunctionKind Kind => SourceFunction.Kind;
    public IReadOnlyList<TypeSymbol> ReturnTypes => SourceFunction.ReturnTypes;
    public IReadOnlyList<ReturnSlot> ReturnSlots => SourceFunction.ReturnSlots;
    public IReadOnlyList<LirBlock> Blocks { get; }
}

public sealed class LirBlock
{
    public LirBlock(
        LirBlockRef blockRef,
        IReadOnlyList<LirBlockParameter> parameters,
        IReadOnlyList<LirInstruction> instructions,
        LirTerminator terminator)
    {
        Ref = Requires.NotNull(blockRef);
        Parameters = parameters;
        Instructions = instructions;
        Terminator = terminator;
    }

    public LirBlockRef Ref { get; }
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

public sealed class LirImmediateOperand(BladeValue value) : LirOperand
{
    public BladeValue Value { get; } = Requires.NotNull(value);
    public TypeSymbol Type => Value.Type;
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
        P2ConditionCode? predicate,
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
    public P2ConditionCode? Predicate { get; }
    public bool WritesC { get; }
    public bool WritesZ { get; }
    public TextSpan Span { get; }

    /// <summary>
    /// Human-readable dump name. Debug/output only; compiler logic must never branch on this text.
    /// </summary>
    public abstract string DisplayName { get; }
}

public abstract class LirOperation
{
    /// <summary>
    /// Human-readable dump name. Debug/output only; compiler logic must never branch on this text.
    /// </summary>
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

public sealed class LirPointerOffsetOperation : LirOperation
{
    public LirPointerOffsetOperation(BoundBinaryOperatorKind operatorKind, int stride)
    {
        OperatorKind = operatorKind;
        Stride = stride;
    }

    public BoundBinaryOperatorKind OperatorKind { get; }
    public int Stride { get; }

    public override string DisplayName => $"ptr.offset.{OperatorKind}[{Stride}]";
}

public sealed class LirPointerDifferenceOperation : LirOperation
{
    public LirPointerDifferenceOperation(int stride)
    {
        Stride = stride;
    }

    public int Stride { get; }

    public override string DisplayName => $"ptr.diff[{Stride}]";
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

public sealed class LirCallOperation : LirOperation
{
    public LirCallOperation(FunctionSymbol targetFunction)
    {
        TargetFunction = Requires.NotNull(targetFunction);
    }

    public FunctionSymbol TargetFunction { get; }

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
    public LirIntrinsicOperation(P2Mnemonic mnemonic)
    {
        Mnemonic = mnemonic;
    }

    public P2Mnemonic Mnemonic { get; }

    public override string DisplayName => $"intrinsic.{Mnemonic}";
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
    public LirUpdatePlaceOperation(BoundBinaryOperatorKind operatorKind, int? pointerArithmeticStride = null)
    {
        OperatorKind = operatorKind;
        PointerArithmeticStride = pointerArithmeticStride;
    }

    public BoundBinaryOperatorKind OperatorKind { get; }
    public int? PointerArithmeticStride { get; }
    public bool IsPointerArithmetic => PointerArithmeticStride is not null;

    public override string DisplayName => PointerArithmeticStride is int stride
        ? $"update.place.{OperatorKind}[{stride}]"
        : $"update.place.{OperatorKind}";
}

public sealed class LirYieldOperation : LirOperation
{
    public override string DisplayName => "yield";
}

public sealed class LirYieldToOperation : LirOperation
{
    public LirYieldToOperation(FunctionSymbol targetFunction)
    {
        TargetFunction = Requires.NotNull(targetFunction);
    }

    public FunctionSymbol TargetFunction { get; }

    public override string DisplayName => $"yieldto:{TargetFunction.Name}";
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

public sealed class LirOpInstruction : LirInstruction
{
    public LirOpInstruction(
        LirOperation operation,
        LirVirtualRegister? destination,
        TypeSymbol? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        P2ConditionCode? predicate,
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
    public LirInlineAsmBinding(InlineAsmBindingSlot slot, Symbol symbol, LirOperand operand, InlineAsmBindingAccess access)
    {
        Slot = Requires.NotNull(slot);
        Symbol = Requires.NotNull(symbol);
        Operand = operand;
        Access = access;
    }

    public InlineAsmBindingSlot Slot { get; }
    public string PlaceholderText => Slot.PlaceholderText;
    public Symbol Symbol { get; }
    public LirOperand Operand { get; }
    public InlineAsmBindingAccess Access { get; }
}

public sealed class LirInlineAsmInstruction : LirInstruction
{
    public LirInlineAsmInstruction(
        AsmVolatility volatility,
        string body,
        InlineAsmFlagOutput? flagOutput,
        IReadOnlyList<InlineAsmLine> parsedLines,
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
    public InlineAsmFlagOutput? FlagOutput { get; }
    public IReadOnlyList<InlineAsmLine> ParsedLines { get; }
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
    public LirGotoTerminator(LirBlockRef target, IReadOnlyList<LirOperand> arguments, TextSpan span)
        : base(span)
    {
        Target = Requires.NotNull(target);
        Arguments = arguments;
    }

    public LirBlockRef Target { get; }
    public IReadOnlyList<LirOperand> Arguments { get; }
}

public sealed class LirBranchTerminator : LirTerminator
{
    public LirBranchTerminator(
        LirOperand condition,
        LirBlockRef trueTarget,
        LirBlockRef falseTarget,
        IReadOnlyList<LirOperand> trueArguments,
        IReadOnlyList<LirOperand> falseArguments,
        TextSpan span,
        MirFlag? conditionFlag = null)
        : base(span)
    {
        Condition = condition;
        TrueTarget = Requires.NotNull(trueTarget);
        FalseTarget = Requires.NotNull(falseTarget);
        TrueArguments = trueArguments;
        FalseArguments = falseArguments;
        ConditionFlag = conditionFlag;
    }

    public LirOperand Condition { get; }
    public LirBlockRef TrueTarget { get; }
    public LirBlockRef FalseTarget { get; }
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
