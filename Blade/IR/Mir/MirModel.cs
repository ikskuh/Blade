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

public sealed class MirModule(
    IReadOnlyList<StoragePlace> storagePlaces,
    IReadOnlyList<StorageDefinition> storageDefinitions,
    IReadOnlyList<MirFunction> functions)
{
    public IReadOnlyList<StoragePlace> StoragePlaces { get; } = storagePlaces;
    public IReadOnlyList<StorageDefinition> StorageDefinitions { get; } = storageDefinitions;
    public IReadOnlyList<MirFunction> Functions { get; } = functions;
}

public sealed class MirFunction(
    FunctionSymbol symbol,
    bool isEntryPoint,
    IReadOnlyList<BladeType> returnTypes,
    IReadOnlyList<MirBlock> blocks,
    IReadOnlyList<ReturnSlot>? returnSlots = null,
    IReadOnlyDictionary<MirValueId, MirFlag>? flagValues = null)
{
    public FunctionSymbol Symbol { get; } = Requires.NotNull(symbol);
    public string Name => Symbol.Name;
    public bool IsEntryPoint { get; } = isEntryPoint;
    public FunctionKind Kind => Symbol.Kind;
    public FunctionInliningPolicy InliningPolicy => Symbol.InliningPolicy;
    public IReadOnlyList<BladeType> ReturnTypes { get; } = returnTypes;
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; } = returnSlots ?? [];
    public IReadOnlyDictionary<MirValueId, MirFlag> FlagValues { get; } = flagValues ?? new Dictionary<MirValueId, MirFlag>();
    public IReadOnlyList<MirBlock> Blocks { get; } = blocks;
}

public sealed class MirBlock(
    MirBlockRef blockRef,
    IReadOnlyList<MirBlockParameter> parameters,
    IReadOnlyList<MirInstruction> instructions,
    MirTerminator terminator)
{
    public MirBlockRef Ref { get; } = Requires.NotNull(blockRef);
    public IReadOnlyList<MirBlockParameter> Parameters { get; } = parameters;
    public IReadOnlyList<MirInstruction> Instructions { get; } = instructions;
    public MirTerminator Terminator { get; } = terminator;
}

public sealed class MirBlockParameter(MirValueId value, string name, BladeType type)
{
    public MirValueId Value { get; } = value;
    public string Name { get; } = name;
    public BladeType Type { get; } = type;
}

public abstract class MirInstruction(MirValueId? result, BladeType? resultType, TextSpan span, bool hasSideEffects)
{
    public MirValueId? Result { get; } = result;
    public BladeType? ResultType { get; } = resultType;
    public TextSpan Span { get; } = span;
    public bool HasSideEffects { get; } = hasSideEffects;

    public abstract IReadOnlyList<MirValueId> Uses { get; }
    public abstract MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping);
}

public sealed class MirConstantInstruction(MirValueId result, BladeType type, BladeValue? value, TextSpan span)
    : MirInstruction(result, type, span, hasSideEffects: false)
{
    public BladeValue? Value { get; } = value;

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirLoadPlaceInstruction(MirValueId result, BladeType type, StoragePlace place, TextSpan span)
    : MirInstruction(result, type, span, hasSideEffects: false)
{
    public StoragePlace Place { get; } = place;

    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirCopyInstruction(MirValueId result, BladeType type, MirValueId source, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public MirValueId Source { get; } = source;

    public override IReadOnlyList<MirValueId> Uses => [Source];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId source = mapping.TryGetValue(Source, out MirValueId mapped) ? mapped : Source;
        return source == Source ? this : new MirCopyInstruction(Result!, ResultType!, source, Span);
    }
}

public sealed class MirUnaryInstruction(MirValueId result, BladeType type, BoundUnaryOperatorKind op, MirValueId operand, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public BoundUnaryOperatorKind Operator { get; } = op;
    public MirValueId Operand { get; } = operand;

    public override IReadOnlyList<MirValueId> Uses => [Operand];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId operand = mapping.TryGetValue(Operand, out MirValueId mapped) ? mapped : Operand;
        return operand == Operand ? this : new MirUnaryInstruction(Result!, ResultType!, Operator, operand, Span);
    }
}

public sealed class MirBinaryInstruction(
    MirValueId result,
    BladeType type,
    BoundBinaryOperatorKind op,
    MirValueId left,
    MirValueId right,
    TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public BoundBinaryOperatorKind Operator { get; } = op;
    public MirValueId Left { get; } = left;
    public MirValueId Right { get; } = right;

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

public sealed class MirPointerOffsetInstruction(
    MirValueId result,
    BladeType type,
    BoundBinaryOperatorKind operatorKind,
    MirValueId baseAddress,
    MirValueId delta,
    int stride,
    TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public BoundBinaryOperatorKind OperatorKind { get; } = operatorKind;
    public MirValueId BaseAddress { get; } = baseAddress;
    public MirValueId Delta { get; } = delta;
    public int Stride { get; } = stride;

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

public sealed class MirPointerDifferenceInstruction(
    MirValueId result,
    BladeType type,
    MirValueId left,
    MirValueId right,
    int stride,
    TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public MirValueId Left { get; } = left;
    public MirValueId Right { get; } = right;
    public int Stride { get; } = stride;

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

public sealed class MirConvertInstruction(MirValueId result, BladeType type, MirValueId operand, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public MirValueId Operand { get; } = operand;

    public override IReadOnlyList<MirValueId> Uses => [Operand];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId operand = mapping.TryGetValue(Operand, out MirValueId mapped) ? mapped : Operand;
        return operand == Operand ? this : new MirConvertInstruction(Result!, ResultType!, operand, Span);
    }
}

public sealed class MirStructLiteralField(AggregateMemberSymbol member, MirValueId value)
{
    public AggregateMemberSymbol Member { get; } = member;
    public MirValueId Value { get; } = value;
}

public sealed class MirStructLiteralInstruction(MirValueId result, StructTypeSymbol type, IReadOnlyList<MirStructLiteralField> fields, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public IReadOnlyList<MirStructLiteralField> Fields { get; } = fields;

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

public sealed class MirLoadMemberInstruction(MirValueId result, BladeType type, MirValueId receiver, AggregateMemberSymbol member, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public MirValueId Receiver { get; } = receiver;
    public AggregateMemberSymbol Member { get; } = member;

    public override IReadOnlyList<MirValueId> Uses => [Receiver];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mapped) ? mapped : Receiver;
        return receiver == Receiver
            ? this
            : new MirLoadMemberInstruction(Result!, ResultType!, receiver, Member, Span);
    }
}

public sealed class MirLoadIndexInstruction(
    MirValueId result,
    BladeType type,
    BladeType indexedType,
    MirValueId indexed,
    MirValueId index,
    VariableStorageClass storageClass,
    bool hasSideEffects,
    TextSpan span) : MirInstruction(result, type, span, hasSideEffects)
{
    public BladeType IndexedType { get; } = Requires.NotNull(indexedType);
    public MirValueId Indexed { get; } = indexed;
    public MirValueId Index { get; } = index;
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override IReadOnlyList<MirValueId> Uses => [Indexed, Index];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId indexed = mapping.TryGetValue(Indexed, out MirValueId mappedIndexed) ? mappedIndexed : Indexed;
        MirValueId index = mapping.TryGetValue(Index, out MirValueId mappedIndex) ? mappedIndex : Index;
        return indexed == Indexed && index == Index
            ? this
            : new MirLoadIndexInstruction(Result!, ResultType!, IndexedType, indexed, index, StorageClass, HasSideEffects, Span);
    }
}

public sealed class MirLoadDerefInstruction(
    MirValueId result,
    BladeType type,
    BladeType pointerType,
    MirValueId address,
    VariableStorageClass storageClass,
    bool hasSideEffects,
    TextSpan span) : MirInstruction(result, type, span, hasSideEffects)
{
    public BladeType PointerType { get; } = Requires.NotNull(pointerType);
    public MirValueId Address { get; } = address;
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override IReadOnlyList<MirValueId> Uses => [Address];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId address = mapping.TryGetValue(Address, out MirValueId mapped) ? mapped : Address;
        return address == Address
            ? this
            : new MirLoadDerefInstruction(Result!, ResultType!, PointerType, address, StorageClass, HasSideEffects, Span);
    }
}

public sealed class MirBitfieldExtractInstruction(MirValueId result, BladeType type, MirValueId receiver, AggregateMemberSymbol member, TextSpan span) : MirInstruction(result, type, span, hasSideEffects: false)
{
    public MirValueId Receiver { get; } = receiver;
    public AggregateMemberSymbol Member { get; } = member;

    public override IReadOnlyList<MirValueId> Uses => [Receiver];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId receiver = mapping.TryGetValue(Receiver, out MirValueId mapped) ? mapped : Receiver;
        return receiver == Receiver
            ? this
            : new MirBitfieldExtractInstruction(Result!, ResultType!, receiver, Member, Span);
    }
}

public sealed class MirBitfieldInsertInstruction(
    MirValueId result,
    BladeType aggregateType,
    MirValueId receiver,
    MirValueId value,
    AggregateMemberSymbol member,
    TextSpan span) : MirInstruction(result, aggregateType, span, hasSideEffects: false)
{
    public MirValueId Receiver { get; } = receiver;
    public MirValueId Value { get; } = value;
    public AggregateMemberSymbol Member { get; } = member;

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

public sealed class MirInsertMemberInstruction(
    MirValueId result,
    BladeType aggregateType,
    MirValueId receiver,
    MirValueId value,
    AggregateMemberSymbol member,
    TextSpan span) : MirInstruction(result, aggregateType, span, hasSideEffects: false)
{
    public MirValueId Receiver { get; } = receiver;
    public MirValueId Value { get; } = value;
    public AggregateMemberSymbol Member { get; } = member;

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

public sealed class MirCallInstruction(
    MirValueId? result,
    BladeType? resultType,
    FunctionSymbol function,
    IReadOnlyList<MirValueId> arguments,
    TextSpan span,
    IReadOnlyList<(MirValueId Value, BladeType Type)>? extraResults = null)
    : MirInstruction(result, resultType, span, hasSideEffects: true)
{
    public FunctionSymbol Function { get; } = Requires.NotNull(function);
    public IReadOnlyList<MirValueId> Arguments { get; } = arguments;
    public IReadOnlyList<(MirValueId Value, BladeType Type)> ExtraResults { get; } = extraResults ?? [];

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

        List<(MirValueId, BladeType)>? rewrittenExtra = null;
        for (int i = 0; i < ExtraResults.Count; i++)
        {
            (MirValueId extraVal, BladeType extraType) = ExtraResults[i];
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

public sealed class MirIntrinsicCallInstruction(
    MirValueId? result,
    BladeType? resultType,
    P2Mnemonic mnemonic,
    IReadOnlyList<MirValueId> arguments,
    TextSpan span)
    : MirInstruction(result, resultType, span, hasSideEffects: true)
{
    public P2Mnemonic Mnemonic { get; } = mnemonic;
    public IReadOnlyList<MirValueId> Arguments { get; } = arguments;

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

public sealed class MirStoreIndexInstruction(
    BladeType? elementType,
    BladeType indexedType,
    MirValueId indexed,
    MirValueId index,
    MirValueId value,
    VariableStorageClass storageClass,
    TextSpan span)
    : MirInstruction(result: null, resultType: elementType, span, hasSideEffects: true)
{
    public BladeType IndexedType { get; } = Requires.NotNull(indexedType);
    public MirValueId Indexed { get; } = indexed;
    public MirValueId Index { get; } = index;
    public MirValueId Value { get; } = value;
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override IReadOnlyList<MirValueId> Uses => [Indexed, Index, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId indexed = mapping.TryGetValue(Indexed, out MirValueId mappedIndexed) ? mappedIndexed : Indexed;
        MirValueId index = mapping.TryGetValue(Index, out MirValueId mappedIndex) ? mappedIndex : Index;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return indexed == Indexed && index == Index && value == Value
            ? this
            : new MirStoreIndexInstruction(ResultType, IndexedType, indexed, index, value, StorageClass, Span);
    }
}

public sealed class MirStoreDerefInstruction(
    BladeType? elementType,
    BladeType pointerType,
    MirValueId address,
    MirValueId value,
    VariableStorageClass storageClass,
    TextSpan span)
    : MirInstruction(result: null, resultType: elementType, span, hasSideEffects: true)
{
    public BladeType PointerType { get; } = Requires.NotNull(pointerType);
    public MirValueId Address { get; } = address;
    public MirValueId Value { get; } = value;
    public VariableStorageClass StorageClass { get; } = storageClass;

    public override IReadOnlyList<MirValueId> Uses => [Address, Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId address = mapping.TryGetValue(Address, out MirValueId mappedAddress) ? mappedAddress : Address;
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mappedValue) ? mappedValue : Value;
        return address == Address && value == Value
            ? this
            : new MirStoreDerefInstruction(ResultType, PointerType, address, value, StorageClass, Span);
    }
}

public sealed class MirStorePlaceInstruction(StoragePlace place, MirValueId value, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public StoragePlace Place { get; } = place;
    public MirValueId Value { get; } = value;

    public override IReadOnlyList<MirValueId> Uses => [Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mapped) ? mapped : Value;
        return value == Value ? this : new MirStorePlaceInstruction(Place, value, Span);
    }
}

public sealed class MirUpdatePlaceInstruction(
    StoragePlace place,
    BoundBinaryOperatorKind operatorKind,
    MirValueId value,
    TextSpan span,
    int? pointerArithmeticStride = null)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public StoragePlace Place { get; } = place;
    public BoundBinaryOperatorKind OperatorKind { get; } = operatorKind;
    public MirValueId Value { get; } = value;
    public int? PointerArithmeticStride { get; } = pointerArithmeticStride;
    public bool IsPointerArithmetic => PointerArithmeticStride is not null;

    public override IReadOnlyList<MirValueId> Uses => [Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mapped) ? mapped : Value;
        return value == Value ? this : new MirUpdatePlaceInstruction(Place, OperatorKind, value, Span, PointerArithmeticStride);
    }
}

public sealed class MirInlineAsmBinding(InlineAsmBindingSlot slot, Symbol symbol, MirValueId? value, StoragePlace? place, InlineAsmBindingAccess access)
{
    public InlineAsmBindingSlot Slot { get; } = Requires.NotNull(slot);
    public string PlaceholderText => Slot.PlaceholderText;
    public Symbol Symbol { get; } = Requires.NotNull(symbol);
    public MirValueId? Value { get; } = value;
    public StoragePlace? Place { get; } = place;
    public InlineAsmBindingAccess Access { get; } = access;
}

public sealed class MirInlineAsmInstruction(
    AsmVolatility volatility,
    InlineAsmFlagOutput? flagOutput,
    IReadOnlyList<InlineAsmLine> parsedLines,
    IReadOnlyList<MirInlineAsmBinding> bindings,
    TextSpan span,
    MirValueId? flagResult = null,
    BladeType? flagResultType = null) : MirInstruction(result: flagResult, resultType: flagResultType, span, hasSideEffects: true)
{
    public AsmVolatility Volatility { get; } = volatility;
    public InlineAsmFlagOutput? FlagOutput { get; } = flagOutput;
    public IReadOnlyList<InlineAsmLine> ParsedLines { get; } = parsedLines;
    public IReadOnlyList<MirInlineAsmBinding> Bindings { get; } = bindings;

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

        return rewritten is null ? this : new MirInlineAsmInstruction(Volatility, FlagOutput, ParsedLines, rewritten, Span, Result, ResultType);
    }
}

public sealed class MirYieldInstruction(TextSpan span) : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirYieldToInstruction(FunctionSymbol targetFunction, IReadOnlyList<MirValueId> arguments, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public FunctionSymbol TargetFunction { get; } = Requires.NotNull(targetFunction);
    public IReadOnlyList<MirValueId> Arguments { get; } = arguments;

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

public sealed class MirRepSetupInstruction(MirValueId count, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public MirValueId Count { get; } = count;

    public override IReadOnlyList<MirValueId> Uses => [Count];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId count = mapping.TryGetValue(Count, out MirValueId mapped) ? mapped : Count;
        return count == Count ? this : new MirRepSetupInstruction(count, Span);
    }
}

public sealed class MirRepIterInstruction(MirValueId count, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public MirValueId Count { get; } = count;

    public override IReadOnlyList<MirValueId> Uses => [Count];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId count = mapping.TryGetValue(Count, out MirValueId mapped) ? mapped : Count;
        return count == Count ? this : new MirRepIterInstruction(count, Span);
    }
}

public sealed class MirRepForSetupInstruction(MirValueId start, MirValueId end, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public MirValueId Start { get; } = start;
    public MirValueId End { get; } = end;

    public override IReadOnlyList<MirValueId> Uses => [Start, End];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId start = mapping.TryGetValue(Start, out MirValueId mappedStart) ? mappedStart : Start;
        MirValueId end = mapping.TryGetValue(End, out MirValueId mappedEnd) ? mappedEnd : End;
        return start == Start && end == End ? this : new MirRepForSetupInstruction(start, end, Span);
    }
}

public sealed class MirRepForIterInstruction(MirValueId start, MirValueId end, TextSpan span)
    : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public MirValueId Start { get; } = start;
    public MirValueId End { get; } = end;

    public override IReadOnlyList<MirValueId> Uses => [Start, End];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId start = mapping.TryGetValue(Start, out MirValueId mappedStart) ? mappedStart : Start;
        MirValueId end = mapping.TryGetValue(End, out MirValueId mappedEnd) ? mappedEnd : End;
        return start == Start && end == End ? this : new MirRepForIterInstruction(start, end, Span);
    }
}

public sealed class MirNoIrqBeginInstruction(TextSpan span) : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public sealed class MirNoIrqEndInstruction(TextSpan span) : MirInstruction(result: null, resultType: null, span, hasSideEffects: true)
{
    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}

public abstract class MirTerminator(TextSpan span)
{
    public TextSpan Span { get; } = span;
    public abstract IReadOnlyList<MirValueId> Uses { get; }
    public abstract MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping);
}

public sealed class MirGotoTerminator(MirBlockRef target, IReadOnlyList<MirValueId> arguments, TextSpan span) : MirTerminator(span)
{
    public MirBlockRef Target { get; } = Requires.NotNull(target);
    public IReadOnlyList<MirValueId> Arguments { get; } = arguments;

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

public sealed class MirBranchTerminator(
    MirValueId condition,
    MirBlockRef trueTarget,
    MirBlockRef falseTarget,
    IReadOnlyList<MirValueId> trueArguments,
    IReadOnlyList<MirValueId> falseArguments,
    TextSpan span,
    MirFlag? conditionFlag = null) : MirTerminator(span)
{
    public MirValueId Condition { get; } = condition;
    public MirBlockRef TrueTarget { get; } = Requires.NotNull(trueTarget);
    public MirBlockRef FalseTarget { get; } = Requires.NotNull(falseTarget);
    public IReadOnlyList<MirValueId> TrueArguments { get; } = trueArguments;
    public IReadOnlyList<MirValueId> FalseArguments { get; } = falseArguments;

    /// <summary>
    /// When set, the branch consumes the hardware flag directly instead of testing a register.
    /// </summary>
    public MirFlag? ConditionFlag { get; } = conditionFlag;

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

public sealed class MirReturnTerminator(IReadOnlyList<MirValueId> values, TextSpan span) : MirTerminator(span)
{
    public IReadOnlyList<MirValueId> Values { get; } = values;

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

public sealed class MirUnreachableTerminator(TextSpan span) : MirTerminator(span)
{
    public override IReadOnlyList<MirValueId> Uses => [];

    public override MirTerminator RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping) => this;
}
