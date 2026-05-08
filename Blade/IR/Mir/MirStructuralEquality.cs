using System;
using System.Collections.Generic;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR.Mir;

/// <summary>
/// Compares MIR graphs structurally while allowing alpha-equivalent block/value identities.
/// </summary>
internal static class MirStructuralEquality
{
    /// <summary>
    /// Determines whether two MIR modules are structurally equivalent.
    /// </summary>
    internal static bool StructurallyEqualTo(this MirModule left, MirModule right)
    {
        Requires.NotNull(left);
        Requires.NotNull(right);

        if (ReferenceEquals(left, right))
            return true;

        if (!ReferenceEquals(left.Image, right.Image))
            return false;

        if (!StoragePlacesEqual(left.StoragePlaces, right.StoragePlaces))
            return false;

        if (!StorageDefinitionsEqual(left.StorageDefinitions, right.StorageDefinitions))
            return false;

        if (left.Functions.Count != right.Functions.Count)
            return false;

        for (int functionIndex = 0; functionIndex < left.Functions.Count; functionIndex++)
        {
            if (!FunctionsEqual(left.Functions[functionIndex], right.Functions[functionIndex]))
                return false;
        }

        return true;
    }

    private static bool FunctionsEqual(MirFunction left, MirFunction right)
    {
        if (!ReferenceEquals(left.Symbol, right.Symbol)
            || left.IsEntryPoint != right.IsEntryPoint
            || !TypeListsEqual(left.ReturnTypes, right.ReturnTypes)
            || !ReturnSlotsEqual(left.ReturnSlots, right.ReturnSlots)
            || left.Blocks.Count != right.Blocks.Count)
        {
            return false;
        }

        FunctionContext context = new();
        for (int blockIndex = 0; blockIndex < left.Blocks.Count; blockIndex++)
        {
            if (!context.BindBlock(left.Blocks[blockIndex].Ref, right.Blocks[blockIndex].Ref))
                return false;
        }

        for (int blockIndex = 0; blockIndex < left.Blocks.Count; blockIndex++)
        {
            if (!BlocksEqual(left.Blocks[blockIndex], right.Blocks[blockIndex], context))
                return false;
        }

        return FlagMapsEqual(left.FlagValues, right.FlagValues, context);
    }

    private static bool BlocksEqual(MirBlock left, MirBlock right, FunctionContext context)
    {
        if (!context.BlockEquals(left.Ref, right.Ref)
            || left.Parameters.Count != right.Parameters.Count
            || left.Instructions.Count != right.Instructions.Count)
        {
            return false;
        }

        for (int parameterIndex = 0; parameterIndex < left.Parameters.Count; parameterIndex++)
        {
            MirBlockParameter leftParameter = left.Parameters[parameterIndex];
            MirBlockParameter rightParameter = right.Parameters[parameterIndex];
            if (leftParameter.Name != rightParameter.Name
                || !TypeEquals(leftParameter.Type, rightParameter.Type)
                || !context.BindValue(leftParameter.Value, rightParameter.Value))
            {
                return false;
            }
        }

        for (int instructionIndex = 0; instructionIndex < left.Instructions.Count; instructionIndex++)
        {
            if (!InstructionsEqual(left.Instructions[instructionIndex], right.Instructions[instructionIndex], context))
                return false;
        }

        return TerminatorsEqual(left.Terminator, right.Terminator, context);
    }

    private static bool InstructionsEqual(MirInstruction left, MirInstruction right, FunctionContext context)
    {
        if (left.GetType() != right.GetType()
            || left.HasSideEffects != right.HasSideEffects
            || !TypeEquals(left.ResultType, right.ResultType))
        {
            return false;
        }

        if ((left.Result is null) != (right.Result is null))
            return false;

        if (left.Result is MirValueId leftResult
            && right.Result is MirValueId rightResult
            && !context.BindValue(leftResult, rightResult))
        {
            return false;
        }

        return (left, right) switch
        {
            (MirConstantInstruction lhs, MirConstantInstruction rhs) => Equals(lhs.Value, rhs.Value),
            (MirLoadPlaceInstruction lhs, MirLoadPlaceInstruction rhs) => ReferenceEquals(lhs.Place, rhs.Place),
            (MirCopyInstruction lhs, MirCopyInstruction rhs) => context.ValueEquals(lhs.Source, rhs.Source),
            (MirUnaryInstruction lhs, MirUnaryInstruction rhs) =>
                lhs.Operator == rhs.Operator
                && context.ValueEquals(lhs.Operand, rhs.Operand),
            (MirBinaryInstruction lhs, MirBinaryInstruction rhs) =>
                lhs.Operator == rhs.Operator
                && lhs.ComparisonLoweringKind == rhs.ComparisonLoweringKind
                && context.ValueEquals(lhs.Left, rhs.Left)
                && context.ValueEquals(lhs.Right, rhs.Right),
            (MirPointerOffsetInstruction lhs, MirPointerOffsetInstruction rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && lhs.Stride == rhs.Stride
                && context.ValueEquals(lhs.BaseAddress, rhs.BaseAddress)
                && context.ValueEquals(lhs.Delta, rhs.Delta),
            (MirPointerDifferenceInstruction lhs, MirPointerDifferenceInstruction rhs) =>
                lhs.Stride == rhs.Stride
                && context.ValueEquals(lhs.Left, rhs.Left)
                && context.ValueEquals(lhs.Right, rhs.Right),
            (MirConvertInstruction lhs, MirConvertInstruction rhs) => context.ValueEquals(lhs.Operand, rhs.Operand),
            (MirStructLiteralInstruction lhs, MirStructLiteralInstruction rhs) => StructLiteralFieldsEqual(lhs.Fields, rhs.Fields, context),
            (MirLoadMemberInstruction lhs, MirLoadMemberInstruction rhs) =>
                ReferenceEquals(lhs.Member, rhs.Member)
                && context.ValueEquals(lhs.Receiver, rhs.Receiver),
            (MirLoadIndexInstruction lhs, MirLoadIndexInstruction rhs) =>
                TypeEquals(lhs.IndexedType, rhs.IndexedType)
                && lhs.StorageClass == rhs.StorageClass
                && context.ValueEquals(lhs.Indexed, rhs.Indexed)
                && context.ValueEquals(lhs.Index, rhs.Index),
            (MirLoadDerefInstruction lhs, MirLoadDerefInstruction rhs) =>
                TypeEquals(lhs.PointerType, rhs.PointerType)
                && lhs.StorageClass == rhs.StorageClass
                && context.ValueEquals(lhs.Address, rhs.Address),
            (MirBitfieldExtractInstruction lhs, MirBitfieldExtractInstruction rhs) =>
                ReferenceEquals(lhs.Member, rhs.Member)
                && context.ValueEquals(lhs.Receiver, rhs.Receiver),
            (MirBitfieldInsertInstruction lhs, MirBitfieldInsertInstruction rhs) =>
                ReferenceEquals(lhs.Member, rhs.Member)
                && context.ValueEquals(lhs.Receiver, rhs.Receiver)
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirInsertMemberInstruction lhs, MirInsertMemberInstruction rhs) =>
                ReferenceEquals(lhs.Member, rhs.Member)
                && context.ValueEquals(lhs.Receiver, rhs.Receiver)
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirCallInstruction lhs, MirCallInstruction rhs) =>
                ReferenceEquals(lhs.Function, rhs.Function)
                && ValueListsEqual(lhs.Arguments, rhs.Arguments, context)
                && ExtraResultsEqual(lhs.ExtraResults, rhs.ExtraResults, context),
            (MirSpawnInstruction lhs, MirSpawnInstruction rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && ReferenceEquals(lhs.Task, rhs.Task)
                && lhs.RequestedResultCount == rhs.RequestedResultCount
                && ValueListsEqual(lhs.Arguments, rhs.Arguments, context)
                && ExtraResultsEqual(lhs.ExtraResults, rhs.ExtraResults, context),
            (MirIntrinsicCallInstruction lhs, MirIntrinsicCallInstruction rhs) =>
                lhs.Mnemonic == rhs.Mnemonic
                && ValueListsEqual(lhs.Arguments, rhs.Arguments, context),
            (MirStoreIndexInstruction lhs, MirStoreIndexInstruction rhs) =>
                TypeEquals(lhs.ResultType, rhs.ResultType)
                && TypeEquals(lhs.IndexedType, rhs.IndexedType)
                && lhs.StorageClass == rhs.StorageClass
                && context.ValueEquals(lhs.Indexed, rhs.Indexed)
                && context.ValueEquals(lhs.Index, rhs.Index)
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirStoreDerefInstruction lhs, MirStoreDerefInstruction rhs) =>
                TypeEquals(lhs.ResultType, rhs.ResultType)
                && TypeEquals(lhs.PointerType, rhs.PointerType)
                && lhs.StorageClass == rhs.StorageClass
                && context.ValueEquals(lhs.Address, rhs.Address)
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirStorePlaceInstruction lhs, MirStorePlaceInstruction rhs) =>
                ReferenceEquals(lhs.Place, rhs.Place)
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirUpdatePlaceInstruction lhs, MirUpdatePlaceInstruction rhs) =>
                ReferenceEquals(lhs.Place, rhs.Place)
                && lhs.OperatorKind == rhs.OperatorKind
                && lhs.PointerArithmeticStride == rhs.PointerArithmeticStride
                && context.ValueEquals(lhs.Value, rhs.Value),
            (MirInlineAsmInstruction lhs, MirInlineAsmInstruction rhs) =>
                lhs.Volatility == rhs.Volatility
                && lhs.FlagOutput == rhs.FlagOutput
                && InlineAsmLinesEqual(lhs.ParsedLines, rhs.ParsedLines)
                && MirInlineAsmBindingsEqual(lhs.Bindings, rhs.Bindings, context),
            (MirYieldInstruction, MirYieldInstruction) => true,
            (MirYieldToInstruction lhs, MirYieldToInstruction rhs) =>
                ReferenceEquals(lhs.TargetFunction, rhs.TargetFunction)
                && ValueListsEqual(lhs.Arguments, rhs.Arguments, context),
            (MirRepSetupInstruction lhs, MirRepSetupInstruction rhs) => context.ValueEquals(lhs.Count, rhs.Count),
            (MirRepIterInstruction lhs, MirRepIterInstruction rhs) => context.ValueEquals(lhs.Count, rhs.Count),
            (MirRepForSetupInstruction lhs, MirRepForSetupInstruction rhs) =>
                context.ValueEquals(lhs.Start, rhs.Start)
                && context.ValueEquals(lhs.End, rhs.End),
            (MirRepForIterInstruction lhs, MirRepForIterInstruction rhs) =>
                lhs.IndexCarrierOrdinal == rhs.IndexCarrierOrdinal
                && ValueListsEqual(lhs.CarrierValues, rhs.CarrierValues, context)
                && ValueListsEqual(lhs.CurrentValues, rhs.CurrentValues, context),
            (MirNoIrqBeginInstruction, MirNoIrqBeginInstruction) => true,
            (MirNoIrqEndInstruction, MirNoIrqEndInstruction) => true,
            _ => Assert.UnreachableValue<bool>($"Unhandled MIR instruction type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool TerminatorsEqual(MirTerminator left, MirTerminator right, FunctionContext context)
    {
        return (left, right) switch
        {
            (MirGotoTerminator lhs, MirGotoTerminator rhs) =>
                context.BlockEquals(lhs.Target, rhs.Target)
                && ValueListsEqual(lhs.Arguments, rhs.Arguments, context),
            (MirBranchTerminator lhs, MirBranchTerminator rhs) =>
                lhs.ConditionFlag == rhs.ConditionFlag
                && context.ValueEquals(lhs.Condition, rhs.Condition)
                && context.BlockEquals(lhs.TrueTarget, rhs.TrueTarget)
                && context.BlockEquals(lhs.FalseTarget, rhs.FalseTarget)
                && ValueListsEqual(lhs.TrueArguments, rhs.TrueArguments, context)
                && ValueListsEqual(lhs.FalseArguments, rhs.FalseArguments, context),
            (MirReturnTerminator lhs, MirReturnTerminator rhs) => ValueListsEqual(lhs.Values, rhs.Values, context),
            (MirUnreachableTerminator, MirUnreachableTerminator) => true,
            _ => false,
        };
    }

    private static bool StoragePlacesEqual(IReadOnlyList<StoragePlace> left, IReadOnlyList<StoragePlace> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool StorageDefinitionsEqual(IReadOnlyList<StorageDefinition> left, IReadOnlyList<StorageDefinition> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i].Place, right[i].Place)
                || !Equals(left[i].InitialValue, right[i].InitialValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StructLiteralFieldsEqual(IReadOnlyList<MirStructLiteralField> left, IReadOnlyList<MirStructLiteralField> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i].Member, right[i].Member)
                || !context.ValueEquals(left[i].Value, right[i].Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExtraResultsEqual(IReadOnlyList<(MirValueId Value, BladeType Type)> left, IReadOnlyList<(MirValueId Value, BladeType Type)> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!TypeEquals(left[i].Type, right[i].Type)
                || !context.BindValue(left[i].Value, right[i].Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValueListsEqual(IReadOnlyList<MirValueId> left, IReadOnlyList<MirValueId> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!context.ValueEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool MirInlineAsmBindingsEqual(IReadOnlyList<MirInlineAsmBinding> left, IReadOnlyList<MirInlineAsmBinding> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            MirInlineAsmBinding lhs = left[i];
            MirInlineAsmBinding rhs = right[i];
            if (!ReferenceEquals(lhs.Slot, rhs.Slot)
                || !ReferenceEquals(lhs.Symbol, rhs.Symbol)
                || !ReferenceEquals(lhs.Place, rhs.Place)
                || lhs.Access != rhs.Access)
            {
                return false;
            }

            if ((lhs.Value is null) != (rhs.Value is null))
                return false;

            if (lhs.Value is MirValueId leftValue
                && rhs.Value is MirValueId rightValue
                && !context.ValueEquals(leftValue, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FlagMapsEqual(IReadOnlyDictionary<MirValueId, MirFlag> left, IReadOnlyDictionary<MirValueId, MirFlag> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        Dictionary<MirValueId, MirFlag> remaining = new(right);
        foreach ((MirValueId leftValue, MirFlag leftFlag) in left)
        {
            if (!context.TryGetMappedValue(leftValue, out MirValueId rightValue)
                || !remaining.TryGetValue(rightValue, out MirFlag rightFlag)
                || leftFlag != rightFlag)
            {
                return false;
            }

            remaining.Remove(rightValue);
        }

        return remaining.Count == 0;
    }

    private static bool TypeListsEqual(IReadOnlyList<BladeType> left, IReadOnlyList<BladeType> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!TypeEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool ReturnSlotsEqual(IReadOnlyList<ReturnSlot> left, IReadOnlyList<ReturnSlot> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!TypeEquals(left[i].Type, right[i].Type)
                || left[i].Placement != right[i].Placement)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TypeEquals(BladeType? left, BladeType? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    private static bool InlineAsmLinesEqual(IReadOnlyList<InlineAsmLine> left, IReadOnlyList<InlineAsmLine> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!InlineAsmLineEqual(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool InlineAsmLineEqual(InlineAsmLine left, InlineAsmLine right)
    {
        if (left.GetType() != right.GetType()
            || left.TrailingComment != right.TrailingComment)
        {
            return false;
        }

        return (left, right) switch
        {
            (InlineAsmCommentLine lhs, InlineAsmCommentLine rhs) => lhs.Comment == rhs.Comment,
            (InlineAsmLabelLine lhs, InlineAsmLabelLine rhs) => ReferenceEquals(lhs.Label, rhs.Label),
            (InlineAsmInstructionLine lhs, InlineAsmInstructionLine rhs) =>
                lhs.Condition == rhs.Condition
                && lhs.Mnemonic == rhs.Mnemonic
                && ReferenceEquals(lhs.Form, rhs.Form)
                && lhs.FlagEffect == rhs.FlagEffect
                && InlineAsmOperandsEqual(lhs.Operands, rhs.Operands),
            (InlineAsmDataLine lhs, InlineAsmDataLine rhs) =>
                lhs.Directive == rhs.Directive
                && InlineAsmDataValuesEqual(lhs.Values, rhs.Values),
            _ => Assert.UnreachableValue<bool>($"Unhandled inline-assembly line type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool InlineAsmOperandsEqual(IReadOnlyList<InlineAsmOperand> left, IReadOnlyList<InlineAsmOperand> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!InlineAsmOperandEqual(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool InlineAsmOperandEqual(InlineAsmOperand left, InlineAsmOperand right)
    {
        if (left.GetType() != right.GetType())
            return false;

        return (left, right) switch
        {
            (InlineAsmBindingRefOperand lhs, InlineAsmBindingRefOperand rhs) => ReferenceEquals(lhs.Slot, rhs.Slot),
            (InlineAsmImmediateOperand lhs, InlineAsmImmediateOperand rhs) => lhs.Value == rhs.Value,
            (InlineAsmCurrentAddressOperand lhs, InlineAsmCurrentAddressOperand rhs) => lhs.AddressingMode == rhs.AddressingMode,
            (InlineAsmLabelOperand lhs, InlineAsmLabelOperand rhs) =>
                ReferenceEquals(lhs.Label, rhs.Label)
                && lhs.AddressingMode == rhs.AddressingMode,
            (InlineAsmSpecialRegisterOperand lhs, InlineAsmSpecialRegisterOperand rhs) => lhs.Register == rhs.Register,
            _ => Assert.UnreachableValue<bool>($"Unhandled inline-assembly operand type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool InlineAsmDataValuesEqual(IReadOnlyList<InlineAsmDataValue> left, IReadOnlyList<InlineAsmDataValue> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!InlineAsmDataValueEqual(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static bool InlineAsmDataValueEqual(InlineAsmDataValue left, InlineAsmDataValue right)
    {
        if (left.GetType() != right.GetType()
            || left.AddressingMode != right.AddressingMode)
        {
            return false;
        }

        return (left, right) switch
        {
            (InlineAsmDataBindingValue lhs, InlineAsmDataBindingValue rhs) => ReferenceEquals(lhs.Slot, rhs.Slot),
            (InlineAsmDataIntegerValue lhs, InlineAsmDataIntegerValue rhs) => lhs.Value == rhs.Value,
            (InlineAsmDataCurrentAddressValue, InlineAsmDataCurrentAddressValue) => true,
            (InlineAsmDataLabelValue lhs, InlineAsmDataLabelValue rhs) => ReferenceEquals(lhs.Label, rhs.Label),
            (InlineAsmDataSpecialRegisterValue lhs, InlineAsmDataSpecialRegisterValue rhs) => lhs.Register == rhs.Register,
            (InlineAsmDataRawSymbolValue lhs, InlineAsmDataRawSymbolValue rhs) => lhs.Name == rhs.Name,
            _ => Assert.UnreachableValue<bool>($"Unhandled inline-assembly data value type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private sealed class FunctionContext
    {
        private readonly Dictionary<MirBlockRef, MirBlockRef> leftToRightBlocks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MirBlockRef, MirBlockRef> rightToLeftBlocks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MirValueId, MirValueId> leftToRightValues = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MirValueId, MirValueId> rightToLeftValues = new(ReferenceEqualityComparer.Instance);

        public bool BindBlock(MirBlockRef left, MirBlockRef right)
        {
            if (leftToRightBlocks.TryGetValue(left, out MirBlockRef? existingLeft))
                return ReferenceEquals(existingLeft, right);

            if (rightToLeftBlocks.TryGetValue(right, out MirBlockRef? existingRight))
                return ReferenceEquals(existingRight, left);

            leftToRightBlocks.Add(left, right);
            rightToLeftBlocks.Add(right, left);
            return true;
        }

        public bool BlockEquals(MirBlockRef left, MirBlockRef right)
        {
            return leftToRightBlocks.TryGetValue(left, out MirBlockRef? mapped)
                && ReferenceEquals(mapped, right);
        }

        public bool BindValue(MirValueId left, MirValueId right)
        {
            if (left.Type != right.Type)
                return false;

            if (leftToRightValues.TryGetValue(left, out MirValueId? existingLeft))
                return ReferenceEquals(existingLeft, right);

            if (rightToLeftValues.TryGetValue(right, out MirValueId? existingRight))
                return ReferenceEquals(existingRight, left);

            leftToRightValues.Add(left, right);
            rightToLeftValues.Add(right, left);
            return true;
        }

        public bool ValueEquals(MirValueId left, MirValueId right)
        {
            return leftToRightValues.TryGetValue(left, out MirValueId? mapped)
                ? ReferenceEquals(mapped, right)
                : BindValue(left, right);
        }

        public bool TryGetMappedValue(MirValueId left, out MirValueId right)
        {
            if (leftToRightValues.TryGetValue(left, out MirValueId? mapped))
            {
                right = mapped;
                return true;
            }

            right = null!;
            return false;
        }
    }
}
