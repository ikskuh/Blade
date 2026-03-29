#nullable enable annotations
#nullable disable warnings
using System.Collections.Generic;
using Blade.IR;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Mir;

/// <summary>
/// Identifies which hardware flag a value resides in, with polarity.
/// C/Z mean "value is true when flag is set"; NC/NZ mean "value is true when flag is clear".
/// </summary>
public enum MirFlag { C, Z, NC, NZ }

public sealed class MirModule
{
    public MirModule(IReadOnlyList<MirFunction> functions)
        : this([], functions)
    {
    }

    public MirModule(IReadOnlyList<StoragePlace> storagePlaces, IReadOnlyList<MirFunction> functions)
    {
        StoragePlaces = storagePlaces;
        Functions = functions;
    }

    public IReadOnlyList<StoragePlace> StoragePlaces { get; }
    public IReadOnlyList<MirFunction> Functions { get; }
}

public sealed class MirFunction
{
    public MirFunction(
        FunctionSymbol symbol,
        bool isEntryPoint,
        IReadOnlyList<TypeSymbol> returnTypes,
        IReadOnlyList<MirBlock> blocks,
        IReadOnlyList<ReturnSlot>? returnSlots = null,
        IReadOnlyDictionary<MirValueId, MirFlag>? flagValues = null)
    {
        Symbol = Requires.NotNull(symbol);
        IsEntryPoint = isEntryPoint;
        ReturnTypes = returnTypes;
        ReturnSlots = returnSlots ?? [];
        FlagValues = flagValues ?? new Dictionary<MirValueId, MirFlag>();
        Blocks = blocks;
    }

    public FunctionSymbol Symbol { get; }
    public string Name => Symbol.Name;
    public bool IsEntryPoint { get; }
    public FunctionKind Kind => Symbol.Kind;
    public IReadOnlyList<TypeSymbol> ReturnTypes { get; }
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; }
    public IReadOnlyDictionary<MirValueId, MirFlag> FlagValues { get; }
    public IReadOnlyList<MirBlock> Blocks { get; }
}

public sealed class MirBlock
{
    public MirBlock(
        MirBlockRef blockRef,
        IReadOnlyList<MirBlockParameter> parameters,
        IReadOnlyList<MirInstruction> instructions,
        MirTerminator terminator)
    {
        Ref = Requires.NotNull(blockRef);
        Parameters = parameters;
        Instructions = instructions;
        Terminator = terminator;
    }

    public MirBlockRef Ref { get; }
    public IReadOnlyList<MirBlockParameter> Parameters { get; }
    public IReadOnlyList<MirInstruction> Instructions { get; }
    public MirTerminator Terminator { get; }
}

public sealed class MirBlockParameter
{
    public MirBlockParameter(MirValueId value, string name, TypeSymbol type)
    {
        Value = value;
        Name = name;
        Type = type;
    }

    public MirValueId Value { get; }
    public string Name { get; }
    public TypeSymbol Type { get; }
}

public abstract class MirInstruction
{
    protected MirInstruction(MirValueId? result, TypeSymbol? resultType, TextSpan span, bool hasSideEffects)
    {
        Result = result;
        ResultType = resultType;
        Span = span;
        HasSideEffects = hasSideEffects;
    }

    public MirValueId? Result { get; }
    public TypeSymbol? ResultType { get; }
    public TextSpan Span { get; }
    public bool HasSideEffects { get; }

    public abstract IReadOnlyList<MirValueId> Uses { get; }
    public abstract MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping);
}

public sealed class MirConstantInstruction : MirInstruction
{
    public MirConstantInstruction(MirValueId result, TypeSymbol type, object? value, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Value = value;
    }

    public object? Value { get; }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirLoadSymbolInstruction : MirInstruction
{
    public MirLoadSymbolInstruction(MirValueId result, TypeSymbol type, StoragePlace symbol, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Symbol = Requires.NotNull(symbol);
    }

    public StoragePlace Symbol { get; }
    public string SymbolName => Symbol.EmittedName;

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirLoadPlaceInstruction : MirInstruction
{
    public MirLoadPlaceInstruction(MirValueId result, TypeSymbol type, StoragePlace place, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Place = place;
    }

    public StoragePlace Place { get; }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirCopyInstruction : MirInstruction
{
    public MirCopyInstruction(MirValueId result, TypeSymbol type, MirValueId source, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Source = source;
    }

    public MirValueId Source { get; }

    public override IReadOnlyList<MirValueId> Uses => [Source];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId source = mapping.TryGetValue(Source, out MirValueId mapped) ? mapped : Source;
        return source == Source ? this : new MirCopyInstruction(Result!, ResultType!, source, Span);
    }
}

public sealed class MirUnaryInstruction : MirInstruction
{
    public MirUnaryInstruction(MirValueId result, TypeSymbol type, BoundUnaryOperatorKind op, MirValueId operand, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Operator = op;
        Operand = operand;
    }

    public BoundUnaryOperatorKind Operator { get; }
    public MirValueId Operand { get; }

    public override IReadOnlyList<MirValueId> Uses => [Operand];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId operand = mapping.TryGetValue(Operand, out MirValueId mapped) ? mapped : Operand;
        return operand == Operand ? this : new MirUnaryInstruction(Result!, ResultType!, Operator, operand, Span);
    }
}

public sealed class MirBinaryInstruction : MirInstruction
{
    public MirBinaryInstruction(
        MirValueId result,
        TypeSymbol type,
        BoundBinaryOperatorKind op,
        MirValueId left,
        MirValueId right,
        TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Operator = op;
        Left = left;
        Right = right;
    }

    public BoundBinaryOperatorKind Operator { get; }
    public MirValueId Left { get; }
    public MirValueId Right { get; }

    public override IReadOnlyList<MirValueId> Uses => [Left, Right];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId left = mapping.TryGetValue(Left, out MirValueId mappedLeft) ? mappedLeft : Left;
        MirValueId right = mapping.TryGetValue(Right, out MirValueId mappedRight) ? mappedRight : Right;
        if (left == Left && right == Right)
            return this;
        return new MirBinaryInstruction(Result!, ResultType!, Operator, left, right, Span);
    }
}

public sealed class MirPointerOffsetInstruction : MirInstruction
{
    public MirPointerOffsetInstruction(
        MirValueId result,
        TypeSymbol type,
        BoundBinaryOperatorKind operatorKind,
        MirValueId baseAddress,
        MirValueId delta,
        int stride,
        TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        OperatorKind = operatorKind;
        BaseAddress = baseAddress;
        Delta = delta;
        Stride = stride;
    }

    public BoundBinaryOperatorKind OperatorKind { get; }
    public MirValueId BaseAddress { get; }
    public MirValueId Delta { get; }
    public int Stride { get; }

    public override IReadOnlyList<MirValueId> Uses => [BaseAddress, Delta];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId baseAddress = mapping.TryGetValue(BaseAddress, out MirValueId mappedBaseAddress) ? mappedBaseAddress : BaseAddress;
        MirValueId delta = mapping.TryGetValue(Delta, out MirValueId mappedDelta) ? mappedDelta : Delta;
        if (baseAddress == BaseAddress && delta == Delta)
            return this;
        return new MirPointerOffsetInstruction(Result!, ResultType!, OperatorKind, baseAddress, delta, Stride, Span);
    }
}

public sealed class MirPointerDifferenceInstruction : MirInstruction
{
    public MirPointerDifferenceInstruction(
        MirValueId result,
        TypeSymbol type,
        MirValueId left,
        MirValueId right,
        int stride,
        TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Left = left;
        Right = right;
        Stride = stride;
    }

    public MirValueId Left { get; }
    public MirValueId Right { get; }
    public int Stride { get; }

    public override IReadOnlyList<MirValueId> Uses => [Left, Right];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId left = mapping.TryGetValue(Left, out MirValueId mappedLeft) ? mappedLeft : Left;
        MirValueId right = mapping.TryGetValue(Right, out MirValueId mappedRight) ? mappedRight : Right;
        if (left == Left && right == Right)
            return this;
        return new MirPointerDifferenceInstruction(Result!, ResultType!, left, right, Stride, Span);
    }
}

public sealed class MirConvertInstruction : MirInstruction
{
    public MirConvertInstruction(MirValueId result, TypeSymbol type, MirValueId operand, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Operand = operand;
    }

    public MirValueId Operand { get; }

    public override IReadOnlyList<MirValueId> Uses => [Operand];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId operand = mapping.TryGetValue(Operand, out MirValueId mapped) ? mapped : Operand;
        return operand == Operand ? this : new MirConvertInstruction(Result!, ResultType!, operand, Span);
    }
}

public sealed class MirRangeInstruction : MirInstruction
{
    public MirRangeInstruction(MirValueId result, TypeSymbol type, MirValueId start, MirValueId end, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Start = start;
        End = end;
    }

    public MirValueId Start { get; }
    public MirValueId End { get; }

    public override IReadOnlyList<MirValueId> Uses => [Start, End];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId start = mapping.TryGetValue(Start, out MirValueId mappedStart) ? mappedStart : Start;
        MirValueId end = mapping.TryGetValue(End, out MirValueId mappedEnd) ? mappedEnd : End;
        return start == Start && end == End
            ? this
            : new MirRangeInstruction(Result!, ResultType!, start, end, Span);
    }
}

public sealed class MirStructLiteralField
{
    public MirStructLiteralField(AggregateMemberSymbol member, MirValueId value)
    {
        Member = member;
        Value = value;
    }

    public AggregateMemberSymbol Member { get; }
    public MirValueId Value { get; }
}

public sealed class MirStructLiteralInstruction : MirInstruction
{
    public MirStructLiteralInstruction(MirValueId result, StructTypeSymbol type, IReadOnlyList<MirStructLiteralField> fields, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Fields = fields;
    }

    public IReadOnlyList<MirStructLiteralField> Fields { get; }

    public override IReadOnlyList<MirValueId> Uses
    {
        get
        {
            List<MirValueId> uses = new(Fields.Count);
            foreach (MirStructLiteralField literalField in Fields)
                uses.Add(literalField.Value);
            return uses;
        }
    }

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirStructLiteralField>? rewritten = null;
        for (int i = 0; i < Fields.Count; i++)
        {
            MirStructLiteralField field = Fields[i];
            if (!mapping.TryGetValue(field.Value, out MirValueId mapped) || mapped == field.Value)
                continue;

            rewritten ??= new List<MirStructLiteralField>(Fields);
            rewritten[i] = new MirStructLiteralField(field.Member, mapped);
        }

        return rewritten is null
            ? this
            : new MirStructLiteralInstruction(Result!, (StructTypeSymbol)ResultType!, rewritten, Span);
    }
}

public sealed class MirLoadMemberInstruction : MirInstruction
{
    public MirLoadMemberInstruction(MirValueId result, TypeSymbol type, MirValueId receiver, AggregateMemberSymbol member, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Receiver = receiver;
        Member = member;
    }

    public MirValueId Receiver { get; }
    public AggregateMemberSymbol Member { get; }

    public override IReadOnlyList<MirValueId> Uses => [Receiver];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mapped) ? mapped : Receiver;
        return receiver == Receiver
            ? this
            : new MirLoadMemberInstruction(Result!, ResultType!, receiver, Member, Span);
    }
}

public sealed class MirLoadIndexInstruction : MirInstruction
{
    public MirLoadIndexInstruction(
        MirValueId result,
        TypeSymbol type,
        MirValueId indexed,
        MirValueId index,
        VariableStorageClass storageClass,
        bool hasSideEffects,
        TextSpan span)
        : base(result, type, span, hasSideEffects)
    {
        Indexed = indexed;
        Index = index;
        StorageClass = storageClass;
    }

    public MirValueId Indexed { get; }
    public MirValueId Index { get; }
    public VariableStorageClass StorageClass { get; }

    public override IReadOnlyList<MirValueId> Uses => [Indexed, Index];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId indexed = mapping.TryGetValue(Indexed, out MirValueId mappedIndexed) ? mappedIndexed : Indexed;
        MirValueId index = mapping.TryGetValue(Index, out MirValueId mappedIndex) ? mappedIndex : Index;
        return indexed == Indexed && index == Index
            ? this
            : new MirLoadIndexInstruction(Result!, ResultType!, indexed, index, StorageClass, HasSideEffects, Span);
    }
}

public sealed class MirLoadDerefInstruction : MirInstruction
{
    public MirLoadDerefInstruction(
        MirValueId result,
        TypeSymbol type,
        MirValueId address,
        VariableStorageClass storageClass,
        bool hasSideEffects,
        TextSpan span)
        : base(result, type, span, hasSideEffects)
    {
        Address = address;
        StorageClass = storageClass;
    }

    public MirValueId Address { get; }
    public VariableStorageClass StorageClass { get; }

    public override IReadOnlyList<MirValueId> Uses => [Address];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId address = mapping.TryGetValue(Address, out MirValueId mapped) ? mapped : Address;
        return address == Address
            ? this
            : new MirLoadDerefInstruction(Result!, ResultType!, address, StorageClass, HasSideEffects, Span);
    }
}

public sealed class MirBitfieldExtractInstruction : MirInstruction
{
    public MirBitfieldExtractInstruction(MirValueId result, TypeSymbol type, MirValueId receiver, AggregateMemberSymbol member, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Receiver = receiver;
        Member = member;
    }

    public MirValueId Receiver { get; }
    public AggregateMemberSymbol Member { get; }

    public override IReadOnlyList<MirValueId> Uses => [Receiver];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mapped) ? mapped : Receiver;
        return receiver == Receiver
            ? this
            : new MirBitfieldExtractInstruction(Result!, ResultType!, receiver, Member, Span);
    }
}

public sealed class MirBitfieldInsertInstruction : MirInstruction
{
    public MirBitfieldInsertInstruction(
        MirValueId result,
        TypeSymbol aggregateType,
        MirValueId receiver,
        MirValueId value,
        AggregateMemberSymbol member,
        TextSpan span)
        : base(result, aggregateType, span, hasSideEffects: false)
    {
        Receiver = receiver;
        Value = value;
        Member = member;
    }

    public MirValueId Receiver { get; }
    public MirValueId Value { get; }
    public AggregateMemberSymbol Member { get; }

    public override IReadOnlyList<MirValueId> Uses => [Receiver, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mappedReceiver) ? mappedReceiver : Receiver;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return receiver == Receiver && value == Value
            ? this
            : new MirBitfieldInsertInstruction(Result!, ResultType!, receiver, value, Member, Span);
    }
}

public sealed class MirInsertMemberInstruction : MirInstruction
{
    public MirInsertMemberInstruction(
        MirValueId result,
        TypeSymbol aggregateType,
        MirValueId receiver,
        MirValueId value,
        AggregateMemberSymbol member,
        TextSpan span)
        : base(result, aggregateType, span, hasSideEffects: false)
    {
        Receiver = receiver;
        Value = value;
        Member = member;
    }

    public MirValueId Receiver { get; }
    public MirValueId Value { get; }
    public AggregateMemberSymbol Member { get; }

    public override IReadOnlyList<MirValueId> Uses => [Receiver, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mappedReceiver) ? mappedReceiver : Receiver;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return receiver == Receiver && value == Value
            ? this
            : new MirInsertMemberInstruction(Result!, ResultType!, receiver, value, Member, Span);
    }
}

public sealed class MirCallInstruction : MirInstruction
{
    public MirCallInstruction(
        MirValueId? result,
        TypeSymbol? resultType,
        FunctionSymbol function,
        IReadOnlyList<MirValueId> arguments,
        TextSpan span,
        IReadOnlyList<(MirValueId Value, TypeSymbol Type)>? extraResults = null)
        : base(result, resultType, span, hasSideEffects: true)
    {
        Function = Requires.NotNull(function);
        Arguments = arguments;
        ExtraResults = extraResults ?? [];
    }

    public FunctionSymbol Function { get; }
    public IReadOnlyList<MirValueId> Arguments { get; }
    public IReadOnlyList<(MirValueId Value, TypeSymbol Type)> ExtraResults { get; }

    public override IReadOnlyList<MirValueId> Uses => Arguments;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Arguments.Count);
        bool changed = false;
        foreach (MirValueId arg in Arguments)
        {
            MirValueId mapped = mapping.TryGetValue(arg, out MirValueId value) ? value : arg;
            rewritten.Add(mapped);
            changed |= mapped != arg;
        }

        List<(MirValueId, TypeSymbol)>? rewrittenExtra = null;
        for (int i = 0; i < ExtraResults.Count; i++)
        {
            (MirValueId extraVal, TypeSymbol extraType) = ExtraResults[i];
            if (mapping.TryGetValue(extraVal, out MirValueId mappedExtra) && mappedExtra != extraVal)
            {
                if (rewrittenExtra is null)
                {
                    rewrittenExtra = new(ExtraResults.Count);
                    for (int j = 0; j < i; j++)
                        rewrittenExtra.Add(ExtraResults[j]);
                }
                rewrittenExtra.Add((mappedExtra, extraType));
                changed = true;
            }
            else
            {
                rewrittenExtra?.Add(ExtraResults[i]);
            }
        }

        return changed
            ? new MirCallInstruction(Result, ResultType, Function, rewritten, Span, rewrittenExtra ?? ExtraResults)
            : this;
    }
}

public sealed class MirIntrinsicCallInstruction : MirInstruction
{
    public MirIntrinsicCallInstruction(
        MirValueId? result,
        TypeSymbol? resultType,
        P2Mnemonic mnemonic,
        IReadOnlyList<MirValueId> arguments,
        TextSpan span)
        : base(result, resultType, span, hasSideEffects: true)
    {
        Mnemonic = mnemonic;
        Arguments = arguments;
    }

    public P2Mnemonic Mnemonic { get; }
    public IReadOnlyList<MirValueId> Arguments { get; }

    public override IReadOnlyList<MirValueId> Uses => Arguments;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Arguments.Count);
        bool changed = false;
        foreach (MirValueId arg in Arguments)
        {
            MirValueId mapped = mapping.TryGetValue(arg, out MirValueId value) ? value : arg;
            rewritten.Add(mapped);
            changed |= mapped != arg;
        }

        return changed ? new MirIntrinsicCallInstruction(Result, ResultType, Mnemonic, rewritten, Span) : this;
    }
}

public sealed class MirStoreIndexInstruction : MirInstruction
{
    public MirStoreIndexInstruction(
        TypeSymbol? elementType,
        MirValueId indexed,
        MirValueId index,
        MirValueId value,
        VariableStorageClass storageClass,
        TextSpan span)
        : base(result: null, resultType: elementType, span, hasSideEffects: true)
    {
        Indexed = indexed;
        Index = index;
        Value = value;
        StorageClass = storageClass;
    }

    public MirValueId Indexed { get; }
    public MirValueId Index { get; }
    public MirValueId Value { get; }
    public VariableStorageClass StorageClass { get; }

    public override IReadOnlyList<MirValueId> Uses => [Indexed, Index, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId indexed = mapping.TryGetValue(Indexed, out MirValueId mappedIndexed) ? mappedIndexed : Indexed;
        MirValueId index = mapping.TryGetValue(Index, out MirValueId mappedIndex) ? mappedIndex : Index;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return indexed == Indexed && index == Index && value == Value
            ? this
            : new MirStoreIndexInstruction(ResultType, indexed, index, value, StorageClass, Span);
    }
}

public sealed class MirStoreDerefInstruction : MirInstruction
{
    public MirStoreDerefInstruction(
        TypeSymbol? elementType,
        MirValueId address,
        MirValueId value,
        VariableStorageClass storageClass,
        TextSpan span)
        : base(result: null, resultType: elementType, span, hasSideEffects: true)
    {
        Address = address;
        Value = value;
        StorageClass = storageClass;
    }

    public MirValueId Address { get; }
    public MirValueId Value { get; }
    public VariableStorageClass StorageClass { get; }

    public override IReadOnlyList<MirValueId> Uses => [Address, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId address = mapping.TryGetValue(Address, out MirValueId mappedAddress) ? mappedAddress : Address;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return address == Address && value == Value
            ? this
            : new MirStoreDerefInstruction(ResultType, address, value, StorageClass, Span);
    }
}

public sealed class MirStorePlaceInstruction : MirInstruction
{
    public MirStorePlaceInstruction(StoragePlace place, MirValueId value, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Place = place;
        Value = value;
    }

    public StoragePlace Place { get; }
    public MirValueId Value { get; }

    public override IReadOnlyList<MirValueId> Uses => [Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mapped) ? mapped : Value;
        return value == Value ? this : new MirStorePlaceInstruction(Place, value, Span);
    }
}

public sealed class MirUpdatePlaceInstruction : MirInstruction
{
    public MirUpdatePlaceInstruction(
        StoragePlace place,
        BoundBinaryOperatorKind operatorKind,
        MirValueId value,
        TextSpan span,
        int? pointerArithmeticStride = null)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Place = place;
        OperatorKind = operatorKind;
        Value = value;
        PointerArithmeticStride = pointerArithmeticStride;
    }

    public StoragePlace Place { get; }
    public BoundBinaryOperatorKind OperatorKind { get; }
    public MirValueId Value { get; }
    public int? PointerArithmeticStride { get; }
    public bool IsPointerArithmetic => PointerArithmeticStride is not null;

    public override IReadOnlyList<MirValueId> Uses => [Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mapped) ? mapped : Value;
        return value == Value ? this : new MirUpdatePlaceInstruction(Place, OperatorKind, value, Span, PointerArithmeticStride);
    }
}

public sealed class MirInlineAsmBinding
{
    public MirInlineAsmBinding(InlineAsmBindingSlot slot, Symbol symbol, MirValueId? value, StoragePlace? place, InlineAsmBindingAccess access)
    {
        Slot = Requires.NotNull(slot);
        Symbol = Requires.NotNull(symbol);
        Value = value;
        Place = place;
        Access = access;
    }

    public InlineAsmBindingSlot Slot { get; }
    public string PlaceholderText => Slot.PlaceholderText;
    public Symbol Symbol { get; }
    public MirValueId? Value { get; }
    public StoragePlace? Place { get; }
    public InlineAsmBindingAccess Access { get; }
}

public sealed class MirInlineAsmInstruction : MirInstruction
{
    public MirInlineAsmInstruction(
        AsmVolatility volatility,
        string body,
        InlineAsmFlagOutput? flagOutput,
        IReadOnlyList<InlineAsmLine> parsedLines,
        IReadOnlyList<MirInlineAsmBinding> bindings,
        TextSpan span,
        MirValueId? flagResult = null,
        TypeSymbol? flagResultType = null)
        : base(result: flagResult, resultType: flagResultType, span, hasSideEffects: true)
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
    public IReadOnlyList<MirInlineAsmBinding> Bindings { get; }

    public override IReadOnlyList<MirValueId> Uses
    {
        get
        {
            List<MirValueId> uses = [];
            foreach (MirInlineAsmBinding binding in Bindings)
            {
                if (binding.Value is MirValueId value
                    && InlineAssemblyBindingAnalysis.IncludesRead(binding.Access))
                {
                    uses.Add(value);
                }
            }

            return uses;
        }
    }

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirInlineAsmBinding>? rewritten = null;
        for (int i = 0; i < Bindings.Count; i++)
        {
            MirInlineAsmBinding binding = Bindings[i];
            if (binding.Value is not MirValueId value || !mapping.TryGetValue(value, out MirValueId mapped) || mapped == value)
                continue;

            rewritten ??= new List<MirInlineAsmBinding>(Bindings);
            rewritten[i] = new MirInlineAsmBinding(binding.Slot, binding.Symbol, mapped, binding.Place, binding.Access);
        }

        return rewritten is null ? this : new MirInlineAsmInstruction(Volatility, Body, FlagOutput, ParsedLines, rewritten, Span, Result, ResultType);
    }
}

public sealed class MirYieldInstruction : MirInstruction
{
    public MirYieldInstruction(TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
    }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirYieldToInstruction : MirInstruction
{
    public MirYieldToInstruction(FunctionSymbol targetFunction, IReadOnlyList<MirValueId> arguments, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        TargetFunction = Requires.NotNull(targetFunction);
        Arguments = arguments;
    }

    public FunctionSymbol TargetFunction { get; }
    public IReadOnlyList<MirValueId> Arguments { get; }

    public override IReadOnlyList<MirValueId> Uses => Arguments;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Arguments.Count);
        bool changed = false;
        foreach (MirValueId argument in Arguments)
        {
            MirValueId mapped = mapping.TryGetValue(argument, out MirValueId value) ? value : argument;
            rewritten.Add(mapped);
            changed |= mapped != argument;
        }

        return changed ? new MirYieldToInstruction(TargetFunction, rewritten, Span) : this;
    }
}

public sealed class MirRepSetupInstruction : MirInstruction
{
    public MirRepSetupInstruction(MirValueId count, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Count = count;
    }

    public MirValueId Count { get; }

    public override IReadOnlyList<MirValueId> Uses => [Count];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId count = mapping.TryGetValue(Count, out MirValueId mapped) ? mapped : Count;
        return count == Count ? this : new MirRepSetupInstruction(count, Span);
    }
}

public sealed class MirRepIterInstruction : MirInstruction
{
    public MirRepIterInstruction(MirValueId count, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Count = count;
    }

    public MirValueId Count { get; }

    public override IReadOnlyList<MirValueId> Uses => [Count];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId count = mapping.TryGetValue(Count, out MirValueId mapped) ? mapped : Count;
        return count == Count ? this : new MirRepIterInstruction(count, Span);
    }
}

public sealed class MirRepForSetupInstruction : MirInstruction
{
    public MirRepForSetupInstruction(MirValueId start, MirValueId end, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Start = start;
        End = end;
    }

    public MirValueId Start { get; }
    public MirValueId End { get; }

    public override IReadOnlyList<MirValueId> Uses => [Start, End];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId start = mapping.TryGetValue(Start, out MirValueId mappedStart) ? mappedStart : Start;
        MirValueId end = mapping.TryGetValue(End, out MirValueId mappedEnd) ? mappedEnd : End;
        return start == Start && end == End ? this : new MirRepForSetupInstruction(start, end, Span);
    }
}

public sealed class MirRepForIterInstruction : MirInstruction
{
    public MirRepForIterInstruction(MirValueId start, MirValueId end, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Start = start;
        End = end;
    }

    public MirValueId Start { get; }
    public MirValueId End { get; }

    public override IReadOnlyList<MirValueId> Uses => [Start, End];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId start = mapping.TryGetValue(Start, out MirValueId mappedStart) ? mappedStart : Start;
        MirValueId end = mapping.TryGetValue(End, out MirValueId mappedEnd) ? mappedEnd : End;
        return start == Start && end == End ? this : new MirRepForIterInstruction(start, end, Span);
    }
}

public sealed class MirNoIrqBeginInstruction : MirInstruction
{
    public MirNoIrqBeginInstruction(TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
    }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirNoIrqEndInstruction : MirInstruction
{
    public MirNoIrqEndInstruction(TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
    }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public abstract class MirTerminator
{
    protected MirTerminator(TextSpan span)
    {
        Span = span;
    }

    public TextSpan Span { get; }
    public abstract IReadOnlyList<MirValueId> Uses { get; }
    public abstract MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping);
}

public sealed class MirGotoTerminator : MirTerminator
{
    public MirGotoTerminator(MirBlockRef target, IReadOnlyList<MirValueId> arguments, TextSpan span)
        : base(span)
    {
        Target = Requires.NotNull(target);
        Arguments = arguments;
    }

    public MirBlockRef Target { get; }
    public IReadOnlyList<MirValueId> Arguments { get; }

    public override IReadOnlyList<MirValueId> Uses => Arguments;

    public override MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Arguments.Count);
        bool changed = false;
        foreach (MirValueId arg in Arguments)
        {
            MirValueId mapped = mapping.TryGetValue(arg, out MirValueId value) ? value : arg;
            rewritten.Add(mapped);
            changed |= mapped != arg;
        }

        return changed ? new MirGotoTerminator(Target, rewritten, Span) : this;
    }
}

public sealed class MirBranchTerminator : MirTerminator
{
    public MirBranchTerminator(
        MirValueId condition,
        MirBlockRef trueTarget,
        MirBlockRef falseTarget,
        IReadOnlyList<MirValueId> trueArguments,
        IReadOnlyList<MirValueId> falseArguments,
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

    public MirValueId Condition { get; }
    public MirBlockRef TrueTarget { get; }
    public MirBlockRef FalseTarget { get; }
    public IReadOnlyList<MirValueId> TrueArguments { get; }
    public IReadOnlyList<MirValueId> FalseArguments { get; }

    /// <summary>
    /// When set, the branch consumes the hardware flag directly instead of testing a register.
    /// </summary>
    public MirFlag? ConditionFlag { get; }

    public override IReadOnlyList<MirValueId> Uses
    {
        get
        {
            List<MirValueId> uses = new(1 + TrueArguments.Count + FalseArguments.Count) { Condition };
            uses.AddRange(TrueArguments);
            uses.AddRange(FalseArguments);
            return uses;
        }
    }

    public override MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId condition = mapping.TryGetValue(Condition, out MirValueId mappedCond) ? mappedCond : Condition;
        List<MirValueId> rewrittenTrue = new(TrueArguments.Count);
        List<MirValueId> rewrittenFalse = new(FalseArguments.Count);
        bool changed = condition != Condition;

        foreach (MirValueId arg in TrueArguments)
        {
            MirValueId mapped = mapping.TryGetValue(arg, out MirValueId value) ? value : arg;
            rewrittenTrue.Add(mapped);
            changed |= mapped != arg;
        }

        foreach (MirValueId arg in FalseArguments)
        {
            MirValueId mapped = mapping.TryGetValue(arg, out MirValueId value) ? value : arg;
            rewrittenFalse.Add(mapped);
            changed |= mapped != arg;
        }

        return changed
            ? new MirBranchTerminator(condition, TrueTarget, FalseTarget, rewrittenTrue, rewrittenFalse, Span, ConditionFlag)
            : this;
    }
}

public sealed class MirReturnTerminator : MirTerminator
{
    public MirReturnTerminator(IReadOnlyList<MirValueId> values, TextSpan span)
        : base(span)
    {
        Values = values;
    }

    public IReadOnlyList<MirValueId> Values { get; }

    public override IReadOnlyList<MirValueId> Uses => Values;

    public override MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Values.Count);
        bool changed = false;
        foreach (MirValueId value in Values)
        {
            MirValueId mapped = mapping.TryGetValue(value, out MirValueId resolved) ? resolved : value;
            rewritten.Add(mapped);
            changed |= mapped != value;
        }

        return changed ? new MirReturnTerminator(rewritten, Span) : this;
    }
}

public sealed class MirUnreachableTerminator : MirTerminator
{
    public MirUnreachableTerminator(TextSpan span)
        : base(span)
    {
    }

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}
