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
        return new LirModule(module.StoragePlaces, module.StorageDefinitions, functions);
    }

    private static LirFunction LowerFunction(MirFunction mirFunction)
    {
        Dictionary<MirValueId, LirVirtualRegister> registers = [];
        Dictionary<MirBlockRef, LirBlockRef> blockRefs = [];

        LirVirtualRegister GetRegister(MirValueId value)
        {
            if (registers.TryGetValue(value, out LirVirtualRegister? register) && register is not null)
                return register;
            LirVirtualRegister fresh = new();
            registers[value] = fresh;
            return fresh;
        }

        LirBlockRef GetBlockRef(MirBlockRef blockRef)
        {
            if (blockRefs.TryGetValue(blockRef, out LirBlockRef? mapped) && mapped is not null)
                return mapped;

            LirBlockRef created = new();
            blockRefs[blockRef] = created;
            return created;
        }

        List<LirBlock> blocks = new(mirFunction.Blocks.Count);
        foreach (MirBlock mirBlock in mirFunction.Blocks)
        {
            List<LirBlockParameter> parameters = [];
            foreach (MirBlockParameter parameter in mirBlock.Parameters)
            {
                LirVirtualRegister register = GetRegister(parameter.Value);
                parameters.Add(new LirBlockParameter(register, parameter.Name, parameter.Type));
            }

            List<LirInstruction> instructions = [];
            foreach (MirInstruction instruction in mirBlock.Instructions)
            {
                instructions.Add(LowerInstruction(instruction, GetRegister));

                // Emit extra result extraction instructions for multi-return calls
                if (instruction is MirCallInstruction { ExtraResults.Count: > 0 } callInstr)
                {
                    for (int i = 0; i < callInstr.ExtraResults.Count; i++)
                    {
                        (MirValueId extraValue, TypeSymbol extraType) = callInstr.ExtraResults[i];
                        LirVirtualRegister extraDest = GetRegister(extraValue);
                        ReturnPlacement placement = GetExtraResultPlacement(callInstr, i);
                        instructions.Add(new LirOpInstruction(
                            new LirCallExtractFlagOperation(placement == ReturnPlacement.FlagC ? MirFlag.C : MirFlag.Z),
                            extraDest,
                            extraType,
                            [],
                            hasSideEffects: false,
                            predicate: null,
                            writesC: false,
                            writesZ: false,
                            instruction.Span));
                    }
                }
            }

            LirTerminator terminator = LowerTerminator(mirBlock.Terminator, GetRegister, GetBlockRef);
            blocks.Add(new LirBlock(GetBlockRef(mirBlock.Ref), parameters, instructions, terminator));
        }

        return new LirFunction(mirFunction, blocks);
    }

    private static LirInstruction LowerInstruction(
        MirInstruction instruction,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        LirVirtualRegister? destination = instruction.Result is MirValueId result
            ? getRegister(result)
            : null;

        return instruction switch
        {
            MirConstantInstruction constant => new LirOpInstruction(
                new LirConstOperation(),
                destination,
                constant.ResultType,
                [new LirImmediateOperand(constant.Value, constant.ResultType!)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                constant.Span),

            MirLoadSymbolInstruction load => new LirOpInstruction(
                new LirLoadAddressOperation(),
                destination,
                load.ResultType,
                [new LirPlaceOperand(load.Symbol)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                load.Span),

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
                [new LirRegisterOperand(getRegister(copy.Source))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                copy.Span),

            MirUnaryInstruction unary => new LirOpInstruction(
                new LirUnaryOperation(unary.Operator),
                destination,
                unary.ResultType,
                [new LirRegisterOperand(getRegister(unary.Operand))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                unary.Span),

            MirBinaryInstruction binary => new LirOpInstruction(
                new LirBinaryOperation(binary.Operator),
                destination,
                binary.ResultType,
                [new LirRegisterOperand(getRegister(binary.Left)), new LirRegisterOperand(getRegister(binary.Right))],
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
                [new LirRegisterOperand(getRegister(convert.Operand))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                convert.Span),

            MirRangeInstruction range => new LirOpInstruction(
                new LirRangeOperation(),
                destination,
                range.ResultType,
                [new LirRegisterOperand(getRegister(range.Start)), new LirRegisterOperand(getRegister(range.End))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                range.Span),

            MirStructLiteralInstruction structLiteral => new LirOpInstruction(
                new LirStructLiteralOperation(LowerStructLiteralMembers(structLiteral.Fields)),
                destination,
                structLiteral.ResultType,
                LowerStructLiteralOperands(structLiteral.Fields, getRegister),
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
                new LirLoadIndexOperation(loadIndex.StorageClass),
                destination,
                loadIndex.ResultType,
                [new LirRegisterOperand(getRegister(loadIndex.Indexed)), new LirRegisterOperand(getRegister(loadIndex.Index))],
                loadIndex.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadIndex.Span),

            MirLoadDerefInstruction loadDeref => new LirOpInstruction(
                new LirLoadDerefOperation(loadDeref.StorageClass),
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
                [new LirRegisterOperand(getRegister(bitfieldInsert.Receiver)), new LirRegisterOperand(getRegister(bitfieldInsert.Value))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                bitfieldInsert.Span),

            MirInsertMemberInstruction insertMember => new LirOpInstruction(
                new LirInsertMemberOperation(insertMember.Member),
                destination,
                insertMember.ResultType,
                [new LirRegisterOperand(getRegister(insertMember.Receiver)), new LirRegisterOperand(getRegister(insertMember.Value))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                insertMember.Span),

            MirCallInstruction call => new LirOpInstruction(
                new LirCallOperation(call.Function),
                destination,
                call.ResultType,
                LowerOperands(call.Arguments, getRegister),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                call.Span),

            MirIntrinsicCallInstruction intrinsic => new LirOpInstruction(
                new LirIntrinsicOperation(intrinsic.Mnemonic),
                destination,
                intrinsic.ResultType,
                LowerOperands(intrinsic.Arguments, getRegister),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                intrinsic.Span),

            MirStoreIndexInstruction storeIndex => new LirOpInstruction(
                new LirStoreIndexOperation(storeIndex.StorageClass),
                destination: null,
                resultType: storeIndex.ResultType,
                [
                    new LirRegisterOperand(getRegister(storeIndex.Indexed)),
                    new LirRegisterOperand(getRegister(storeIndex.Index)),
                    new LirRegisterOperand(getRegister(storeIndex.Value)),
                ],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storeIndex.Span),

            MirStoreDerefInstruction storeDeref => new LirOpInstruction(
                new LirStoreDerefOperation(storeDeref.StorageClass),
                destination: null,
                resultType: storeDeref.ResultType,
                [new LirRegisterOperand(getRegister(storeDeref.Address)), new LirRegisterOperand(getRegister(storeDeref.Value))],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storeDeref.Span),

            MirStorePlaceInstruction storePlace => new LirOpInstruction(
                new LirStorePlaceOperation(),
                destination: null,
                resultType: null,
                [new LirPlaceOperand(storePlace.Place), new LirRegisterOperand(getRegister(storePlace.Value))],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storePlace.Span),

            MirUpdatePlaceInstruction updatePlace => new LirOpInstruction(
                new LirUpdatePlaceOperation(updatePlace.OperatorKind, updatePlace.PointerArithmeticStride),
                destination: null,
                resultType: null,
                [new LirPlaceOperand(updatePlace.Place), new LirRegisterOperand(getRegister(updatePlace.Value))],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                updatePlace.Span),

            MirInlineAsmInstruction inlineAsm => new LirInlineAsmInstruction(
                inlineAsm.Volatility,
                inlineAsm.Body,
                inlineAsm.FlagOutput,
                inlineAsm.ParsedLines,
                LowerInlineAsmBindings(inlineAsm.Bindings, getRegister),
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
                LowerOperands(yieldTo.Arguments, getRegister),
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
                new LirRepForIterOperation(),
                destination: null,
                resultType: null,
                [new LirRegisterOperand(getRegister(repForIter.Start)), new LirRegisterOperand(getRegister(repForIter.End))],
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

            _ => Assert.UnreachableValue<LirInstruction>(),
        };
    }

    private static LirTerminator LowerTerminator(
        MirTerminator terminator,
        System.Func<MirValueId, LirVirtualRegister> getRegister,
        System.Func<MirBlockRef, LirBlockRef> getBlockRef)
    {
        return terminator switch
        {
            MirGotoTerminator mirGoto => new LirGotoTerminator(
                getBlockRef(mirGoto.Target),
                LowerOperands(mirGoto.Arguments, getRegister),
                mirGoto.Span),

            MirBranchTerminator branch => new LirBranchTerminator(
                new LirRegisterOperand(getRegister(branch.Condition)),
                getBlockRef(branch.TrueTarget),
                getBlockRef(branch.FalseTarget),
                LowerOperands(branch.TrueArguments, getRegister),
                LowerOperands(branch.FalseArguments, getRegister),
                branch.Span,
                branch.ConditionFlag),

            MirReturnTerminator ret => new LirReturnTerminator(
                LowerOperands(ret.Values, getRegister),
                ret.Span),

            MirUnreachableTerminator unreachable => new LirUnreachableTerminator(unreachable.Span),
            _ => new LirUnreachableTerminator(new TextSpan(0, 0)),
        };
    }

    private static IReadOnlyList<LirOperand> LowerOperands(
        IReadOnlyList<MirValueId> values,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        List<LirOperand> operands = new(values.Count);
        foreach (MirValueId value in values)
            operands.Add(new LirRegisterOperand(getRegister(value)));
        return operands;
    }

    private static IReadOnlyList<LirInlineAsmBinding> LowerInlineAsmBindings(
        IReadOnlyList<MirInlineAsmBinding> bindings,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        List<LirInlineAsmBinding> lowered = new(bindings.Count);
        foreach (MirInlineAsmBinding binding in bindings)
        {
            LirOperand operand = binding.Value is MirValueId value
                ? new LirRegisterOperand(getRegister(value))
                : new LirPlaceOperand(binding.Place!);
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
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        List<LirOperand> operands = new(fields.Count);
        foreach (MirStructLiteralField field in fields)
            operands.Add(new LirRegisterOperand(getRegister(field.Value)));
        return operands;
    }

    private static ReturnPlacement GetExtraResultPlacement(MirCallInstruction call, int extraResultIndex)
    {
        switch (extraResultIndex)
        {
            case 0:
                return ReturnPlacement.FlagC;

            case 1:
                return ReturnPlacement.FlagZ;

            default:
                return Assert.UnreachableValue<ReturnPlacement>(
                    $"Call '{call.Function.Name}' exposes extra result index {extraResultIndex}, but only C and Z flag result slots are available.");
        }
    }
}
