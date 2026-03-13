using System.Collections.Generic;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Source;

namespace Blade.IR.Lir;

public static class LirLowerer
{
    public static LirModule Lower(MirModule module)
    {
        List<LirFunction> functions = new(module.Functions.Count);
        foreach (MirFunction mirFunction in module.Functions)
            functions.Add(LowerFunction(mirFunction));
        return new LirModule(module.StoragePlaces, functions);
    }

    private static LirFunction LowerFunction(MirFunction mirFunction)
    {
        Dictionary<MirValueId, LirVirtualRegister> registers = [];
        int nextRegisterId = 0;

        LirVirtualRegister GetRegister(MirValueId value)
        {
            if (registers.TryGetValue(value, out LirVirtualRegister register))
                return register;
            LirVirtualRegister fresh = new(nextRegisterId++);
            registers[value] = fresh;
            return fresh;
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
                instructions.Add(LowerInstruction(instruction, GetRegister));

            LirTerminator terminator = LowerTerminator(mirBlock.Terminator, GetRegister);
            blocks.Add(new LirBlock(mirBlock.Label, parameters, instructions, terminator));
        }

        return new LirFunction(
            mirFunction.Name,
            mirFunction.IsEntryPoint,
            mirFunction.Kind,
            mirFunction.ReturnTypes,
            blocks);
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
                "const",
                destination,
                constant.ResultType,
                [new LirImmediateOperand(constant.Value, constant.ResultType!)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                constant.Span),

            MirLoadSymbolInstruction load => new LirOpInstruction(
                "load.sym",
                destination,
                load.ResultType,
                [new LirSymbolOperand(load.SymbolName)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                load.Span),

            MirLoadPlaceInstruction loadPlace => new LirOpInstruction(
                "load.place",
                destination,
                loadPlace.ResultType,
                [new LirPlaceOperand(loadPlace.Place)],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                loadPlace.Span),

            MirCopyInstruction copy => new LirOpInstruction(
                "mov",
                destination,
                copy.ResultType,
                [new LirRegisterOperand(getRegister(copy.Source))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                copy.Span),

            MirUnaryInstruction unary => new LirOpInstruction(
                $"unary.{unary.Operator}",
                destination,
                unary.ResultType,
                [new LirRegisterOperand(getRegister(unary.Operand))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                unary.Span),

            MirBinaryInstruction binary => new LirOpInstruction(
                $"binary.{binary.Operator}",
                destination,
                binary.ResultType,
                [new LirRegisterOperand(getRegister(binary.Left)), new LirRegisterOperand(getRegister(binary.Right))],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                binary.Span),

            MirOpInstruction op => new LirOpInstruction(
                op.Opcode,
                destination,
                op.ResultType,
                LowerOperands(op.Operands, getRegister),
                op.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                op.Span),

            MirSelectInstruction select => new LirOpInstruction(
                "select",
                destination,
                select.ResultType,
                [
                    new LirRegisterOperand(getRegister(select.Condition)),
                    new LirRegisterOperand(getRegister(select.WhenTrue)),
                    new LirRegisterOperand(getRegister(select.WhenFalse)),
                ],
                hasSideEffects: false,
                predicate: null,
                writesC: false,
                writesZ: false,
                select.Span),

            MirCallInstruction call => new LirOpInstruction(
                "call",
                destination,
                call.ResultType,
                LowerCallOperands(call.FunctionName, call.Arguments, getRegister),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                call.Span),

            MirIntrinsicCallInstruction intrinsic => new LirOpInstruction(
                "intrinsic",
                destination,
                intrinsic.ResultType,
                LowerCallOperands($"@{intrinsic.IntrinsicName}", intrinsic.Arguments, getRegister),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                intrinsic.Span),

            MirStoreInstruction store => new LirOpInstruction(
                $"store.{store.Target}",
                destination: null,
                resultType: null,
                LowerOperands(store.Operands, getRegister),
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                store.Span),

            MirStorePlaceInstruction storePlace => new LirOpInstruction(
                "store.place",
                destination: null,
                resultType: null,
                [new LirPlaceOperand(storePlace.Place), new LirRegisterOperand(getRegister(storePlace.Value))],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                storePlace.Span),

            MirUpdatePlaceInstruction updatePlace => new LirOpInstruction(
                $"update.place.{updatePlace.OperatorKind}",
                destination: null,
                resultType: null,
                [new LirPlaceOperand(updatePlace.Place), new LirRegisterOperand(getRegister(updatePlace.Value))],
                hasSideEffects: true,
                predicate: null,
                writesC: false,
                writesZ: false,
                updatePlace.Span),

            MirInlineAsmInstruction inlineAsm => new LirInlineAsmInstruction(
                inlineAsm.Body,
                LowerInlineAsmBindings(inlineAsm.Bindings, getRegister),
                inlineAsm.Span),

            MirPseudoInstruction pseudo => new LirOpInstruction(
                $"pseudo.{pseudo.Opcode}",
                destination: null,
                resultType: null,
                LowerOperands(pseudo.Operands, getRegister),
                pseudo.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                pseudo.Span),

            _ => new LirOpInstruction(
                "unknown",
                destination,
                instruction.ResultType,
                [],
                instruction.HasSideEffects,
                predicate: null,
                writesC: false,
                writesZ: false,
                instruction.Span),
        };
    }

    private static LirTerminator LowerTerminator(
        MirTerminator terminator,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        return terminator switch
        {
            MirGotoTerminator mirGoto => new LirGotoTerminator(
                mirGoto.TargetLabel,
                LowerOperands(mirGoto.Arguments, getRegister),
                mirGoto.Span),

            MirBranchTerminator branch => new LirBranchTerminator(
                new LirRegisterOperand(getRegister(branch.Condition)),
                branch.TrueLabel,
                branch.FalseLabel,
                LowerOperands(branch.TrueArguments, getRegister),
                LowerOperands(branch.FalseArguments, getRegister),
                branch.Span),

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

    private static IReadOnlyList<LirOperand> LowerCallOperands(
        string target,
        IReadOnlyList<MirValueId> arguments,
        System.Func<MirValueId, LirVirtualRegister> getRegister)
    {
        List<LirOperand> operands = new(arguments.Count + 1)
        {
            new LirSymbolOperand(target)
        };

        foreach (MirValueId argument in arguments)
            operands.Add(new LirRegisterOperand(getRegister(argument)));
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
            lowered.Add(new LirInlineAsmBinding(binding.Name, operand));
        }

        return lowered;
    }
}
