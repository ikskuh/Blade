using System;
using System.Collections.Generic;
using System.Globalization;
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
            if (callee.Kind == FunctionKind.Inline)
                return true;

            if (callee.Kind == FunctionKind.Noinline)
                return false;

            return enableSingleCallsiteInlining
                && callCounts.TryGetValue(callee.Name, out int count)
                && count == 1;
        });
    }

    public static MirModule InlineCostBased(MirModule module, int inlineCostThreshold)
    {
        Requires.NotNull(module);

        return Inline(module, (_, _, callee, _) =>
        {
            if (callee.Kind is FunctionKind.Noinline
                or FunctionKind.Rec
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
        while (true)
        {
            Dictionary<string, MirFunction> functionsByName = new();
            foreach (MirFunction function in current.Functions)
                functionsByName[function.Name] = function;

            Dictionary<string, int> callCounts = CountCallSites(current);
            bool changed = false;
            List<MirFunction> rewrittenFunctions = new(current.Functions.Count);
            foreach (MirFunction function in current.Functions)
            {
                MutableFunction mutable = new(function);
                bool functionChanged = mutable.TryInline(functionsByName, callCounts, shouldInline);
                changed |= functionChanged;
                rewrittenFunctions.Add(mutable.ToImmutable());
            }

            current = new MirModule(current.StoragePlaces, rewrittenFunctions);
            if (!changed)
                break;
        }

        return current;
    }

    private static Dictionary<string, int> CountCallSites(MirModule module)
    {
        Dictionary<string, int> counts = new();
        foreach (MirFunction function in module.Functions)
        {
            foreach (MirBlock block in function.Blocks)
            {
                foreach (MirInstruction instruction in block.Instructions)
                {
                    if (instruction is not MirCallInstruction call)
                        continue;

                    if (!counts.TryAdd(call.FunctionName, 1))
                        counts[call.FunctionName]++;
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
        IReadOnlyDictionary<string, int> callCounts);

    private sealed class MutableFunction
    {
        private readonly MirFunction _source;
        private readonly List<MutableBlock> _blocks;
        private int _nextValueId;
        private int _inlineOrdinal;

        public MutableFunction(MirFunction source)
        {
            _source = source;
            _blocks = [];
            foreach (MirBlock block in source.Blocks)
                _blocks.Add(new MutableBlock(block.Label, block.Parameters, block.Instructions, block.Terminator));
            _nextValueId = ComputeNextValueId(source);
            _inlineOrdinal = ComputeNextInlineOrdinal(source);
        }

        public bool TryInline(
            IReadOnlyDictionary<string, MirFunction> functionsByName,
            IReadOnlyDictionary<string, int> callCounts,
            InlinePredicate shouldInline)
        {
            for (int blockIndex = 0; blockIndex < _blocks.Count; blockIndex++)
            {
                MutableBlock block = _blocks[blockIndex];
                for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
                {
                    if (block.Instructions[instructionIndex] is not MirCallInstruction call)
                        continue;

                    if (!functionsByName.TryGetValue(call.FunctionName, out MirFunction? callee))
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

            int inlineOrdinal = _inlineOrdinal++;
            MutableBlock afterBlock = CreateAfterBlock(call, suffix, callerBlock.Terminator, inlineOrdinal);

            Dictionary<string, string> labelMap = new();
            foreach (MirBlock calleeBlock in callee.Blocks)
                labelMap[calleeBlock.Label] = $"inl_{inlineOrdinal}_{calleeBlock.Label}";

            List<MutableBlock> clonedBlocks = CloneCalleeBlocks(call, callee, labelMap, afterBlock.Label);
            List<MirValueId> entryArguments = BuildEntryArguments(call, callee.Blocks[0], callerBlock, call.Span);

            callerBlock.Instructions = prefix;
            callerBlock.Terminator = new MirGotoTerminator(
                labelMap[callee.Blocks[0].Label],
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
            IReadOnlyDictionary<string, string> labelMap,
            string returnTargetLabel)
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
                cloned.Add(new MutableBlock(labelMap[calleeBlock.Label], parameters, instructions, terminator));
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
                MirConstantInstruction constant => new MirConstantInstruction(newResult!.Value, constant.ResultType!, constant.Value, constant.Span),
                MirLoadSymbolInstruction load => new MirLoadSymbolInstruction(newResult!.Value, load.ResultType!, load.SymbolName, load.Span),
                MirLoadPlaceInstruction loadPlace => new MirLoadPlaceInstruction(newResult!.Value, loadPlace.ResultType!, loadPlace.Place, loadPlace.Span),
                MirCopyInstruction copy => new MirCopyInstruction(newResult!.Value, copy.ResultType!, copy.Source, copy.Span),
                MirUnaryInstruction unary => new MirUnaryInstruction(newResult!.Value, unary.ResultType!, unary.Operator, unary.Operand, unary.Span),
                MirBinaryInstruction binary => new MirBinaryInstruction(newResult!.Value, binary.ResultType!, binary.Operator, binary.Left, binary.Right, binary.Span),
                MirConvertInstruction convert => new MirConvertInstruction(newResult!.Value, convert.ResultType!, convert.Operand, convert.Span),
                MirRangeInstruction range => new MirRangeInstruction(newResult!.Value, range.ResultType!, range.Start, range.End, range.Span),
                MirStructLiteralInstruction structLiteral => new MirStructLiteralInstruction(newResult!.Value, (StructTypeSymbol)structLiteral.ResultType!, structLiteral.Fields, structLiteral.Span),
                MirLoadMemberInstruction loadMember => new MirLoadMemberInstruction(newResult!.Value, loadMember.ResultType!, loadMember.Receiver, loadMember.Member, loadMember.Span),
                MirLoadIndexInstruction loadIndex => new MirLoadIndexInstruction(newResult!.Value, loadIndex.ResultType!, loadIndex.Indexed, loadIndex.Index, loadIndex.StorageClass, loadIndex.HasSideEffects, loadIndex.Span),
                MirLoadDerefInstruction loadDeref => new MirLoadDerefInstruction(newResult!.Value, loadDeref.ResultType!, loadDeref.Address, loadDeref.StorageClass, loadDeref.HasSideEffects, loadDeref.Span),
                MirBitfieldExtractInstruction bitfieldExtract => new MirBitfieldExtractInstruction(newResult!.Value, bitfieldExtract.ResultType!, bitfieldExtract.Receiver, bitfieldExtract.Member, bitfieldExtract.Span),
                MirBitfieldInsertInstruction bitfieldInsert => new MirBitfieldInsertInstruction(newResult!.Value, bitfieldInsert.ResultType!, bitfieldInsert.Receiver, bitfieldInsert.Value, bitfieldInsert.Member, bitfieldInsert.Span),
                MirInsertMemberInstruction insertMember => new MirInsertMemberInstruction(newResult!.Value, insertMember.ResultType!, insertMember.Receiver, insertMember.Value, insertMember.Member, insertMember.Span),
                MirSelectInstruction select => new MirSelectInstruction(newResult!.Value, select.ResultType!, select.Condition, select.WhenTrue, select.WhenFalse, select.Span),
                MirCallInstruction call => CloneMirCallInstruction(call, newResult, valueMap),
                MirIntrinsicCallInstruction intrinsic => new MirIntrinsicCallInstruction(newResult, intrinsic.ResultType, intrinsic.IntrinsicName, intrinsic.Arguments, intrinsic.Span),
                MirStoreIndexInstruction storeIndex => new MirStoreIndexInstruction(storeIndex.ResultType, storeIndex.Indexed, storeIndex.Index, storeIndex.Value, storeIndex.StorageClass, storeIndex.Span),
                MirStoreDerefInstruction storeDeref => new MirStoreDerefInstruction(storeDeref.ResultType, storeDeref.Address, storeDeref.Value, storeDeref.StorageClass, storeDeref.Span),
                MirStorePlaceInstruction storePlace => new MirStorePlaceInstruction(storePlace.Place, storePlace.Value, storePlace.Span),
                MirUpdatePlaceInstruction updatePlace => new MirUpdatePlaceInstruction(updatePlace.Place, updatePlace.OperatorKind, updatePlace.Value, updatePlace.Span),
                MirInlineAsmInstruction inlineAsm => new MirInlineAsmInstruction(
                    inlineAsm.Volatility,
                    inlineAsm.Body,
                    inlineAsm.FlagOutput,
                    inlineAsm.ParsedLines,
                    inlineAsm.Bindings,
                    inlineAsm.Span,
                    newResult,
                    inlineAsm.ResultType),
                MirYieldInstruction yield => new MirYieldInstruction(yield.Span),
                MirYieldToInstruction yieldTo => new MirYieldToInstruction(yieldTo.TargetFunctionName, yieldTo.Arguments, yieldTo.Span),
                MirRepSetupInstruction repSetup => new MirRepSetupInstruction(repSetup.Count, repSetup.Span),
                MirRepIterInstruction repIter => new MirRepIterInstruction(repIter.Count, repIter.Span),
                MirRepForSetupInstruction repForSetup => new MirRepForSetupInstruction(repForSetup.Start, repForSetup.End, repForSetup.Span),
                MirRepForIterInstruction repForIter => new MirRepForIterInstruction(repForIter.Start, repForIter.End, repForIter.Span),
                MirNoIrqBeginInstruction begin => new MirNoIrqBeginInstruction(begin.Span),
                MirNoIrqEndInstruction end => new MirNoIrqEndInstruction(end.Span),
                MirErrorStatementInstruction errorStatement => new MirErrorStatementInstruction(errorStatement.Span),
                MirErrorStoreInstruction errorStore => new MirErrorStoreInstruction(errorStore.Value, errorStore.Span),
                _ => rewritten,
            };
        }

        private MirTerminator CloneTerminator(
            MirTerminator terminator,
            IReadOnlyDictionary<MirValueId, MirValueId> valueMap,
            IReadOnlyDictionary<string, string> labelMap,
            MirCallInstruction call,
            string returnTargetLabel,
            ICollection<MirInstruction> _)
        {
            MirTerminator rewritten = terminator.RewriteUses(valueMap);
            switch (rewritten)
            {
                case MirGotoTerminator mirGoto:
                    return new MirGotoTerminator(
                        labelMap[mirGoto.TargetLabel],
                        mirGoto.Arguments,
                        mirGoto.Span);

                case MirBranchTerminator branch:
                    return new MirBranchTerminator(
                        branch.Condition,
                        labelMap[branch.TrueLabel],
                        labelMap[branch.FalseLabel],
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
            MirTerminator terminator,
            int inlineOrdinal)
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

            string label = $"inl_after_{inlineOrdinal}";
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

        private MirValueId NextValue() => new(_nextValueId++);

        private static int ComputeNextInlineOrdinal(MirFunction function)
        {
            int nextOrdinal = 0;
            foreach (MirBlock block in function.Blocks)
            {
                if (TryGetInlineOrdinal(block.Label, out int ordinal))
                    nextOrdinal = Math.Max(nextOrdinal, ordinal + 1);
            }

            return nextOrdinal;
        }

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

            return new MirCallInstruction(newResult, call.ResultType, call.FunctionName, call.Arguments, call.Span, clonedExtra);
        }

        private static bool TryGetInlineOrdinal(string label, out int ordinal)
        {
            ordinal = default;
            const string inlinePrefix = "inl_";
            const string afterPrefix = "inl_after_";

            ReadOnlySpan<char> source = label.AsSpan();
            if (source.StartsWith(afterPrefix, StringComparison.Ordinal))
                return TryParseInlineOrdinal(source[afterPrefix.Length..], out ordinal);

            if (!source.StartsWith(inlinePrefix, StringComparison.Ordinal))
                return false;

            return TryParseInlineOrdinal(source[inlinePrefix.Length..], out ordinal);
        }

        private static bool TryParseInlineOrdinal(ReadOnlySpan<char> source, out int ordinal)
        {
            ordinal = default;
            int separator = source.IndexOf('_');
            ReadOnlySpan<char> number = separator >= 0 ? source[..separator] : source;
            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);
        }

        private static int ComputeNextValueId(MirFunction function)
        {
            int maxId = -1;
            foreach (MirBlock block in function.Blocks)
            {
                foreach (MirBlockParameter parameter in block.Parameters)
                    maxId = parameter.Value.Id > maxId ? parameter.Value.Id : maxId;
                foreach (MirInstruction instruction in block.Instructions)
                {
                    if (instruction.Result is MirValueId result && result.Id > maxId)
                        maxId = result.Id;
                    if (instruction is MirCallInstruction callInstr)
                    {
                        foreach ((MirValueId extraVal, _) in callInstr.ExtraResults)
                            maxId = extraVal.Id > maxId ? extraVal.Id : maxId;
                    }
                    foreach (MirValueId use in instruction.Uses)
                        maxId = use.Id > maxId ? use.Id : maxId;
                }

                foreach (MirValueId use in block.Terminator.Uses)
                    maxId = use.Id > maxId ? use.Id : maxId;
            }

            return maxId + 1;
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

            return new MirFunction(_source.Name, _source.IsEntryPoint, _source.Kind, _source.ReturnTypes, blocks, _source.ReturnSlots);
        }
    }

    private sealed class MutableBlock
    {
        public MutableBlock(
            string label,
            IReadOnlyList<MirBlockParameter> parameters,
            IReadOnlyList<MirInstruction> instructions,
            MirTerminator terminator)
        {
            Label = label;
            Parameters = new List<MirBlockParameter>(parameters);
            Instructions = new List<MirInstruction>(instructions);
            Terminator = terminator;
        }

        public string Label { get; }
        public List<MirBlockParameter> Parameters { get; }
        public List<MirInstruction> Instructions { get; set; }
        public MirTerminator Terminator { get; set; }
    }
}
