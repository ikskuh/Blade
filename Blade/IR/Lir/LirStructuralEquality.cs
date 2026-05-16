using System;
using System.Collections.Generic;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR.Lir;

/// <summary>
/// Compares LIR graphs structurally while allowing alpha-equivalent block/value identities.
/// </summary>
internal static class LirStructuralEquality
{
    /// <summary>
    /// Determines whether two LIR modules are structurally equivalent.
    /// </summary>
    internal static bool StructurallyEqualTo(this LirModule left, LirModule right)
    {
        Requires.NotNull(left);
        Requires.NotNull(right);

        if (ReferenceEquals(left, right))
            return true;

        if (!ReferenceEquals(left.SourceModule, right.SourceModule))
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

    private static bool FunctionsEqual(LirFunction left, LirFunction right)
    {
        if (!ReferenceEquals(left.SourceFunction, right.SourceFunction)
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

    private static bool BlocksEqual(LirBlock left, LirBlock right, FunctionContext context)
    {
        if (!context.BlockEquals(left.Ref, right.Ref)
            || left.Parameters.Count != right.Parameters.Count
            || left.Instructions.Count != right.Instructions.Count)
        {
            return false;
        }

        for (int parameterIndex = 0; parameterIndex < left.Parameters.Count; parameterIndex++)
        {
            LirBlockParameter leftParameter = left.Parameters[parameterIndex];
            LirBlockParameter rightParameter = right.Parameters[parameterIndex];
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

    private static bool InstructionsEqual(LirInstruction left, LirInstruction right, FunctionContext context)
    {
        if (left.GetType() != right.GetType()
            || !TypeEquals(left.ResultType, right.ResultType)
            || left.HasSideEffects != right.HasSideEffects
            || left.Predicate != right.Predicate
            || left.WritesC != right.WritesC
            || left.WritesZ != right.WritesZ)
        {
            return false;
        }

        if ((left.Destination is null) != (right.Destination is null))
            return false;

        if (left.Destination is VirtualLirValue leftDestination
            && right.Destination is VirtualLirValue rightDestination
            && !context.BindValue(leftDestination, rightDestination))
        {
            return false;
        }

        return (left, right) switch
        {
            (LirOpInstruction lhs, LirOpInstruction rhs) =>
                OperationsEqual(lhs.Operation, rhs.Operation)
                && OperandsEqual(lhs.Operands, rhs.Operands, context),
            (LirInlineAsmInstruction lhs, LirInlineAsmInstruction rhs) =>
                lhs.Volatility == rhs.Volatility
                && lhs.FlagOutput == rhs.FlagOutput
                && InlineAsmLinesEqual(lhs.ParsedLines, rhs.ParsedLines)
                && LirInlineAsmBindingsEqual(lhs.Bindings, rhs.Bindings, context),
            _ => Assert.UnreachableValue<bool>($"Unhandled LIR instruction type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool TerminatorsEqual(LirTerminator left, LirTerminator right, FunctionContext context)
    {
        return (left, right) switch
        {
            (LirGotoTerminator lhs, LirGotoTerminator rhs) =>
                context.BlockEquals(lhs.Target, rhs.Target)
                && OperandsEqual(lhs.Arguments, rhs.Arguments, context),
            (LirBranchTerminator lhs, LirBranchTerminator rhs) =>
                lhs.ConditionFlag == rhs.ConditionFlag
                && context.BlockEquals(lhs.TrueTarget, rhs.TrueTarget)
                && context.BlockEquals(lhs.FalseTarget, rhs.FalseTarget)
                && OperandEquals(lhs.Condition, rhs.Condition, context)
                && OperandsEqual(lhs.TrueArguments, rhs.TrueArguments, context)
                && OperandsEqual(lhs.FalseArguments, rhs.FalseArguments, context),
            (LirReturnTerminator lhs, LirReturnTerminator rhs) => OperandsEqual(lhs.Values, rhs.Values, context),
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

    private static bool OperandsEqual(IReadOnlyList<LirOperand> left, IReadOnlyList<LirOperand> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!OperandEquals(left[i], right[i], context))
                return false;
        }

        return true;
    }

    private static bool OperandEquals(LirOperand left, LirOperand right, FunctionContext context)
    {
        if (left.GetType() != right.GetType())
            return false;

        return (left, right) switch
        {
            (LirValueOperand lhs, LirValueOperand rhs) => context.ValueEquals(lhs.Value, rhs.Value),
            (LirRegisterOperand lhs, LirRegisterOperand rhs) => context.ValueEquals(lhs.Register, rhs.Register),
            (LirFlagOperand lhs, LirFlagOperand rhs) => context.ValueEquals(lhs.Flag, rhs.Flag),
            (LirImmediateOperand lhs, LirImmediateOperand rhs) => Equals(lhs.Value, rhs.Value),
            (LirPlaceOperand lhs, LirPlaceOperand rhs) => ReferenceEquals(lhs.Place, rhs.Place),
            _ => Assert.UnreachableValue<bool>($"Unhandled LIR operand type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool OperationsEqual(LirOperation left, LirOperation right)
    {
        if (left.GetType() != right.GetType())
            return false;

        return (left, right) switch
        {
            (LirConstOperation, LirConstOperation) => true,
            (LirMovOperation, LirMovOperation) => true,
            (LirLoadPlaceOperation, LirLoadPlaceOperation) => true,
            (LirUnaryOperation lhs, LirUnaryOperation rhs) => lhs.OperatorKind == rhs.OperatorKind,
            (LirBinaryOperation lhs, LirBinaryOperation rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && lhs.ComparisonLoweringKind == rhs.ComparisonLoweringKind,
            (LirPointerOffsetOperation lhs, LirPointerOffsetOperation rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && lhs.Stride == rhs.Stride,
            (LirPointerDifferenceOperation lhs, LirPointerDifferenceOperation rhs) => lhs.Stride == rhs.Stride,
            (LirConvertOperation, LirConvertOperation) => true,
            (LirAggregateFlagTransportOperation, LirAggregateFlagTransportOperation) => true,
            (LirStructLiteralOperation lhs, LirStructLiteralOperation rhs) => MembersEqual(lhs.Members, rhs.Members),
            (LirLoadMemberOperation lhs, LirLoadMemberOperation rhs) => ReferenceEquals(lhs.Member, rhs.Member),
            (LirLoadIndexOperation lhs, LirLoadIndexOperation rhs) =>
                TypeEquals(lhs.IndexedType, rhs.IndexedType)
                && lhs.StorageClass == rhs.StorageClass,
            (LirLoadDerefOperation lhs, LirLoadDerefOperation rhs) =>
                TypeEquals(lhs.PointerType, rhs.PointerType)
                && lhs.StorageClass == rhs.StorageClass,
            (LirBitfieldExtractOperation lhs, LirBitfieldExtractOperation rhs) => ReferenceEquals(lhs.Member, rhs.Member),
            (LirBitfieldInsertOperation lhs, LirBitfieldInsertOperation rhs) => ReferenceEquals(lhs.Member, rhs.Member),
            (LirInsertMemberOperation lhs, LirInsertMemberOperation rhs) => ReferenceEquals(lhs.Member, rhs.Member),
            (LirCallOperation lhs, LirCallOperation rhs) => ReferenceEquals(lhs.TargetFunction, rhs.TargetFunction),
            (LirSpawnOperation lhs, LirSpawnOperation rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && ReferenceEquals(lhs.TargetTask, rhs.TargetTask)
                && lhs.RequestedResultCount == rhs.RequestedResultCount,
            (LirCallExtractFlagOperation lhs, LirCallExtractFlagOperation rhs) => lhs.Flag == rhs.Flag,
            (LirIntrinsicOperation lhs, LirIntrinsicOperation rhs) => lhs.Mnemonic == rhs.Mnemonic,
            (LirStoreIndexOperation lhs, LirStoreIndexOperation rhs) =>
                TypeEquals(lhs.IndexedType, rhs.IndexedType)
                && lhs.StorageClass == rhs.StorageClass,
            (LirStoreDerefOperation lhs, LirStoreDerefOperation rhs) =>
                TypeEquals(lhs.PointerType, rhs.PointerType)
                && lhs.StorageClass == rhs.StorageClass,
            (LirStorePlaceOperation, LirStorePlaceOperation) => true,
            (LirUpdatePlaceOperation lhs, LirUpdatePlaceOperation rhs) =>
                lhs.OperatorKind == rhs.OperatorKind
                && lhs.PointerArithmeticStride == rhs.PointerArithmeticStride,
            (LirYieldOperation, LirYieldOperation) => true,
            (LirYieldToOperation lhs, LirYieldToOperation rhs) => ReferenceEquals(lhs.TargetFunction, rhs.TargetFunction),
            (LirRepSetupOperation, LirRepSetupOperation) => true,
            (LirRepIterOperation, LirRepIterOperation) => true,
            (LirRepForSetupOperation, LirRepForSetupOperation) => true,
            (LirRepForIterOperation lhs, LirRepForIterOperation rhs) => lhs.IndexCarrierOrdinal == rhs.IndexCarrierOrdinal,
            (LirNoIrqBeginOperation, LirNoIrqBeginOperation) => true,
            (LirNoIrqEndOperation, LirNoIrqEndOperation) => true,
            _ => Assert.UnreachableValue<bool>($"Unhandled LIR operation type '{left.GetType().Name}'."), // pragma: force-coverage
        };
    }

    private static bool MembersEqual(IReadOnlyList<AggregateMemberSymbol> left, IReadOnlyList<AggregateMemberSymbol> right)
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

    private static bool LirInlineAsmBindingsEqual(IReadOnlyList<LirInlineAsmBinding> left, IReadOnlyList<LirInlineAsmBinding> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            LirInlineAsmBinding lhs = left[i];
            LirInlineAsmBinding rhs = right[i];
            if (!ReferenceEquals(lhs.Slot, rhs.Slot)
                || !ReferenceEquals(lhs.Symbol, rhs.Symbol)
                || lhs.Access != rhs.Access
                || !OperandEquals(lhs.Operand, rhs.Operand, context))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FlagMapsEqual(IReadOnlyDictionary<VirtualLirFlag, MirFlag> left, IReadOnlyDictionary<VirtualLirFlag, MirFlag> right, FunctionContext context)
    {
        if (left.Count != right.Count)
            return false;

        Dictionary<VirtualLirFlag, MirFlag> remaining = new(right, ReferenceEqualityComparer.Instance);
        foreach ((VirtualLirFlag leftValue, MirFlag leftFlag) in left)
        {
            if (!context.TryGetMappedValue(leftValue, out VirtualLirValue mappedValue)
                || mappedValue is not VirtualLirFlag rightValue
                || !remaining.TryGetValue(rightValue, out MirFlag rightFlag)
                || leftFlag != rightFlag)
            {
                return false;
            }

            remaining.Remove(rightValue);
        }

        return remaining.Count == 0;
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
        private readonly Dictionary<LirBlockRef, LirBlockRef> leftToRightBlocks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<LirBlockRef, LirBlockRef> rightToLeftBlocks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VirtualLirValue, VirtualLirValue> leftToRightValues = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VirtualLirValue, VirtualLirValue> rightToLeftValues = new(ReferenceEqualityComparer.Instance);

        public bool BindBlock(LirBlockRef left, LirBlockRef right)
        {
            if (leftToRightBlocks.TryGetValue(left, out LirBlockRef? existingLeft))
                return ReferenceEquals(existingLeft, right);

            if (rightToLeftBlocks.TryGetValue(right, out LirBlockRef? existingRight))
                return ReferenceEquals(existingRight, left);

            leftToRightBlocks.Add(left, right);
            rightToLeftBlocks.Add(right, left);
            return true;
        }

        public bool BlockEquals(LirBlockRef left, LirBlockRef right)
        {
            return leftToRightBlocks.TryGetValue(left, out LirBlockRef? mapped)
                && ReferenceEquals(mapped, right);
        }

        public bool BindValue(VirtualLirValue left, VirtualLirValue right)
        {
            if (left.Type != right.Type)
                return false;

            if (leftToRightValues.TryGetValue(left, out VirtualLirValue? existingLeft))
                return ReferenceEquals(existingLeft, right);

            if (rightToLeftValues.TryGetValue(right, out VirtualLirValue? existingRight))
                return ReferenceEquals(existingRight, left);

            leftToRightValues.Add(left, right);
            rightToLeftValues.Add(right, left);
            return true;
        }

        public bool ValueEquals(VirtualLirValue left, VirtualLirValue right)
        {
            return leftToRightValues.TryGetValue(left, out VirtualLirValue? mapped)
                ? ReferenceEquals(mapped, right)
                : BindValue(left, right);
        }

        public bool TryGetMappedValue(VirtualLirValue left, out VirtualLirValue right)
        {
            if (leftToRightValues.TryGetValue(left, out VirtualLirValue? mapped))
            {
                right = mapped;
                return true;
            }

            right = null!;
            return false;
        }
    }
}
