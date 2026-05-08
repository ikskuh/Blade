using System.Collections.Generic;
using Blade;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR.Lir;

public static class LirLowerer
{
    public static LirModule Lower(MirModule module)
    {
        Requires.NotNull(module);

        List<LirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction mirFunction in module.Functions)
            functions.Add(LowerFunction(mirFunction));
        return new LirModule(module, module.StoragePlaces, module.StorageDefinitions, functions);
    }

    private static LirFunction LowerFunction(MirFunction mirFunction)
    {
        Dictionary<MirValueId, VirtualLirValue> values = [];
        Dictionary<MirBlockRef, LirBlockRef> blockRefs = [];

        VirtualLirValue GetValue(MirValueId value)
        {
            if (values.TryGetValue(value, out VirtualLirValue? existing) && existing is not null)
                return existing;

            VirtualLirValue fresh = value switch
            {
                MirVirtualFlag => new VirtualLirFlag(),
                MirVirtualRegister => new VirtualLirRegister(),
                _ => Assert.UnreachableValue<VirtualLirValue>($"Unexpected MIR value type '{value.GetType().Name}'."), // pragma: force-coverage
            };
            values[value] = fresh;
            return fresh;
        }

        LirVirtualRegister GetRegister(MirValueId value) => (LirVirtualRegister)GetValue(value);

        LirOperand GetValueOperand(MirValueId value)
        {
            return GetValue(value) switch
            {
                LirVirtualRegister register => new LirRegisterOperand(register),
                VirtualLirFlag flag => new LirFlagOperand(flag),
                _ => Assert.UnreachableValue<LirOperand>($"Unexpected LIR value type '{GetValue(value).GetType().Name}'."), // pragma: force-coverage
            };
        }

        LirBlockRef GetBlockRef(MirBlockRef blockRef)
        {
            if (blockRefs.TryGetValue(blockRef, out LirBlockRef? mapped) && mapped is not null)
                return mapped;

            LirBlockRef created = new();
            blockRefs[blockRef] = created;
            return created;
        }

        Dictionary<MirValueId, BladeValue> constValues = [];
        foreach (MirBlock mirBlock in mirFunction.Blocks)
        {
            foreach (MirInstruction instruction in mirBlock.Instructions)
            {
                if (instruction is MirConstantInstruction { Result: MirValueId constId, Value: BladeValue constVal })
                    constValues[constId] = constVal;
            }
        }

        Dictionary<VirtualLirFlag, MirFlag> flagValues = [];
        foreach ((MirValueId value, MirFlag flag) in mirFunction.FlagValues)
        {
            if (GetValue(value) is VirtualLirFlag lirFlag)
                flagValues[lirFlag] = flag;
        }

        List<LirBlock> blocks = new(mirFunction.Blocks.Count);
        foreach (MirBlock mirBlock in mirFunction.Blocks)
        {
            List<LirBlockParameter> parameters = [];
            foreach (MirBlockParameter parameter in mirBlock.Parameters)
            {
                VirtualLirValue loweredValue = GetValue(parameter.Value);
                parameters.Add(new LirBlockParameter(loweredValue, parameter.Name, parameter.Type));
            }

            List<LirInstruction> instructions = [];
            foreach (MirInstruction instruction in mirBlock.Instructions)
            {
                instructions.Add(LowerInstruction(instruction, GetValue, GetRegister, GetValueOperand, constValues));

                // Emit extra result extraction instructions for multi-return calls
                if (instruction is MirCallInstruction { ExtraResults.Count: > 0 } callInstr)
                {
                    for (int i = 0; i < callInstr.ExtraResults.Count; i++)
                    {
                        (MirValueId extraValue, BladeType extraType) = callInstr.ExtraResults[i];
                        VirtualLirValue extraDest = GetValue(extraValue);
                        ReturnPlacement placement = GetExtraResultPlacement(callInstr, i);
                        instructions.Add(new LirOpInstruction(
                            new LirCallExtractFlagOperation(placement == ReturnPlacement.FlagC ? MirFlag.C : MirFlag.Z),
                            extraDest,
                            extraType,
                            [],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            instruction.Span));
                    }
                }
                else if (instruction is MirSpawnInstruction { ExtraResults.Count: > 0 } spawnInstr)
                {
                    for (int i = 0; i < spawnInstr.ExtraResults.Count; i++)
                    {
                        (MirValueId extraValue, BladeType extraType) = spawnInstr.ExtraResults[i];
                        VirtualLirValue extraDest = GetValue(extraValue);
                        instructions.Add(new LirOpInstruction(
                            new LirCallExtractFlagOperation(MirFlag.NC),
                            extraDest,
                            extraType,
                            [],
                            hasSideEffects: true,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            instruction.Span));
                    }
                }
            }

            LirTerminator terminator = LowerTerminator(mirBlock.Terminator, GetValueOperand, GetBlockRef);
            blocks.Add(new LirBlock(GetBlockRef(mirBlock.Ref), parameters, instructions, terminator));
        }

        return new LirFunction(mirFunction, blocks, flagValues);
    }

    private static LirInstruction LowerInstruction(
        MirInstruction instruction,
        System.Func<MirValueId, VirtualLirValue> getValue,
        System.Func<MirValueId, LirVirtualRegister> getRegister,
        System.Func<MirValueId, LirOperand> getValueOperand,
        IReadOnlyDictionary<MirValueId, BladeValue> constValues)
    {
        VirtualLirValue? destination = instruction.Result is MirValueId result
            ? getValue(result)
            : null;

        return instruction switch
        {
            MirConstantInstruction constant => new LirOpInstruction(
                new LirConstOperation(),
                destination,
                constant.ResultType,
                LowerConstantOperands(constant),
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                constant.Span),

            MirLoadPlaceInstruction loadPlace => new LirOpInstruction(
                new LirLoadPlaceOperation(),
                destination,
                loadPlace.ResultType,
                [new LirPlaceOperand(loadPlace.Place)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadPlace.Span),

            MirCopyInstruction copy => new LirOpInstruction(
                new LirMovOperation(),
                destination,
                copy.ResultType,
                [getValueOperand(copy.Source)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                copy.Span),

            MirUnaryInstruction unary => new LirOpInstruction(
                new LirUnaryOperation(unary.Operator),
                destination,
                unary.ResultType,
                [getValueOperand(unary.Operand)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                unary.Span),

            MirBinaryInstruction binary => new LirOpInstruction(
                new LirBinaryOperation(binary.Operator, binary.ComparisonLoweringKind),
                destination,
                binary.ResultType,
                [getValueOperand(binary.Left), getValueOperand(binary.Right)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                binary.Span),

            MirPointerOffsetInstruction pointerOffset => new LirOpInstruction(
                new LirPointerOffsetOperation(pointerOffset.OperatorKind, pointerOffset.Stride),
                destination,
                pointerOffset.ResultType,
                [new LirRegisterOperand(getRegister(pointerOffset.BaseAddress)), new LirRegisterOperand(getRegister(pointerOffset.Delta))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                pointerOffset.Span),

            MirPointerDifferenceInstruction pointerDifference => new LirOpInstruction(
                new LirPointerDifferenceOperation(pointerDifference.Stride),
                destination,
                pointerDifference.ResultType,
                [new LirRegisterOperand(getRegister(pointerDifference.Left)), new LirRegisterOperand(getRegister(pointerDifference.Right))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                pointerDifference.Span),

            MirConvertInstruction convert => new LirOpInstruction(
                new LirConvertOperation(),
                destination,
                convert.ResultType,
                [getValueOperand(convert.Operand)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                convert.Span),

            MirStructLiteralInstruction structLiteral => new LirOpInstruction(
                new LirStructLiteralOperation(LowerStructLiteralMembers(structLiteral.Fields)),
                destination,
                structLiteral.ResultType,
                LowerStructLiteralOperands(structLiteral.Fields, getValueOperand),
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                structLiteral.Span),

            MirLoadMemberInstruction loadMember => new LirOpInstruction(
                new LirLoadMemberOperation(loadMember.Member),
                destination,
                loadMember.ResultType,
                [new LirRegisterOperand(getRegister(loadMember.Receiver))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadMember.Span),

            MirLoadIndexInstruction loadIndex => new LirOpInstruction(
                new LirLoadIndexOperation(loadIndex.IndexedType, loadIndex.StorageClass),
                destination,
                loadIndex.ResultType,
                [new LirRegisterOperand(getRegister(loadIndex.Indexed)), new LirRegisterOperand(getRegister(loadIndex.Index))],
                loadIndex.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadIndex.Span),

            MirLoadDerefInstruction loadDeref => new LirOpInstruction(
                new LirLoadDerefOperation(loadDeref.PointerType, loadDeref.StorageClass),
                destination,
                loadDeref.ResultType,
                [new LirRegisterOperand(getRegister(loadDeref.Address))],
                loadDeref.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadDeref.Span),

            MirBitfieldExtractInstruction bitfieldExtract => new LirOpInstruction(
                new LirBitfieldExtractOperation(bitfieldExtract.Member),
                destination,
                bitfieldExtract.ResultType,
                [new LirRegisterOperand(getRegister(bitfieldExtract.Receiver))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                bitfieldExtract.Span),

            MirBitfieldInsertInstruction bitfieldInsert => new LirOpInstruction(
                new LirBitfieldInsertOperation(bitfieldInsert.Member),
                destination,
                bitfieldInsert.ResultType,
                [new LirRegisterOperand(getRegister(bitfieldInsert.Receiver)), getValueOperand(bitfieldInsert.Value)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                bitfieldInsert.Span),

            MirInsertMemberInstruction insertMember => new LirOpInstruction(
                new LirInsertMemberOperation(insertMember.Member),
                destination,
                insertMember.ResultType,
                [new LirRegisterOperand(getRegister(insertMember.Receiver)), getValueOperand(insertMember.Value)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                insertMember.Span),

            MirCallInstruction call => new LirOpInstruction(
                new LirCallOperation(call.Function),
                destination,
                call.ResultType,
                LowerOperands(call.Arguments, getValueOperand),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                call.Span),

            MirSpawnInstruction spawn => new LirOpInstruction(
                new LirSpawnOperation(spawn.OperatorKind, spawn.Task, spawn.RequestedResultCount),
                destination,
                spawn.ResultType,
                LowerOperands(spawn.Arguments, getValueOperand),
                hasSideEffects: true,
                predicate: null,
                writesC: true,
                writesZ: false,
                spawn.Span),

            MirIntrinsicCallInstruction intrinsic => new LirOpInstruction(
                new LirIntrinsicOperation(intrinsic.Mnemonic),
                destination,
                intrinsic.ResultType,
                LowerIntrinsicArguments(intrinsic.Arguments, getRegister, constValues),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                intrinsic.Span),

            MirStoreIndexInstruction storeIndex => new LirOpInstruction(
                new LirStoreIndexOperation(storeIndex.IndexedType, storeIndex.StorageClass),
                destination: null,
                resultType: storeIndex.ResultType,
                [
                    new LirRegisterOperand(getRegister(storeIndex.Indexed)),
                    new LirRegisterOperand(getRegister(storeIndex.Index)),
                    getValueOperand(storeIndex.Value),
                ],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storeIndex.Span),

            MirStoreDerefInstruction storeDeref => new LirOpInstruction(
                new LirStoreDerefOperation(storeDeref.PointerType, storeDeref.StorageClass),
                destination: null,
                resultType: storeDeref.ResultType,
                [new LirRegisterOperand(getRegister(storeDeref.Address)), getValueOperand(storeDeref.Value)],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storeDeref.Span),

            MirStorePlaceInstruction storePlace => new LirOpInstruction(
                new LirStorePlaceOperation(),
                destination: null,
                resultType: null,
                [new LirPlaceOperand(storePlace.Place), getValueOperand(storePlace.Value)],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storePlace.Span),

            MirUpdatePlaceInstruction updatePlace => new LirOpInstruction(
                new LirUpdatePlaceOperation(updatePlace.OperatorKind, updatePlace.PointerArithmeticStride),
                destination: null,
                resultType: null,
                [new LirPlaceOperand(updatePlace.Place), getValueOperand(updatePlace.Value)],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                updatePlace.Span),

            MirInlineAsmInstruction inlineAsm => new LirInlineAsmInstruction(
                inlineAsm.Volatility,
                inlineAsm.FlagOutput,
                inlineAsm.ParsedLines,
                LowerInlineAsmBindings(inlineAsm.Bindings, getValueOperand),
                destination,
                inlineAsm.ResultType,
                inlineAsm.Span),

            MirYieldInstruction yield => new LirOpInstruction(
                new LirYieldOperation(),
                destination: null,
                resultType: null,
                [],
                yield.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                yield.Span),

            MirYieldToInstruction yieldTo => new LirOpInstruction(
                new LirYieldToOperation(yieldTo.TargetFunction),
                destination: null,
                resultType: null,
                LowerOperands(yieldTo.Arguments, getValueOperand),
                yieldTo.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                yieldTo.Span),

            MirRepSetupInstruction repSetup => new LirOpInstruction(
                new LirRepSetupOperation(),
                destination: null,
                resultType: null,
                [new LirRegisterOperand(getRegister(repSetup.Count))],
                repSetup.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                repSetup.Span),

            MirRepIterInstruction repIter => new LirOpInstruction(
                new LirRepIterOperation(),
                destination: null,
                resultType: null,
                [new LirRegisterOperand(getRegister(repIter.Count))],
                repIter.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                repIter.Span),

            MirRepForSetupInstruction repForSetup => new LirOpInstruction(
                new LirRepForSetupOperation(),
                destination: null,
                resultType: null,
                [new LirRegisterOperand(getRegister(repForSetup.Start)), new LirRegisterOperand(getRegister(repForSetup.End))],
                repForSetup.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                repForSetup.Span),

            MirRepForIterInstruction repForIter => new LirOpInstruction(
                new LirRepForIterOperation(repForIter.IndexCarrierOrdinal),
                destination: null,
                resultType: null,
                FlattenRepForIterOperands(repForIter, getRegister),
                repForIter.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                repForIter.Span),

            MirNoIrqBeginInstruction begin => new LirOpInstruction(
                new LirNoIrqBeginOperation(),
                destination: null,
                resultType: null,
                [],
                begin.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                begin.Span),

            MirNoIrqEndInstruction end => new LirOpInstruction(
                new LirNoIrqEndOperation(),
                destination: null,
                resultType: null,
                [],
                end.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                end.Span),

            _ => Assert.UnreachableValue<LirInstruction>(), // pragma: force-coverage
        };
    }

    private static IReadOnlyList<LirOperand> LowerConstantOperands(MirConstantInstruction constant)
    {
        if (constant.Value is null)
            return [];

        return [new LirImmediateOperand(constant.Value)];
    }

    private static LirTerminator LowerTerminator(
        MirTerminator terminator,
        System.Func<MirValueId, LirOperand> getValueOperand,
        System.Func<MirBlockRef, LirBlockRef> getBlockRef)
    {
        return terminator switch
        {
            MirGotoTerminator mirGoto => new LirGotoTerminator(
                getBlockRef(mirGoto.Target),
                LowerOperands(mirGoto.Arguments, getValueOperand),
                mirGoto.Span),

            MirBranchTerminator branch => new LirBranchTerminator(
                getValueOperand(branch.Condition),
                getBlockRef(branch.TrueTarget),
                getBlockRef(branch.FalseTarget),
                LowerOperands(branch.TrueArguments, getValueOperand),
                LowerOperands(branch.FalseArguments, getValueOperand),
                branch.Span,
                branch.ConditionFlag),

            MirReturnTerminator ret => new LirReturnTerminator(
                LowerOperands(ret.Values, getValueOperand),
                ret.Span),

            MirUnreachableTerminator unreachable => new LirUnreachableTerminator(unreachable.Span),
            _ => new LirUnreachableTerminator(new TextSpan(0, 0)),
        };
    }

    private static IReadOnlyList<LirOperand> LowerOperands(
        IReadOnlyList<MirValueId> values,
        System.Func<MirValueId, LirOperand> getValueOperand)
    {
        List<LirOperand> operands = new(values.Count);
        foreach (MirValueId value in values)
            operands.Add(getValueOperand(value));
        return operands;
    }

    private static IReadOnlyList<LirOperand> LowerIntrinsicArguments(
        IReadOnlyList<MirValueId> values,
        System.Func<MirValueId, LirVirtualRegister> getRegister,
        IReadOnlyDictionary<MirValueId, BladeValue> constValues)
    {
        List<LirOperand> operands = new(values.Count);
        foreach (MirValueId value in values)
        {
            operands.Add(constValues.TryGetValue(value, out BladeValue? constVal)
                ? new LirImmediateOperand(constVal)
                : new LirRegisterOperand(getRegister(value)));
        }
        return operands;
    }

    private static IReadOnlyList<LirInlineAsmBinding> LowerInlineAsmBindings(
        IReadOnlyList<MirInlineAsmBinding> bindings,
        System.Func<MirValueId, LirOperand> getValueOperand)
    {
        List<LirInlineAsmBinding> lowered = new(bindings.Count);
        foreach (MirInlineAsmBinding binding in bindings)
        {
            LirOperand operand;
            if (binding.Place is not null)
            {
                operand = new LirPlaceOperand(binding.Place);
            }
            else if (binding.Value is MirValueId value)
            {
                operand = getValueOperand(value);
            }
            else
            {
                operand = Assert.UnreachableValue<LirOperand>(); // pragma: force-coverage
            }

            lowered.Add(new LirInlineAsmBinding(binding.Slot, binding.Symbol, operand, binding.Access));
        }

        return lowered;
    }

    private static IReadOnlyList<AggregateMemberSymbol> LowerStructLiteralMembers(IReadOnlyList<MirStructLiteralField> fields)
    {
        List<AggregateMemberSymbol> members = new(fields.Count);
        foreach (MirStructLiteralField field in fields)
            members.Add(field.Member);
        return members;
    }

    private static IReadOnlyList<LirOperand> LowerStructLiteralOperands(
        IReadOnlyList<MirStructLiteralField> fields,
        System.Func<MirValueId, LirOperand> getValueOperand)
    {
        List<LirOperand> operands = new(fields.Count);
        foreach (MirStructLiteralField field in fields)
            operands.Add(getValueOperand(field.Value));
        return operands;
    }

    private static IReadOnlyList<LirOperand> FlattenRepForIterOperands(
        MirRepForIterInstruction instruction,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        Assert.Invariant(
            instruction.CarrierValues.Count == instruction.CurrentValues.Count,
            "repfor.iter carrier and current value counts must match.");

        List<LirOperand> operands = new(instruction.CarrierValues.Count * 2);
        for (int i = 0; i < instruction.CarrierValues.Count; i++)
        {
            operands.Add(new LirRegisterOperand(getRegister(instruction.CarrierValues[i])));
            operands.Add(new LirRegisterOperand(getRegister(instruction.CurrentValues[i])));
        }

        return operands;
    }

    private static ReturnPlacement GetExtraResultPlacement(MirCallInstruction call, int extraResultIndex)
    {
        int returnSlotIndex = extraResultIndex + 1;
        Assert.Invariant(
            returnSlotIndex < call.Function.ReturnSlots.Count,
            $"Call '{call.Function.Name}' exposes extra result index {extraResultIndex}, but no corresponding return slot exists.");

        ReturnPlacement placement = call.Function.ReturnSlots[returnSlotIndex].Placement;
        Assert.Invariant(
            placement is ReturnPlacement.FlagC or ReturnPlacement.FlagZ,
            $"Call '{call.Function.Name}' extra result index {extraResultIndex} must be flag-backed, but uses placement '{placement}'.");
        return placement;
    }
}
