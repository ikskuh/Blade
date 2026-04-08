using System;
using System.Collections.Generic;
using Blade;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR.Mir;

public static class MirInliner
{
    public static MirModule InlineMandatoryAndSingleCallsite(MirModule module, bool enableSingleCallsiteInlining)
    {
        Requires.NotNull(module);

        return Inline(module, (_, _, callee, callCounts) =>
        {
            if (callee.InliningPolicy == FunctionInliningPolicy.ForceInline)
                return true;

            if (callee.InliningPolicy == FunctionInliningPolicy.NeverInline)
                return false;

            return enableSingleCallsiteInlining
                && callCounts.TryGetValue(callee.Symbol, out int count)
                && count == 1;
        });
    }

    public static MirModule InlineCostBased(MirModule module, int inlineCostThreshold)
    {
        Requires.NotNull(module);

        return Inline(module, (_, _, callee, _) =>
        {
            if (callee.InliningPolicy == FunctionInliningPolicy.NeverInline
                || callee.Kind is FunctionKind.Rec
                or FunctionKind.Coro
                or FunctionKind.Int1
                or FunctionKind.Int2
                or FunctionKind.Int3)
            {
                return false;
            }

            int cost = EstimateInlineCost(callee);
            return cost <= inlineCostThreshold;
        });
    }

    private static MirModule Inline(
        MirModule module,
        InlinePredicate shouldInline)
    {
        MirModule current = module;
        int nextInlineOrdinal = 0;
        while (true)
        {
            Dictionary<FunctionSymbol, MirFunction> functionsByName = [];
            foreach (MirFunction function in current.Functions)
                functionsByName[function.Symbol] = function;

            Dictionary<FunctionSymbol, int> callCounts = CountCallSites(current);
            bool changed = false;
            List<MirFunction> rewrittenFunctions = new(current.Functions.Count);
            foreach (MirFunction function in current.Functions)
            {
                MutableFunction mutable = new(function, () => nextInlineOrdinal++);
                bool functionChanged = mutable.TryInline(functionsByName, callCounts, shouldInline);
                changed |= functionChanged;
                rewrittenFunctions.Add(mutable.ToImmutable());
            }

            current = new MirModule(current.StoragePlaces, current.StorageDefinitions, rewrittenFunctions);
            if (!changed)
                break;
        }

        return current;
    }

    private static Dictionary<FunctionSymbol, int> CountCallSites(MirModule module)
    {
        Dictionary<FunctionSymbol, int> counts = [];
        foreach (MirFunction function in module.Functions)
        {
            foreach (MirBlock block in function.Blocks)
            {
                foreach (MirInstruction instruction in block.Instructions)
                {
                    if (instruction is not MirCallInstruction call)
                        continue;

                    if (!counts.TryAdd(call.Function, 1))
                        counts[call.Function]++;
                }
            }
        }

        return counts;
    }

    private static int EstimateInlineCost(MirFunction function)
    {
        int cost = 0;
        foreach (MirBlock block in function.Blocks)
        {
            cost += block.Instructions.Count;
            cost += 1;
        }

        return cost;
    }

    private delegate bool InlinePredicate(
        MirFunction caller,
        MirCallInstruction call,
        MirFunction callee,
        IReadOnlyDictionary<FunctionSymbol, int> callCounts);

    private sealed class MutableFunction
    {
        private readonly MirFunction _source;
        private readonly List<MutableBlock> _blocks;
        private readonly Func<int> _nextInlineOrdinal;

        public MutableFunction(MirFunction source, Func<int> nextInlineOrdinal)
        {
            _source = source;
            _nextInlineOrdinal = Requires.NotNull(nextInlineOrdinal);
            _blocks = [];
            foreach (MirBlock block in source.Blocks)
                _blocks.Add(new MutableBlock(block.Ref, block.Parameters, block.Instructions, block.Terminator));
        }

        public bool TryInline(
            IReadOnlyDictionary<FunctionSymbol, MirFunction> functionsByName,
            IReadOnlyDictionary<FunctionSymbol, int> callCounts,
            InlinePredicate shouldInline)
        {
            for (int blockIndex = 0; blockIndex < _blocks.Count; blockIndex++)
            {
                MutableBlock block = _blocks[blockIndex];
                for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
                {
                    if (block.Instructions[instructionIndex] is not MirCallInstruction call)
                        continue;

                    if (!functionsByName.TryGetValue(call.Function, out MirFunction? callee))
                        continue;

                    MirFunction caller = ToImmutable();
                    if (!shouldInline(caller, call, callee, callCounts))
                        continue;

                    InlineCall(blockIndex, instructionIndex, call, callee);
                    return true;
                }
            }

            return false;
        }

        private void InlineCall(int blockIndex, int instructionIndex, MirCallInstruction call, MirFunction callee)
        {
            MutableBlock callerBlock = _blocks[blockIndex];
            List<MirInstruction> prefix = [];
            for (int i = 0; i < instructionIndex; i++)
                prefix.Add(callerBlock.Instructions[i]);
            List<MirInstruction> suffix = [];
            for (int i = instructionIndex + 1; i < callerBlock.Instructions.Count; i++)
                suffix.Add(callerBlock.Instructions[i]);

            int inlineOrdinal = _nextInlineOrdinal();
            MutableBlock afterBlock = CreateAfterBlock(call, suffix, callerBlock.Terminator);

            Dictionary<MirBlockRef, MirBlockRef> labelMap = [];
            foreach (MirBlock calleeBlock in callee.Blocks)
                labelMap[calleeBlock.Ref] = new MirBlockRef();

            List<MutableBlock> clonedBlocks = CloneCalleeBlocks(call, callee, labelMap, afterBlock.Label);
            List<MirValueId> entryArguments = BuildEntryArguments(call, callee.Blocks[0], callerBlock, call.Span);

            callerBlock.Instructions = prefix;
            callerBlock.Terminator = new MirGotoTerminator(
                labelMap[callee.Blocks[0].Ref],
                entryArguments,
                call.Span);

            int insertIndex = blockIndex + 1;
            foreach (MutableBlock cloned in clonedBlocks)
                _blocks.Insert(insertIndex++, cloned);
            _blocks.Insert(insertIndex, afterBlock);
        }

        private List<MutableBlock> CloneCalleeBlocks(
            MirCallInstruction call,
            MirFunction callee,
            IReadOnlyDictionary<MirBlockRef, MirBlockRef> labelMap,
            MirBlockRef returnTargetLabel)
        {
            List<MutableBlock> cloned = [];
            Dictionary<MirValueId, MirValueId> valueMap = [];
            foreach (MirBlock calleeBlock in callee.Blocks)
            {
                List<MirBlockParameter> parameters = [];
                foreach (MirBlockParameter parameter in calleeBlock.Parameters)
                {
                    MirValueId newValue = NextValue();
                    valueMap[parameter.Value] = newValue;
                    parameters.Add(new MirBlockParameter(newValue, parameter.Name, parameter.Type));
                }

                List<MirInstruction> instructions = [];
                foreach (MirInstruction instruction in calleeBlock.Instructions)
                {
                    MirInstruction rewritten = instruction.RewriteUses(valueMap);
                    MirInstruction clonedInstruction = CloneInstruction(rewritten, valueMap);
                    instructions.Add(clonedInstruction);
                }

                MirTerminator terminator = CloneTerminator(calleeBlock.Terminator, valueMap, labelMap, call, returnTargetLabel, instructions);
                cloned.Add(new MutableBlock(labelMap[calleeBlock.Ref], parameters, instructions, terminator));
            }

            return cloned;
        }

        private MirInstruction CloneInstruction(
            MirInstruction rewritten,
            IDictionary<MirValueId, MirValueId> valueMap)
        {
            MirValueId? newResult = null;
            if (rewritten.Result is MirValueId oldResult)
            {
                MirValueId fresh = NextValue();
                valueMap[oldResult] = fresh;
                newResult = fresh;
            }

            return rewritten switch
            {
                MirConstantInstruction constant => new MirConstantInstruction(newResult!, constant.ResultType!, constant.Value, constant.Span),
                MirLoadPlaceInstruction loadPlace => new MirLoadPlaceInstruction(newResult!, loadPlace.ResultType!, loadPlace.Place, loadPlace.Span),
                MirCopyInstruction copy => new MirCopyInstruction(newResult!, copy.ResultType!, copy.Source, copy.Span),
                MirUnaryInstruction unary => new MirUnaryInstruction(newResult!, unary.ResultType!, unary.Operator, unary.Operand, unary.Span),
                MirBinaryInstruction binary => new MirBinaryInstruction(newResult!, binary.ResultType!, binary.Operator, binary.Left, binary.Right, binary.Span),
                MirPointerOffsetInstruction pointerOffset => new MirPointerOffsetInstruction(newResult!, pointerOffset.ResultType!, pointerOffset.OperatorKind, pointerOffset.BaseAddress, pointerOffset.Delta, pointerOffset.Stride, pointerOffset.Span),
                MirPointerDifferenceInstruction pointerDifference => new MirPointerDifferenceInstruction(newResult!, pointerDifference.ResultType!, pointerDifference.Left, pointerDifference.Right, pointerDifference.Stride, pointerDifference.Span),
                MirConvertInstruction convert => new MirConvertInstruction(newResult!, convert.ResultType!, convert.Operand, convert.Span),
                MirStructLiteralInstruction structLiteral => new MirStructLiteralInstruction(newResult!, (StructTypeSymbol)structLiteral.ResultType!, structLiteral.Fields, structLiteral.Span),
                MirLoadMemberInstruction loadMember => new MirLoadMemberInstruction(newResult!, loadMember.ResultType!, loadMember.Receiver, loadMember.Member, loadMember.Span),
                MirLoadIndexInstruction loadIndex => new MirLoadIndexInstruction(newResult!, loadIndex.ResultType!, loadIndex.IndexedType, loadIndex.Indexed, loadIndex.Index, loadIndex.StorageClass, loadIndex.HasSideEffects, loadIndex.Span),
                MirLoadDerefInstruction loadDeref => new MirLoadDerefInstruction(newResult!, loadDeref.ResultType!, loadDeref.PointerType, loadDeref.Address, loadDeref.StorageClass, loadDeref.HasSideEffects, loadDeref.Span),
                MirBitfieldExtractInstruction bitfieldExtract => new MirBitfieldExtractInstruction(newResult!, bitfieldExtract.ResultType!, bitfieldExtract.Receiver, bitfieldExtract.Member, bitfieldExtract.Span),
                MirBitfieldInsertInstruction bitfieldInsert => new MirBitfieldInsertInstruction(newResult!, bitfieldInsert.ResultType!, bitfieldInsert.Receiver, bitfieldInsert.Value, bitfieldInsert.Member, bitfieldInsert.Span),
                MirInsertMemberInstruction insertMember => new MirInsertMemberInstruction(newResult!, insertMember.ResultType!, insertMember.Receiver, insertMember.Value, insertMember.Member, insertMember.Span),
                MirCallInstruction call => CloneMirCallInstruction(call, newResult, valueMap),
                MirIntrinsicCallInstruction intrinsic => new MirIntrinsicCallInstruction(newResult, intrinsic.ResultType, intrinsic.Mnemonic, intrinsic.Arguments, intrinsic.Span),
                MirStoreIndexInstruction storeIndex => new MirStoreIndexInstruction(storeIndex.ResultType, storeIndex.IndexedType, storeIndex.Indexed, storeIndex.Index, storeIndex.Value, storeIndex.StorageClass, storeIndex.Span),
                MirStoreDerefInstruction storeDeref => new MirStoreDerefInstruction(storeDeref.ResultType, storeDeref.PointerType, storeDeref.Address, storeDeref.Value, storeDeref.StorageClass, storeDeref.Span),
                MirStorePlaceInstruction storePlace => new MirStorePlaceInstruction(storePlace.Place, storePlace.Value, storePlace.Span),
                MirUpdatePlaceInstruction updatePlace => new MirUpdatePlaceInstruction(updatePlace.Place, updatePlace.OperatorKind, updatePlace.Value, updatePlace.Span, updatePlace.PointerArithmeticStride),
                MirInlineAsmInstruction inlineAsm => new MirInlineAsmInstruction(
                    inlineAsm.Volatility,
                    inlineAsm.FlagOutput,
                    inlineAsm.ParsedLines,
                    inlineAsm.Bindings,
                    inlineAsm.Span,
                    newResult,
                    inlineAsm.ResultType),
                MirYieldInstruction yield => new MirYieldInstruction(yield.Span),
                MirYieldToInstruction yieldTo => new MirYieldToInstruction(yieldTo.TargetFunction, yieldTo.Arguments, yieldTo.Span),
                MirRepSetupInstruction repSetup => new MirRepSetupInstruction(repSetup.Count, repSetup.Span),
                MirRepIterInstruction repIter => new MirRepIterInstruction(repIter.Count, repIter.Span),
                MirRepForSetupInstruction repForSetup => new MirRepForSetupInstruction(repForSetup.Start, repForSetup.End, repForSetup.Span),
                MirRepForIterInstruction repForIter => new MirRepForIterInstruction(repForIter.Start, repForIter.End, repForIter.Span),
                MirNoIrqBeginInstruction begin => new MirNoIrqBeginInstruction(begin.Span),
                MirNoIrqEndInstruction end => new MirNoIrqEndInstruction(end.Span),
                _ => rewritten,
            };
        }

        private MirTerminator CloneTerminator(
            MirTerminator terminator,
            IReadOnlyDictionary<MirValueId, MirValueId> valueMap,
            IReadOnlyDictionary<MirBlockRef, MirBlockRef> labelMap,
            MirCallInstruction call,
            MirBlockRef returnTargetLabel,
            ICollection<MirInstruction> _)
        {
            MirTerminator rewritten = terminator.RewriteUses(valueMap);
            switch (rewritten)
            {
                case MirGotoTerminator mirGoto:
                    return new MirGotoTerminator(
                        labelMap[mirGoto.Target],
                        mirGoto.Arguments,
                        mirGoto.Span);

                case MirBranchTerminator branch:
                    return new MirBranchTerminator(
                        branch.Condition,
                        labelMap[branch.TrueTarget],
                        labelMap[branch.FalseTarget],
                        branch.TrueArguments,
                        branch.FalseArguments,
                        branch.Span,
                        branch.ConditionFlag);

                case MirReturnTerminator ret:
                {
                    List<MirValueId> gotoArgs = [];

                    if (call.Result is MirValueId)
                    {
                        Assert.Invariant(ret.Values.Count > 0, "Inlined function return must have a value when call has a result");
                        gotoArgs.Add(ret.Values[0]);
                    }

                    // Pass extra return values for multi-return
                    for (int i = 0; i < call.ExtraResults.Count; i++)
                    {
                        int retIndex = i + 1;
                        Assert.Invariant(retIndex < ret.Values.Count, "Inlined function return must have enough values for all extra results");
                        gotoArgs.Add(ret.Values[retIndex]);
                    }

                    return new MirGotoTerminator(returnTargetLabel, gotoArgs, ret.Span);
                }

                case MirUnreachableTerminator unreachable:
                    return new MirUnreachableTerminator(unreachable.Span);
            }

            return rewritten;
        }

        private MutableBlock CreateAfterBlock(
            MirCallInstruction call,
            IReadOnlyList<MirInstruction> suffix,
            MirTerminator terminator)
        {
            List<MirBlockParameter> parameters = [];
            if (call.Result is MirValueId callResult)
            {
                TypeSymbol type = call.ResultType ?? BuiltinTypes.Unknown;
                parameters.Add(new MirBlockParameter(callResult, "ret0", type));
            }

            for (int i = 0; i < call.ExtraResults.Count; i++)
            {
                (MirValueId value, TypeSymbol type) = call.ExtraResults[i];
                parameters.Add(new MirBlockParameter(value, $"ret{i + 1}", type));
            }

            MirBlockRef label = new();
            return new MutableBlock(label, parameters, suffix, terminator);
        }

        private List<MirValueId> BuildEntryArguments(
            MirCallInstruction call,
            MirBlock entryBlock,
            MutableBlock callerBlock,
            TextSpan span)
        {
            List<MirValueId> arguments = [];
            for (int i = 0; i < entryBlock.Parameters.Count; i++)
            {
                if (i < call.Arguments.Count)
                {
                    arguments.Add(call.Arguments[i]);
                }
                else
                {
                    MirValueId fallback = NextValue();
                    callerBlock.Instructions.Add(new MirConstantInstruction(fallback, entryBlock.Parameters[i].Type, null, span));
                    arguments.Add(fallback);
                }
            }

            return arguments;
        }

        private static MirValueId NextValue() => new();

        private MirCallInstruction CloneMirCallInstruction(
            MirCallInstruction call,
            MirValueId? newResult,
            IDictionary<MirValueId, MirValueId> valueMap)
        {
            List<(MirValueId, TypeSymbol)>? clonedExtra = null;
            if (call.ExtraResults.Count > 0)
            {
                clonedExtra = new(call.ExtraResults.Count);
                foreach ((MirValueId extraVal, TypeSymbol extraType) in call.ExtraResults)
                {
                    MirValueId newExtra = NextValue();
                    valueMap[extraVal] = newExtra;
                    clonedExtra.Add((newExtra, extraType));
                }
            }

            return new MirCallInstruction(newResult, call.ResultType, call.Function, call.Arguments, call.Span, clonedExtra);
        }

        public MirFunction ToImmutable()
        {
            List<MirBlock> blocks = [];
            foreach (MutableBlock block in _blocks)
            {
                blocks.Add(new MirBlock(
                    block.Label,
                    block.Parameters,
                    block.Instructions,
                    block.Terminator));
            }

            return new MirFunction(_source.Symbol, _source.IsEntryPoint, _source.ReturnTypes, blocks, _source.ReturnSlots);
        }
    }

    private sealed class MutableBlock
    {
        public MutableBlock(
            MirBlockRef label,
            IReadOnlyList<MirBlockParameter> parameters,
            IReadOnlyList<MirInstruction> instructions,
            MirTerminator terminator)
        {
            Label = label;
            Parameters = new List<MirBlockParameter>(parameters);
            Instructions = new List<MirInstruction>(instructions);
            Terminator = terminator;
        }

        public MirBlockRef Label { get; }
        public List<MirBlockParameter> Parameters { get; }
        public List<MirInstruction> Instructions { get; set; }
        public MirTerminator Terminator { get; set; }
    }
}
