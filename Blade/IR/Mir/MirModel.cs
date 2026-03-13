using System.Collections.Generic;
using Blade.IR;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Mir;

public readonly record struct MirValueId(int Id)
{
    public override string ToString() => $"%v{Id}";
}

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
        string name,
        bool isEntryPoint,
        FunctionKind kind,
        IReadOnlyList<TypeSymbol> returnTypes,
        IReadOnlyList<MirBlock> blocks)
    {
        Name = name;
        IsEntryPoint = isEntryPoint;
        Kind = kind;
        ReturnTypes = returnTypes;
        Blocks = blocks;
    }

    public string Name { get; }
    public bool IsEntryPoint { get; }
    public FunctionKind Kind { get; }
    public IReadOnlyList<TypeSymbol> ReturnTypes { get; }
    public IReadOnlyList<MirBlock> Blocks { get; }
}

public sealed class MirBlock
{
    public MirBlock(
        string label,
        IReadOnlyList<MirBlockParameter> parameters,
        IReadOnlyList<MirInstruction> instructions,
        MirTerminator terminator)
    {
        Label = label;
        Parameters = parameters;
        Instructions = instructions;
        Terminator = terminator;
    }

    public string Label { get; }
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
    public MirLoadSymbolInstruction(MirValueId result, TypeSymbol type, string symbolName, TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        SymbolName = symbolName;
    }

    public string SymbolName { get; }

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
        return source == Source ? this : new MirCopyInstruction(Result!.Value, ResultType!, source, Span);
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
        return operand == Operand ? this : new MirUnaryInstruction(Result!.Value, ResultType!, Operator, operand, Span);
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
        return new MirBinaryInstruction(Result!.Value, ResultType!, Operator, left, right, Span);
    }
}

public sealed class MirOpInstruction : MirInstruction
{
    public MirOpInstruction(
        string opcode,
        MirValueId? result,
        TypeSymbol? resultType,
        IReadOnlyList<MirValueId> operands,
        bool hasSideEffects,
        TextSpan span)
        : base(result, resultType, span, hasSideEffects)
    {
        Opcode = opcode;
        Operands = operands;
    }

    public string Opcode { get; }
    public IReadOnlyList<MirValueId> Operands { get; }

    public override IReadOnlyList<MirValueId> Uses => Operands;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Operands.Count);
        bool changed = false;
        foreach (MirValueId operand in Operands)
        {
            MirValueId mapped = mapping.TryGetValue(operand, out MirValueId value) ? value : operand;
            rewritten.Add(mapped);
            changed |= mapped != operand;
        }

        return changed
            ? new MirOpInstruction(Opcode, Result, ResultType, rewritten, HasSideEffects, Span)
            : this;
    }
}

public sealed class MirSelectInstruction : MirInstruction
{
    public MirSelectInstruction(
        MirValueId result,
        TypeSymbol type,
        MirValueId condition,
        MirValueId whenTrue,
        MirValueId whenFalse,
        TextSpan span)
        : base(result, type, span, hasSideEffects: false)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    public MirValueId Condition { get; }
    public MirValueId WhenTrue { get; }
    public MirValueId WhenFalse { get; }

    public override IReadOnlyList<MirValueId> Uses => [Condition, WhenTrue, WhenFalse];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId condition = mapping.TryGetValue(Condition, out MirValueId mappedCond) ? mappedCond : Condition;
        MirValueId whenTrue = mapping.TryGetValue(WhenTrue, out MirValueId mappedTrue) ? mappedTrue : WhenTrue;
        MirValueId whenFalse = mapping.TryGetValue(WhenFalse, out MirValueId mappedFalse) ? mappedFalse : WhenFalse;
        if (condition == Condition && whenTrue == WhenTrue && whenFalse == WhenFalse)
            return this;
        return new MirSelectInstruction(Result!.Value, ResultType!, condition, whenTrue, whenFalse, Span);
    }
}

public sealed class MirCallInstruction : MirInstruction
{
    public MirCallInstruction(
        MirValueId? result,
        TypeSymbol? resultType,
        string functionName,
        IReadOnlyList<MirValueId> arguments,
        TextSpan span)
        : base(result, resultType, span, hasSideEffects: true)
    {
        FunctionName = functionName;
        Arguments = arguments;
    }

    public string FunctionName { get; }
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

        return changed ? new MirCallInstruction(Result, ResultType, FunctionName, rewritten, Span) : this;
    }
}

public sealed class MirIntrinsicCallInstruction : MirInstruction
{
    public MirIntrinsicCallInstruction(
        MirValueId? result,
        TypeSymbol? resultType,
        string intrinsicName,
        IReadOnlyList<MirValueId> arguments,
        TextSpan span)
        : base(result, resultType, span, hasSideEffects: true)
    {
        IntrinsicName = intrinsicName;
        Arguments = arguments;
    }

    public string IntrinsicName { get; }
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

        return changed ? new MirIntrinsicCallInstruction(Result, ResultType, IntrinsicName, rewritten, Span) : this;
    }
}

public sealed class MirStoreInstruction : MirInstruction
{
    public MirStoreInstruction(string target, IReadOnlyList<MirValueId> operands, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Target = target;
        Operands = operands;
    }

    public string Target { get; }
    public IReadOnlyList<MirValueId> Operands { get; }

    public override IReadOnlyList<MirValueId> Uses => Operands;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Operands.Count);
        bool changed = false;
        foreach (MirValueId op in Operands)
        {
            MirValueId mapped = mapping.TryGetValue(op, out MirValueId value) ? value : op;
            rewritten.Add(mapped);
            changed |= mapped != op;
        }

        return changed ? new MirStoreInstruction(Target, rewritten, Span) : this;
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
    public MirUpdatePlaceInstruction(StoragePlace place, BoundBinaryOperatorKind operatorKind, MirValueId value, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Place = place;
        OperatorKind = operatorKind;
        Value = value;
    }

    public StoragePlace Place { get; }
    public BoundBinaryOperatorKind OperatorKind { get; }
    public MirValueId Value { get; }

    public override IReadOnlyList<MirValueId> Uses => [Value];

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        MirValueId value = mapping.TryGetValue(Value, out MirValueId mapped) ? mapped : Value;
        return value == Value ? this : new MirUpdatePlaceInstruction(Place, OperatorKind, value, Span);
    }
}

public sealed class MirInlineAsmBinding
{
    public MirInlineAsmBinding(string name, MirValueId? value, StoragePlace? place)
    {
        Name = name;
        Value = value;
        Place = place;
    }

    public string Name { get; }
    public MirValueId? Value { get; }
    public StoragePlace? Place { get; }
}

public sealed class MirInlineAsmInstruction : MirInstruction
{
    public MirInlineAsmInstruction(string body, IReadOnlyList<MirInlineAsmBinding> bindings, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects: true)
    {
        Body = body;
        Bindings = bindings;
    }

    public string Body { get; }
    public IReadOnlyList<MirInlineAsmBinding> Bindings { get; }

    public override IReadOnlyList<MirValueId> Uses
    {
        get
        {
            List<MirValueId> uses = [];
            foreach (MirInlineAsmBinding binding in Bindings)
            {
                if (binding.Value is MirValueId value)
                    uses.Add(value);
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
            rewritten[i] = new MirInlineAsmBinding(binding.Name, mapped, binding.Place);
        }

        return rewritten is null ? this : new MirInlineAsmInstruction(Body, rewritten, Span);
    }
}

public sealed class MirPseudoInstruction : MirInstruction
{
    public MirPseudoInstruction(string opcode, IReadOnlyList<MirValueId> operands, bool hasSideEffects, TextSpan span)
        : base(result: null, resultType: null, span, hasSideEffects)
    {
        Opcode = opcode;
        Operands = operands;
    }

    public string Opcode { get; }
    public IReadOnlyList<MirValueId> Operands { get; }

    public override IReadOnlyList<MirValueId> Uses => Operands;

    public override MirInstruction RewriteUses(IReadOnlyDictionary<MirValueId, MirValueId> mapping)
    {
        List<MirValueId> rewritten = new(Operands.Count);
        bool changed = false;
        foreach (MirValueId op in Operands)
        {
            MirValueId mapped = mapping.TryGetValue(op, out MirValueId value) ? value : op;
            rewritten.Add(mapped);
            changed |= mapped != op;
        }

        return changed ? new MirPseudoInstruction(Opcode, rewritten, HasSideEffects, Span) : this;
    }
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
    public MirGotoTerminator(string targetLabel, IReadOnlyList<MirValueId> arguments, TextSpan span)
        : base(span)
    {
        TargetLabel = targetLabel;
        Arguments = arguments;
    }

    public string TargetLabel { get; }
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

        return changed ? new MirGotoTerminator(TargetLabel, rewritten, Span) : this;
    }
}

public sealed class MirBranchTerminator : MirTerminator
{
    public MirBranchTerminator(
        MirValueId condition,
        string trueLabel,
        string falseLabel,
        IReadOnlyList<MirValueId> trueArguments,
        IReadOnlyList<MirValueId> falseArguments,
        TextSpan span)
        : base(span)
    {
        Condition = condition;
        TrueLabel = trueLabel;
        FalseLabel = falseLabel;
        TrueArguments = trueArguments;
        FalseArguments = falseArguments;
    }

    public MirValueId Condition { get; }
    public string TrueLabel { get; }
    public string FalseLabel { get; }
    public IReadOnlyList<MirValueId> TrueArguments { get; }
    public IReadOnlyList<MirValueId> FalseArguments { get; }

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
            ? new MirBranchTerminator(condition, TrueLabel, FalseLabel, rewrittenTrue, rewrittenFalse, Span)
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
