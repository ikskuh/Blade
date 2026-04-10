using System.Collections.Generic;
using Blade.IR;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Lir;

public sealed class LirModule(
    IReadOnlyList<StoragePlace> storagePlaces,
    IReadOnlyList<StorageDefinition> storageDefinitions,
    IReadOnlyList<LirFunction> functions)
{
    public LirModule(IReadOnlyList<LirFunction> functions)
        : this([], [], functions)
    {
    }

    public IReadOnlyList<StoragePlace> StoragePlaces { get; } = storagePlaces;
    public IReadOnlyList<StorageDefinition> StorageDefinitions { get; } = storageDefinitions;
    public IReadOnlyList<LirFunction> Functions { get; } = functions;
}

public sealed class LirFunction(MirFunction sourceFunction, IReadOnlyList<LirBlock> blocks)
{
    public MirFunction SourceFunction { get; } = Requires.NotNull(sourceFunction);
    public FunctionSymbol Symbol => SourceFunction.Symbol;
    public string Name => SourceFunction.Name;
    public bool IsEntryPoint => SourceFunction.IsEntryPoint;
    public FunctionKind Kind => SourceFunction.Kind;
    public IReadOnlyList<BladeType> ReturnTypes => SourceFunction.ReturnTypes;
    public IReadOnlyList<ReturnSlot> ReturnSlots => SourceFunction.ReturnSlots;
    public IReadOnlyList<LirBlock> Blocks { get; } = blocks;
}

public sealed class LirBlock(
    LirBlockRef blockRef,
    IReadOnlyList<LirBlockParameter> parameters,
    IReadOnlyList<LirInstruction> instructions,
    LirTerminator terminator)
{
    public LirBlockRef Ref { get; } = Requires.NotNull(blockRef);
    public IReadOnlyList<LirBlockParameter> Parameters { get; } = parameters;
    public IReadOnlyList<LirInstruction> Instructions { get; } = instructions;
    public LirTerminator Terminator { get; } = terminator;
}

public sealed class LirBlockParameter(LirVirtualRegister register, string name, BladeType type)
{
    public LirVirtualRegister Register { get; } = register;
    public string Name { get; } = name;
    public BladeType Type { get; } = type;
}

public abstract class LirOperand
{
}

public sealed class LirRegisterOperand(LirVirtualRegister register) : LirOperand
{
    public LirVirtualRegister Register { get; } = register;
}

public sealed class LirImmediateOperand(BladeValue value) : LirOperand
{
    public BladeValue Value { get; } = Requires.NotNull(value);
    public BladeType Type => Value.Type;
}

public sealed class LirPlaceOperand(StoragePlace place) : LirOperand
{
    public StoragePlace Place { get; } = place;
}

public abstract class LirInstruction(
    LirVirtualRegister? destination,
    BladeType? resultType,
    IReadOnlyList<LirOperand> operands,
    bool hasSideEffects,
    P2ConditionCode? predicate,
    bool writesC,
    bool writesZ,
    TextSpan span)
{
    public LirVirtualRegister? Destination { get; } = destination;
    public BladeType? ResultType { get; } = resultType;
    public IReadOnlyList<LirOperand> Operands { get; } = operands;
    public bool HasSideEffects { get; } = hasSideEffects;
    public P2ConditionCode? Predicate { get; } = predicate;
    public bool WritesC { get; } = writesC;
    public bool WritesZ { get; } = writesZ;
    public TextSpan Span { get; } = span;

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

    public abstract bool IsValidResultType(BladeType? resultType);

    public abstract bool IsValidOperandCount(int operandCount);

    protected static string StorageClassSuffix(VariableStorageClass storageClass)
    {
        return storageClass switch
        {
            VariableStorageClass.Lut => "lut",
            VariableStorageClass.Hub => "hub",
            _ => "reg",
        };
    }

    protected static bool MatchesExpectedType(BladeType? actualType, BladeType expectedType)
        => actualType?.Equals(expectedType) == true;

    protected static bool IsSingleBitScalarType(BladeType? resultType)
        => resultType is ScalarTypeSymbol { BitWidth: 1 };

    protected static bool MatchesContainerMember(BladeType? containerType, AggregateMemberSymbol expectedMember)
    {
        AggregateMemberSymbol checkedMember = Requires.NotNull(expectedMember);
        IReadOnlyDictionary<string, AggregateMemberSymbol>? members = containerType switch
        {
            AggregateTypeSymbol aggregateType => aggregateType.Members,
            BitfieldTypeSymbol bitfieldType => bitfieldType.Members,
            _ => null,
        };

        if (members is null || !members.TryGetValue(checkedMember.Name, out AggregateMemberSymbol? actualMember))
            return false;

        return actualMember.Type.Equals(checkedMember.Type)
            && actualMember.ByteOffset == checkedMember.ByteOffset
            && actualMember.BitOffset == checkedMember.BitOffset
            && actualMember.BitWidth == checkedMember.BitWidth
            && actualMember.IsBitfield == checkedMember.IsBitfield;
    }
}

public sealed class LirConstOperation : LirOperation
{
    public override string DisplayName => "const";

    public override bool IsValidResultType(BladeType? resultType) => resultType is not null;

    public override bool IsValidOperandCount(int operandCount) => operandCount is 0 or 1;
}

public sealed class LirMovOperation : LirOperation
{
    public override string DisplayName => "mov";

    public override bool IsValidResultType(BladeType? resultType) => resultType is not null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirLoadPlaceOperation : LirOperation
{
    public override string DisplayName => "load.place";

    public override bool IsValidResultType(BladeType? resultType) => resultType is not null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirUnaryOperation(BoundUnaryOperatorKind operatorKind) : LirOperation
{
    public BoundUnaryOperatorKind OperatorKind { get; } = operatorKind;

    public override string DisplayName => $"unary.{OperatorKind}";

    public override bool IsValidResultType(BladeType? resultType)
    {
        return OperatorKind switch
        {
            BoundUnaryOperatorKind.AddressOf => resultType is PointerLikeTypeSymbol,
            _ => resultType is not null,
        };
    }

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirBinaryOperation(BoundBinaryOperatorKind operatorKind) : LirOperation
{
    public BoundBinaryOperatorKind OperatorKind { get; } = operatorKind;

    public override string DisplayName => $"binary.{OperatorKind}";

    public override bool IsValidResultType(BladeType? resultType)
    {
        return OperatorKind switch
        {
            BoundBinaryOperatorKind.LogicalAnd
                or BoundBinaryOperatorKind.LogicalOr
                or BoundBinaryOperatorKind.Equals
                or BoundBinaryOperatorKind.NotEquals
                or BoundBinaryOperatorKind.Less
                or BoundBinaryOperatorKind.LessOrEqual
                or BoundBinaryOperatorKind.Greater
                or BoundBinaryOperatorKind.GreaterOrEqual => MatchesExpectedType(resultType, BuiltinTypes.Bool),
            _ => resultType is not null,
        };
    }

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirPointerOffsetOperation(BoundBinaryOperatorKind operatorKind, int stride) : LirOperation
{
    public BoundBinaryOperatorKind OperatorKind { get; } = operatorKind;
    public int Stride { get; } = stride;

    public override string DisplayName => $"ptr.offset.{OperatorKind}[{Stride}]";

    public override bool IsValidResultType(BladeType? resultType) => resultType is PointerLikeTypeSymbol;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirPointerDifferenceOperation(int stride) : LirOperation
{
    public int Stride { get; } = stride;

    public override string DisplayName => $"ptr.diff[{Stride}]";

    public override bool IsValidResultType(BladeType? resultType) => MatchesExpectedType(resultType, BuiltinTypes.I32);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirConvertOperation : LirOperation
{
    public override string DisplayName => "convert";

    public override bool IsValidResultType(BladeType? resultType) => resultType is not null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirStructLiteralOperation(IReadOnlyList<AggregateMemberSymbol> members) : LirOperation
{
    public IReadOnlyList<AggregateMemberSymbol> Members { get; } = members;

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

    public override bool IsValidResultType(BladeType? resultType) => resultType is StructTypeSymbol;

    public override bool IsValidOperandCount(int operandCount) => operandCount == Members.Count;
}

public sealed class LirLoadMemberOperation(AggregateMemberSymbol member) : LirOperation
{
    public AggregateMemberSymbol Member { get; } = member;

    public override string DisplayName => $"load.member.{Member.Name}.{Member.ByteOffset}";

    public override bool IsValidResultType(BladeType? resultType) => MatchesExpectedType(resultType, Member.Type);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirLoadIndexOperation(BladeType indexedType, VariableStorageClass storageClass) : LirOperation
{
    public BladeType IndexedType { get; } = Requires.NotNull(indexedType);
    public BladeType ElementType { get; } = GetElementType(indexedType);
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override string DisplayName => $"load.index.{StorageClassSuffix(StorageClass)}";

    public override bool IsValidResultType(BladeType? resultType) => MatchesExpectedType(resultType, ElementType);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;

    private static BladeType GetElementType(BladeType indexedType)
    {
        if (indexedType is MultiPointerTypeSymbol pointerType)
            return pointerType.PointeeType;

        return ((ArrayTypeSymbol)indexedType).ElementType;
    }
}

public sealed class LirLoadDerefOperation(BladeType pointerType, VariableStorageClass storageClass) : LirOperation
{
    public PointerTypeSymbol PointerType { get; } = GetPointerType(pointerType);
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override string DisplayName => $"load.deref.{StorageClassSuffix(StorageClass)}";

    public override bool IsValidResultType(BladeType? resultType) => MatchesExpectedType(resultType, PointerType.PointeeType);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;

    private static PointerTypeSymbol GetPointerType(BladeType pointerType)
    {
        BladeType effectivePointerType = Requires.NotNull(pointerType);
        return (PointerTypeSymbol)effectivePointerType;
    }
}

public sealed class LirBitfieldExtractOperation(AggregateMemberSymbol member) : LirOperation
{
    public AggregateMemberSymbol Member { get; } = member;

    public override string DisplayName => $"bitfield.extract.{Member.BitOffset}.{Member.BitWidth}";

    public override bool IsValidResultType(BladeType? resultType) => MatchesExpectedType(resultType, Member.Type);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirBitfieldInsertOperation(AggregateMemberSymbol member) : LirOperation
{
    public AggregateMemberSymbol Member { get; } = member;

    public override string DisplayName => $"bitfield.insert.{Member.BitOffset}.{Member.BitWidth}";

    public override bool IsValidResultType(BladeType? resultType)
        => MatchesContainerMember(resultType, Member);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirInsertMemberOperation(AggregateMemberSymbol member) : LirOperation
{
    public AggregateMemberSymbol Member { get; } = member;

    public override string DisplayName => $"insert.member.{Member.Name}.{Member.ByteOffset}";

    public override bool IsValidResultType(BladeType? resultType)
        => MatchesContainerMember(resultType, Member);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirCallOperation(FunctionSymbol targetFunction) : LirOperation
{
    public FunctionSymbol TargetFunction { get; } = Requires.NotNull(targetFunction);

    public override string DisplayName => "call";

    public override bool IsValidResultType(BladeType? resultType)
    {
        if (TargetFunction.ReturnSlots.Count == 0)
            return resultType is null;

        return MatchesExpectedType(resultType, TargetFunction.ReturnSlots[0].Type);
    }

    public override bool IsValidOperandCount(int operandCount) => operandCount == TargetFunction.Parameters.Count;
}

public sealed class LirCallExtractFlagOperation(MirFlag flag) : LirOperation
{
    public MirFlag Flag { get; } = flag;

    public override string DisplayName => Flag == MirFlag.C ? "call.extractC" : "call.extractZ";

    public override bool IsValidResultType(BladeType? resultType)
        => MatchesExpectedType(resultType, BuiltinTypes.Bool) || MatchesExpectedType(resultType, BuiltinTypes.Bit);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 0;
}

public sealed class LirIntrinsicOperation(P2Mnemonic mnemonic) : LirOperation
{
    public P2Mnemonic Mnemonic { get; } = mnemonic;

    public override string DisplayName => $"intrinsic.{Mnemonic}";

    public override bool IsValidResultType(BladeType? resultType) => true;

    public override bool IsValidOperandCount(int operandCount) => P2InstructionMetadata.TryGetInstructionForm(Mnemonic, operandCount, out _)
        || P2InstructionMetadata.TryGetInstructionForm(Mnemonic, operandCount + 1, out _);
}

public sealed class LirStoreIndexOperation(BladeType indexedType, VariableStorageClass storageClass) : LirOperation
{
    public BladeType IndexedType { get; } = Requires.NotNull(indexedType);
    public BladeType ElementType { get; } = GetElementType(indexedType);
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override string DisplayName => $"store.index.{StorageClassSuffix(StorageClass)}";

    public override bool IsValidResultType(BladeType? resultType)
        => MatchesExpectedType(resultType, ElementType);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 3;

    private static BladeType GetElementType(BladeType indexedType)
    {
        if (indexedType is MultiPointerTypeSymbol pointerType)
            return pointerType.PointeeType;

        return ((ArrayTypeSymbol)indexedType).ElementType;
    }
}

public sealed class LirStoreDerefOperation(BladeType pointerType, VariableStorageClass storageClass) : LirOperation
{
    public PointerTypeSymbol PointerType { get; } = GetPointerType(pointerType);
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override string DisplayName => $"store.deref.{StorageClassSuffix(StorageClass)}";

    public override bool IsValidResultType(BladeType? resultType)
        => MatchesExpectedType(resultType, PointerType.PointeeType);

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;

    private static PointerTypeSymbol GetPointerType(BladeType pointerType)
    {
        BladeType effectivePointerType = Requires.NotNull(pointerType);
        return (PointerTypeSymbol)effectivePointerType;
    }
}

public sealed class LirStorePlaceOperation : LirOperation
{
    public override string DisplayName => "store.place";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirUpdatePlaceOperation(BoundBinaryOperatorKind operatorKind, int? pointerArithmeticStride = null) : LirOperation
{
    public BoundBinaryOperatorKind OperatorKind { get; } = operatorKind;
    public int? PointerArithmeticStride { get; } = pointerArithmeticStride;

    public override string DisplayName => PointerArithmeticStride is int stride
        ? $"update.place.{OperatorKind}[{stride}]"
        : $"update.place.{OperatorKind}";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirYieldOperation : LirOperation
{
    public override string DisplayName => "yield";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 0;
}

public sealed class LirYieldToOperation(FunctionSymbol targetFunction) : LirOperation
{
    public FunctionSymbol TargetFunction { get; } = Requires.NotNull(targetFunction);

    public override string DisplayName => $"yieldto:{TargetFunction.Name}";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == TargetFunction.Parameters.Count;
}

public sealed class LirRepSetupOperation : LirOperation
{
    public override string DisplayName => "rep.setup";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirRepIterOperation : LirOperation
{
    public override string DisplayName => "rep.iter";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 1;
}

public sealed class LirRepForSetupOperation : LirOperation
{
    public override string DisplayName => "repfor.setup";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 2;
}

public sealed class LirRepForIterOperation(int? indexCarrierOrdinal) : LirOperation
{
    public override string DisplayName => "repfor.iter";

    public int? IndexCarrierOrdinal { get; } = indexCarrierOrdinal;

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount % 2 == 0;
}

public sealed class LirNoIrqBeginOperation : LirOperation
{
    public override string DisplayName => "noirq.begin";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 0;
}

public sealed class LirNoIrqEndOperation : LirOperation
{
    public override string DisplayName => "noirq.end";

    public override bool IsValidResultType(BladeType? resultType) => resultType is null;

    public override bool IsValidOperandCount(int operandCount) => operandCount == 0;
}

public sealed class LirOpInstruction : LirInstruction
{
    public LirOpInstruction(
        LirOperation operation,
        LirVirtualRegister? destination,
        BladeType? resultType,
        IReadOnlyList<LirOperand> operands,
        bool hasSideEffects,
        P2ConditionCode? predicate,
        bool writesC,
        bool writesZ,
        TextSpan span)
        : base(destination, resultType, CheckedOperands(operands), hasSideEffects, predicate, writesC, writesZ, span)
    {
        LirOperation checkedOperation = Requires.NotNull(operation);
        Assert.Invariant(
            destination is null || resultType is not null,
            "LIR instructions with destination registers must declare a result type.");
        Assert.Invariant(
            checkedOperation.IsValidResultType(resultType),
            $"Operation '{checkedOperation.DisplayName}' does not accept result type '{resultType?.Name ?? "<null>"}'.");
        Assert.Invariant(
            checkedOperation.IsValidOperandCount(Operands.Count),
            $"Operation '{checkedOperation.DisplayName}' does not accept operand count {Operands.Count}.");
        Operation = checkedOperation;
    }

    public LirOperation Operation { get; }

    public override string DisplayName => Operation.DisplayName;

    private static IReadOnlyList<LirOperand> CheckedOperands(IReadOnlyList<LirOperand> operands)
        => Requires.NotNull(operands);
}

public sealed class LirInlineAsmBinding(InlineAsmBindingSlot slot, Symbol symbol, LirOperand operand, InlineAsmBindingAccess access)
{
    public InlineAsmBindingSlot Slot { get; } = Requires.NotNull(slot);
    public string PlaceholderText => Slot.PlaceholderText;
    public Symbol Symbol { get; } = Requires.NotNull(symbol);
    public LirOperand Operand { get; } = operand;
    public InlineAsmBindingAccess Access { get; } = access;
}

public sealed class LirInlineAsmInstruction(
    AsmVolatility volatility,
    InlineAsmFlagOutput? flagOutput,
    IReadOnlyList<InlineAsmLine> parsedLines,
    IReadOnlyList<LirInlineAsmBinding> bindings,
    TextSpan span)
    : LirInstruction(
        destination: null,
        resultType: null,
        operands: [],
        hasSideEffects: true,
        predicate: null,
        writesC: false,
        writesZ: false,
        span)
{
    public AsmVolatility Volatility { get; } = volatility;
    public InlineAsmFlagOutput? FlagOutput { get; } = flagOutput;
    public IReadOnlyList<InlineAsmLine> ParsedLines { get; } = parsedLines;
    public IReadOnlyList<LirInlineAsmBinding> Bindings { get; } = bindings;

    public override string DisplayName => "inlineasm";
}

public abstract class LirTerminator(TextSpan span)
{
    public TextSpan Span { get; } = span;
}

public sealed class LirGotoTerminator(LirBlockRef target, IReadOnlyList<LirOperand> arguments, TextSpan span) : LirTerminator(span)
{
    public LirBlockRef Target { get; } = Requires.NotNull(target);
    public IReadOnlyList<LirOperand> Arguments { get; } = arguments;
}

public sealed class LirBranchTerminator(
    LirOperand condition,
    LirBlockRef trueTarget,
    LirBlockRef falseTarget,
    IReadOnlyList<LirOperand> trueArguments,
    IReadOnlyList<LirOperand> falseArguments,
    TextSpan span,
    MirFlag? conditionFlag = null)
    : LirTerminator(span)
{
    public LirOperand Condition { get; } = condition;
    public LirBlockRef TrueTarget { get; } = Requires.NotNull(trueTarget);
    public LirBlockRef FalseTarget { get; } = Requires.NotNull(falseTarget);
    public IReadOnlyList<LirOperand> TrueArguments { get; } = trueArguments;
    public IReadOnlyList<LirOperand> FalseArguments { get; } = falseArguments;

    /// <summary>
    /// When set, the branch uses the hardware flag directly instead of testing a register.
    /// </summary>
    public MirFlag? ConditionFlag { get; } = conditionFlag;
}

public sealed class LirReturnTerminator(IReadOnlyList<LirOperand> values, TextSpan span) : LirTerminator(span)
{
    public IReadOnlyList<LirOperand> Values { get; } = values;
}

public sealed class LirUnreachableTerminator(TextSpan span) : LirTerminator(span);
