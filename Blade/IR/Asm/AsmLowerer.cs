using System;
using System.Collections.Generic;
using Blade.IR.Lir;
using Blade.Semantics;
using Blade.Semantics.Bound;

namespace Blade.IR.Asm;

/// <summary>
/// Lowers LIR (virtual registers, high-level opcodes) to ASMIR
/// (real P2 mnemonics, virtual registers, label-based jumps).
/// Performs instruction selection and calling convention lowering.
/// </summary>
public static class AsmLowerer
{
    public static AsmModule Lower(LirModule module)
    {
        // Run call graph analysis to determine CC tiers and dead functions
        CallGraphResult cgResult = CallGraphAnalyzer.Analyze(module);

        // Build a map of function name → block label → block parameter registers
        // so φ-moves can emit actual MOV instructions to the right target registers.
        Dictionary<string, Dictionary<string, IReadOnlyList<LirBlockParameter>>> blockParamMap = [];
        foreach (LirFunction function in module.Functions)
        {
            Dictionary<string, IReadOnlyList<LirBlockParameter>> funcBlocks = [];
            foreach (LirBlock block in function.Blocks)
                funcBlocks[block.Label] = block.Parameters;
            blockParamMap[function.Name] = funcBlocks;
        }

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            // Eliminate dead (unreachable) functions from codegen
            if (cgResult.DeadFunctions.Contains(function.Name))
                continue;

            CallingConventionTier tier = cgResult.Tiers.GetValueOrDefault(function.Name, CallingConventionTier.General);
            LoweringContext ctx = new(function, tier, cgResult.Tiers, blockParamMap[function.Name]);
            functions.Add(LowerFunction(ctx));
        }

        return new AsmModule(functions);
    }

    private sealed class LoweringContext
    {
        public LirFunction Function { get; }
        public CallingConventionTier Tier { get; }
        public Dictionary<string, CallingConventionTier> CalleeTiers { get; }
        public Dictionary<string, IReadOnlyList<LirBlockParameter>> BlockParams { get; }

        public LoweringContext(
            LirFunction function,
            CallingConventionTier tier,
            Dictionary<string, CallingConventionTier> calleeTiers,
            Dictionary<string, IReadOnlyList<LirBlockParameter>> blockParams)
        {
            Function = function;
            Tier = tier;
            CalleeTiers = calleeTiers;
            BlockParams = blockParams;
        }
    }

    private static AsmFunction LowerFunction(LoweringContext ctx)
    {
        List<AsmNode> nodes = [];
        nodes.Add(new AsmDirectiveNode($"function {ctx.Function.Name}"));

        foreach (LirBlock block in ctx.Function.Blocks)
        {
            string blockLabel = $"{ctx.Function.Name}_{block.Label}";
            nodes.Add(new AsmLabelNode(blockLabel));

            foreach (LirInstruction instruction in block.Instructions)
                LowerInstruction(nodes, instruction, ctx);

            LowerTerminator(nodes, ctx, block.Terminator);
        }

        return new AsmFunction(ctx.Function.Name, ctx.Function.IsEntryPoint, ctx.Tier, nodes);
    }

    private static void LowerInstruction(List<AsmNode> nodes, LirInstruction instruction, LoweringContext ctx)
    {
        if (instruction is not LirOpInstruction op)
        {
            nodes.Add(new AsmCommentNode($"unknown instruction: {instruction.Opcode}"));
            return;
        }

        switch (op.Opcode)
        {
            case "const":
                LowerConst(nodes, op);
                break;
            case "mov":
                LowerMov(nodes, op);
                break;
            case "load.sym":
                LowerLoadSym(nodes, op);
                break;
            case "select":
                LowerSelect(nodes, op);
                break;
            case "call":
                LowerCall(nodes, op, ctx);
                break;
            case "intrinsic":
                LowerIntrinsic(nodes, op);
                break;
            case "convert":
                LowerConvert(nodes, op);
                break;
            default:
                if (op.Opcode.StartsWith("binary.", StringComparison.Ordinal))
                    LowerBinary(nodes, op);
                else if (op.Opcode.StartsWith("unary.", StringComparison.Ordinal))
                    LowerUnary(nodes, op);
                else if (op.Opcode.StartsWith("store.", StringComparison.Ordinal))
                    LowerStore(nodes, op);
                else if (op.Opcode.StartsWith("pseudo.", StringComparison.Ordinal))
                    LowerPseudo(nodes, op);
                else if (op.Opcode.StartsWith("yieldto:", StringComparison.Ordinal)
                         || op.Opcode == "yield")
                    LowerYield(nodes, op);
                else
                    nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                break;
        }
    }

    private static void LowerConst(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        long value = GetImmediateValue(op.Operands[0]);
        nodes.Add(Emit("MOV", dest, new AsmImmediateOperand(value)));
    }

    private static void LowerMov(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        nodes.Add(Emit("MOV", dest, src));
    }

    private static void LowerLoadSym(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        string symbol = ((LirSymbolOperand)op.Operands[0]).Symbol;
        nodes.Add(Emit("MOV", dest, new AsmSymbolOperand(symbol)));
    }

    private static void LowerConvert(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        nodes.Add(Emit("MOV", dest, src));
    }

    private static void LowerBinary(List<AsmNode> nodes, LirOpInstruction op)
    {
        string operatorName = op.Opcode["binary.".Length..];
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand left = OpReg(op.Operands[0]);
        AsmRegisterOperand right = OpReg(op.Operands[1]);

        if (!Enum.TryParse<BoundBinaryOperatorKind>(operatorName, out BoundBinaryOperatorKind kind))
        {
            nodes.Add(new AsmCommentNode($"unknown binary op: {operatorName}"));
            return;
        }

        switch (kind)
        {
            case BoundBinaryOperatorKind.Add:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("ADD", dest, right));
                break;

            case BoundBinaryOperatorKind.Subtract:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SUB", dest, right));
                break;

            case BoundBinaryOperatorKind.Multiply:
                nodes.Add(Emit("QMUL", left, right));
                nodes.Add(Emit("GETQX", dest));
                break;

            case BoundBinaryOperatorKind.Divide:
                nodes.Add(Emit("QDIV", left, right));
                nodes.Add(Emit("GETQX", dest));
                break;

            case BoundBinaryOperatorKind.BitwiseAnd:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("AND", dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseOr:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("OR", dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseXor:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("XOR", dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftLeft:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHL", dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftRight:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHR", dest, right));
                break;

            case BoundBinaryOperatorKind.Equals:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRZ", dest));
                break;

            case BoundBinaryOperatorKind.NotEquals:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRNZ", dest));
                break;

            case BoundBinaryOperatorKind.Less:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRC", dest));
                break;

            case BoundBinaryOperatorKind.LessOrEqual:
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRNC", dest));
                break;

            case BoundBinaryOperatorKind.Greater:
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRC", dest));
                break;

            case BoundBinaryOperatorKind.GreaterOrEqual:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRNC", dest));
                break;
        }
    }

    private static void LowerUnary(List<AsmNode> nodes, LirOpInstruction op)
    {
        string operatorName = op.Opcode["unary.".Length..];
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);

        if (!Enum.TryParse<BoundUnaryOperatorKind>(operatorName, out BoundUnaryOperatorKind kind))
        {
            nodes.Add(new AsmCommentNode($"unknown unary op: {operatorName}"));
            return;
        }

        switch (kind)
        {
            case BoundUnaryOperatorKind.Negation:
                nodes.Add(Emit("NEG", dest, src));
                break;

            case BoundUnaryOperatorKind.LogicalNot:
                nodes.Add(Emit("CMP", src, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRZ", dest));
                break;

            case BoundUnaryOperatorKind.PostIncrement:
                nodes.Add(Emit("MOV", dest, src));
                nodes.Add(Emit("ADD", src, new AsmImmediateOperand(1)));
                break;

            case BoundUnaryOperatorKind.PostDecrement:
                nodes.Add(Emit("MOV", dest, src));
                nodes.Add(Emit("SUB", src, new AsmImmediateOperand(1)));
                break;
        }
    }

    private static void LowerSelect(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand cond = OpReg(op.Operands[0]);
        AsmRegisterOperand whenTrue = OpReg(op.Operands[1]);
        AsmRegisterOperand whenFalse = OpReg(op.Operands[2]);

        nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
        nodes.Add(Emit("MOV", dest, whenFalse));
        nodes.Add(Emit("MOV", dest, whenTrue, predicate: "IF_NZ"));
    }

    private static void LowerCall(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string target = ((LirSymbolOperand)op.Operands[0]).Symbol;
        CallingConventionTier calleeTier = ctx.CalleeTiers.GetValueOrDefault(target, CallingConventionTier.General);

        // Collect argument registers
        List<AsmRegisterOperand> args = [];
        for (int i = 1; i < op.Operands.Count; i++)
            args.Add(OpReg(op.Operands[i]));

        AsmRegisterOperand? destReg = op.Destination is { } dest ? new AsmRegisterOperand(dest.Id) : null;
        AsmSymbolOperand targetOp = new(target);

        switch (calleeTier)
        {
            case CallingConventionTier.Leaf:
                // CALLPA: param in PA, result in PA
                if (args.Count > 0)
                    nodes.Add(Emit("MOV", new AsmSymbolOperand("PA"), args[0]));
                nodes.Add(Emit("CALLPA", new AsmSymbolOperand("PA"), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit("MOV", destReg, new AsmSymbolOperand("PA")));
                break;

            case CallingConventionTier.SecondOrder:
                // CALLPB: param in PB, result in PB
                if (args.Count > 0)
                    nodes.Add(Emit("MOV", new AsmSymbolOperand("PB"), args[0]));
                nodes.Add(Emit("CALLPB", new AsmSymbolOperand("PB"), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit("MOV", destReg, new AsmSymbolOperand("PB")));
                break;

            case CallingConventionTier.General:
            case CallingConventionTier.EntryPoint:
                // CALL: params in global registers, result in assigned register
                for (int i = 0; i < args.Count; i++)
                    nodes.Add(new AsmCommentNode($"arg{i} = {args[i].Format()}"));
                nodes.Add(Emit("CALL", targetOp));
                if (destReg is not null)
                    nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
                break;

            case CallingConventionTier.Recursive:
                // CALLB: push locals to hub stack via PTRB, then CALLB
                for (int i = 0; i < args.Count; i++)
                    nodes.Add(new AsmCommentNode($"arg{i} = {args[i].Format()}"));
                nodes.Add(Emit("CALL", targetOp));
                if (destReg is not null)
                    nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
                break;

            default:
                // Fallback for coro/interrupt — emit comment + generic call
                nodes.Add(new AsmCommentNode($"call ({calleeTier}) {target}"));
                nodes.Add(Emit("CALL", targetOp));
                break;
        }
    }

    private static void LowerIntrinsic(List<AsmNode> nodes, LirOpInstruction op)
    {
        string name = ((LirSymbolOperand)op.Operands[0]).Symbol;
        if (name.StartsWith('@'))
            name = name[1..];

        List<AsmOperand> operands = [];
        if (op.Destination is { } dest)
            operands.Add(new AsmRegisterOperand(dest.Id));
        for (int i = 1; i < op.Operands.Count; i++)
            operands.Add(LowerOperand(op.Operands[i]));

        nodes.Add(new AsmInstructionNode(name.ToUpperInvariant(), operands));
    }

    private static void LowerStore(List<AsmNode> nodes, LirOpInstruction op)
    {
        string target = op.Opcode["store.".Length..];

        if (op.Operands.Count == 1)
        {
            AsmOperand valueOp = LowerOperand(op.Operands[0]);
            nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand(target), valueOp]));
        }
        else if (op.Operands.Count >= 2)
        {
            AsmOperand targetOp = LowerOperand(op.Operands[0]);
            AsmOperand valueOp = LowerOperand(op.Operands[^1]);
            nodes.Add(new AsmCommentNode($"store.{target}"));
            nodes.Add(new AsmInstructionNode("MOV", [targetOp, valueOp]));
        }
    }

    private static void LowerPseudo(List<AsmNode> nodes, LirOpInstruction op)
    {
        string pseudoOp = op.Opcode["pseudo.".Length..];

        switch (pseudoOp)
        {
            case "rep.setup":
                if (op.Operands.Count >= 1)
                {
                    AsmOperand iters = LowerOperand(op.Operands[0]);
                    nodes.Add(new AsmCommentNode("REP setup: body length TBD"));
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), iters]));
                }
                break;

            case "rep.iter":
                break;

            case "repfor.setup":
                if (op.Operands.Count >= 2)
                {
                    AsmOperand end = LowerOperand(op.Operands[1]);
                    nodes.Add(new AsmCommentNode("REP-FOR setup: body length TBD"));
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), end]));
                }
                break;

            case "repfor.iter":
                break;

            case "noirq.begin":
                nodes.Add(new AsmCommentNode("noirq: body length TBD"));
                nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), new AsmImmediateOperand(1)]));
                break;

            case "noirq.end":
                break;

            default:
                nodes.Add(new AsmCommentNode($"pseudo.{pseudoOp}"));
                break;
        }
    }

    private static void LowerYield(List<AsmNode> nodes, LirOpInstruction op)
    {
        if (op.Opcode.StartsWith("yieldto:", StringComparison.Ordinal))
        {
            string target = op.Opcode["yieldto:".Length..];
            nodes.Add(new AsmCommentNode($"TODO: CALLD (yieldto {target})"));
        }
        else
        {
            nodes.Add(new AsmCommentNode("TODO: CALLD (yield)"));
        }
    }

    private static void LowerTerminator(List<AsmNode> nodes, LoweringContext ctx, LirTerminator terminator)
    {
        string functionName = ctx.Function.Name;

        switch (terminator)
        {
            case LirGotoTerminator goto_:
                EmitPhiMoves(nodes, goto_.Arguments, ctx, goto_.TargetLabel);
                nodes.Add(Emit("JMP", new AsmSymbolOperand($"{functionName}_{goto_.TargetLabel}")));
                break;

            case LirBranchTerminator branch:
                LowerBranch(nodes, ctx, branch);
                break;

            case LirReturnTerminator ret:
                LowerReturn(nodes, ctx, ret);
                break;

            case LirUnreachableTerminator:
                nodes.Add(new AsmCommentNode("unreachable"));
                EmitHaltLoop(nodes);
                break;
        }
    }

    private static void LowerReturn(List<AsmNode> nodes, LoweringContext ctx, LirReturnTerminator ret)
    {
        // Move return value to appropriate location based on CC tier
        if (ret.Values.Count > 0)
        {
            AsmOperand resultOp = LowerOperand(ret.Values[0]);

            switch (ctx.Tier)
            {
                case CallingConventionTier.Leaf:
                    // Result in PA
                    nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand("PA"), resultOp]));
                    break;

                case CallingConventionTier.SecondOrder:
                    // Result in PB
                    nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand("PB"), resultOp]));
                    break;

                default:
                    // General/Recursive: result stays in its register (caller knows which)
                    nodes.Add(new AsmCommentNode($"return value: {resultOp.Format()}"));
                    break;
            }
        }

        switch (ctx.Tier)
        {
            case CallingConventionTier.EntryPoint:
                // Entry point "returns" by halting: endless loop with interrupts shielded
                EmitHaltLoop(nodes);
                break;

            case CallingConventionTier.Recursive:
                nodes.Add(Emit("RET"));
                break;

            case CallingConventionTier.Interrupt:
                FunctionKind kind = ctx.Function.Kind;
                string retInsn = kind switch
                {
                    FunctionKind.Int1 => "RETI1",
                    FunctionKind.Int2 => "RETI2",
                    FunctionKind.Int3 => "RETI3",
                    _ => "RET",
                };
                nodes.Add(Emit(retInsn));
                break;

            default:
                nodes.Add(Emit("RET"));
                break;
        }
    }

    /// <summary>
    /// Emit an endless halt loop: REP #1, #0 followed by NOP.
    /// REP #1, #0 repeats the next 1 instruction forever (count=0 means infinite).
    /// This keeps the COG alive without executing real work.
    /// </summary>
    private static void EmitHaltLoop(List<AsmNode> nodes)
    {
        nodes.Add(new AsmCommentNode("halt: endless loop"));
        nodes.Add(new AsmInstructionNode("REP",
            [new AsmImmediateOperand(1), new AsmImmediateOperand(0)]));
        nodes.Add(Emit("NOP"));
    }

    private static void LowerBranch(List<AsmNode> nodes, LoweringContext ctx, LirBranchTerminator branch)
    {
        AsmRegisterOperand cond = OpReg(branch.Condition);
        string functionName = ctx.Function.Name;
        string trueLabel = $"{functionName}_{branch.TrueLabel}";
        string falseLabel = $"{functionName}_{branch.FalseLabel}";

        if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
        {
            nodes.Add(Emit("TJZ", cond, new AsmSymbolOperand(falseLabel)));
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
        else
        {
            nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));

            // False path (Z=1, condition was zero)
            EmitPhiMovesConditioned(nodes, branch.FalseArguments, ctx, branch.FalseLabel, "IF_Z");
            nodes.Add(Emit("JMP", new AsmSymbolOperand(falseLabel), predicate: "IF_Z"));

            // True path (fall-through when NZ)
            EmitPhiMoves(nodes, branch.TrueArguments, ctx, branch.TrueLabel);
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
    }

    /// <summary>
    /// Emit MOV instructions for SSA φ-arguments (block parameter passing).
    /// Maps arguments to the target block's parameter registers.
    /// </summary>
    private static void EmitPhiMoves(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        LoweringContext ctx,
        string targetLabel,
        string? predicate = null)
    {
        if (arguments.Count == 0)
            return;

        IReadOnlyList<LirBlockParameter>? targetParams = null;
        ctx.BlockParams.TryGetValue(targetLabel, out targetParams);

        for (int i = 0; i < arguments.Count; i++)
        {
            AsmOperand src = LowerOperand(arguments[i]);

            if (targetParams is not null && i < targetParams.Count)
            {
                AsmRegisterOperand paramReg = new(targetParams[i].Register.Id);
                nodes.Add(new AsmInstructionNode("MOV", [paramReg, src], predicate));
            }
            else
            {
                // Fallback: emit as comment if we can't resolve target param
                string prefix = predicate is not null ? $"{predicate} " : "";
                nodes.Add(new AsmCommentNode($"{prefix}phi[{i}] = {src.Format()} -> {ctx.Function.Name}_{targetLabel}"));
            }
        }
    }

    private static void EmitPhiMovesConditioned(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        LoweringContext ctx,
        string targetLabel,
        string predicate)
    {
        EmitPhiMoves(nodes, arguments, ctx, targetLabel, predicate);
    }

    // --- Helpers ---

    private static AsmRegisterOperand DestReg(LirOpInstruction op)
    {
        if (op.Destination is not { } dest)
            throw new InvalidOperationException($"Instruction '{op.Opcode}' expected a destination register");
        return new AsmRegisterOperand(dest.Id);
    }

    private static AsmRegisterOperand OpReg(LirOperand operand)
    {
        if (operand is LirRegisterOperand reg)
            return new AsmRegisterOperand(reg.Register.Id);
        throw new InvalidOperationException($"Expected register operand, got {operand.GetType().Name}");
    }

    private static AsmOperand LowerOperand(LirOperand operand)
    {
        return operand switch
        {
            LirRegisterOperand reg => new AsmRegisterOperand(reg.Register.Id),
            LirImmediateOperand imm => new AsmImmediateOperand(GetImmediateValue(imm)),
            LirSymbolOperand sym => new AsmSymbolOperand(sym.Symbol),
            _ => throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}"),
        };
    }

    private static long GetImmediateValue(LirOperand operand)
    {
        if (operand is LirImmediateOperand imm)
            return GetImmediateValue(imm);
        throw new InvalidOperationException($"Expected immediate operand, got {operand.GetType().Name}");
    }

    private static long GetImmediateValue(LirImmediateOperand imm)
    {
        return imm.Value switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            int i => i,
            uint u => u,
            long l => l,
            ulong u => (long)u,
            byte b => b,
            sbyte s => s,
            short s => s,
            ushort u => u,
            _ => Convert.ToInt64(imm.Value),
        };
    }

    private static AsmInstructionNode Emit(
        string opcode,
        params AsmOperand[] operands)
    {
        return new AsmInstructionNode(opcode, operands);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmOperand op1,
        AsmOperand op2,
        string? predicate = null,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1, op2], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmOperand op1,
        string? predicate = null,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [], null, flagEffect);
    }
}
