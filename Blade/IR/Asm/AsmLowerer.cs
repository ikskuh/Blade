using System;
using System.Collections.Generic;
using Blade.IR.Lir;
using Blade.Semantics.Bound;

namespace Blade.IR.Asm;

/// <summary>
/// Lowers LIR (virtual registers, high-level opcodes) to ASMIR
/// (real P2 mnemonics, virtual registers, label-based jumps).
/// </summary>
public static class AsmLowerer
{
    public static AsmModule Lower(LirModule module)
    {
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            functions.Add(LowerFunction(function));
        return new AsmModule(functions);
    }

    private static AsmFunction LowerFunction(LirFunction function)
    {
        List<AsmNode> nodes = [];
        nodes.Add(new AsmDirectiveNode($"function {function.Name}"));

        foreach (LirBlock block in function.Blocks)
        {
            string blockLabel = $"{function.Name}_{block.Label}";
            nodes.Add(new AsmLabelNode(blockLabel));

            // Block parameters are register assignments, not real instructions.
            // They are resolved by φ-moves at branch/goto sites.

            foreach (LirInstruction instruction in block.Instructions)
                LowerInstruction(nodes, instruction);

            LowerTerminator(nodes, function.Name, block.Terminator);
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, nodes);
    }

    private static void LowerInstruction(List<AsmNode> nodes, LirInstruction instruction)
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
                LowerCall(nodes, op);
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
        // Type conversion between integer types. In COG mode everything is a 32-bit register.
        // For narrowing: mask/sign-extend. For widening: just MOV (upper bits are already 0
        // if the source was properly narrowed). For same-size: MOV.
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        // For now, emit a simple MOV. Sign/zero extension (ZEROX/SIGNX) will be
        // needed once we have proper type width tracking.
        nodes.Add(Emit("MOV", dest, src));
    }

    private static void LowerBinary(List<AsmNode> nodes, LirOpInstruction op)
    {
        string operatorName = op.Opcode["binary.".Length..];
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand left = OpReg(op.Operands[0]);
        AsmRegisterOperand right = OpReg(op.Operands[1]);

        // Parse the enum name from the opcode string
        if (!Enum.TryParse<BoundBinaryOperatorKind>(operatorName, out BoundBinaryOperatorKind kind))
        {
            nodes.Add(new AsmCommentNode($"unknown binary op: {operatorName}"));
            return;
        }

        switch (kind)
        {
            case BoundBinaryOperatorKind.Add:
                // MOV dest, left; ADD dest, right
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("ADD", dest, right));
                break;

            case BoundBinaryOperatorKind.Subtract:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SUB", dest, right));
                break;

            case BoundBinaryOperatorKind.Multiply:
                // CORDIC: QMUL left, right; GETQX dest
                nodes.Add(Emit("QMUL", left, right));
                nodes.Add(Emit("GETQX", dest));
                break;

            case BoundBinaryOperatorKind.Divide:
                // CORDIC: QDIV left, right; GETQX dest (quotient)
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
                // Default to unsigned shift right
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHR", dest, right));
                break;

            case BoundBinaryOperatorKind.Equals:
                // CMP left, right WZ; WRZ dest
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRZ", dest));
                break;

            case BoundBinaryOperatorKind.NotEquals:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRNZ", dest));
                break;

            case BoundBinaryOperatorKind.Less:
                // Unsigned: CMP left, right WC; WRC dest (C=borrow means left < right)
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRC", dest));
                break;

            case BoundBinaryOperatorKind.LessOrEqual:
                // left <= right  ⟺  !(right < left)
                // CMP right, left WC; WRNC dest
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRNC", dest));
                break;

            case BoundBinaryOperatorKind.Greater:
                // left > right  ⟺  right < left
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                nodes.Add(Emit("WRC", dest));
                break;

            case BoundBinaryOperatorKind.GreaterOrEqual:
                // left >= right  ⟺  !(left < right)
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
                // NEG is 2-operand non-destructive on P2
                nodes.Add(Emit("NEG", dest, src));
                break;

            case BoundUnaryOperatorKind.LogicalNot:
                // CMP src, #0 WZ; WRZ dest (zero becomes 1, nonzero becomes 0)
                nodes.Add(Emit("CMP", src, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
                nodes.Add(Emit("WRZ", dest));
                break;

            case BoundUnaryOperatorKind.PostIncrement:
                // MOV dest, src; ADD src, #1
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
        // select dest, cond, whenTrue, whenFalse
        // → CMP cond, #0 WZ; MOV dest, whenFalse; IF_NZ MOV dest, whenTrue
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand cond = OpReg(op.Operands[0]);
        AsmRegisterOperand whenTrue = OpReg(op.Operands[1]);
        AsmRegisterOperand whenFalse = OpReg(op.Operands[2]);

        nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
        nodes.Add(Emit("MOV", dest, whenFalse));
        nodes.Add(Emit("MOV", dest, whenTrue, predicate: "IF_NZ"));
    }

    private static void LowerCall(List<AsmNode> nodes, LirOpInstruction op)
    {
        // For now emit a generic CALL. Calling convention lowering (Step 2)
        // will replace this with CALLPA/CALLPB/CALL/CALLB based on FunctionKind.
        string target = ((LirSymbolOperand)op.Operands[0]).Symbol;
        AsmSymbolOperand targetOp = new(target);

        // TODO: calling convention lowering — move args to PA/PB/registers
        // For now, emit argument moves as comments
        for (int i = 1; i < op.Operands.Count; i++)
        {
            AsmRegisterOperand arg = OpReg(op.Operands[i]);
            nodes.Add(new AsmCommentNode($"arg{i - 1} = {arg.Format()}"));
        }

        nodes.Add(Emit("CALL", targetOp));

        // Move result from PA/return register to dest
        if (op.Destination is { } dest)
        {
            AsmRegisterOperand destReg = new(dest.Id);
            nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
        }
    }

    private static void LowerIntrinsic(List<AsmNode> nodes, LirOpInstruction op)
    {
        // Intrinsics map directly to P2 instructions
        string name = ((LirSymbolOperand)op.Operands[0]).Symbol;
        // Strip the leading @ from intrinsic names
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
            // store.<varname> value — store value into the variable's register.
            // At ASMIR level with virtual registers, this is a MOV from value
            // into a symbol reference (resolved by register allocation later).
            AsmOperand valueOp = LowerOperand(op.Operands[0]);
            nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand(target), valueOp]));
        }
        else if (op.Operands.Count >= 2)
        {
            // store.member / store.index / store.deref — more complex stores
            // For member: operands = [receiver, value]
            // For index:  operands = [base, index, value]
            // For deref:  operands = [pointer, value]
            // These will need hub/LUT memory operations in later passes.
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
                // REP #bodyLen, iterations — bodyLen must be patched after instruction counting.
                // For now, emit REP with the iteration count register.
                // The first operand is the iteration count.
                if (op.Operands.Count >= 1)
                {
                    AsmOperand iters = LowerOperand(op.Operands[0]);
                    // Body length placeholder — will be resolved by a later pass
                    nodes.Add(new AsmCommentNode("REP setup: body length TBD"));
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), iters]));
                }
                break;

            case "rep.iter":
                // Marks the start of the REP body — no instruction needed.
                // The hardware REP loop repeats automatically.
                break;

            case "repfor.setup":
                // rep-for: similar to rep.setup but with start/end range
                if (op.Operands.Count >= 2)
                {
                    AsmOperand start = LowerOperand(op.Operands[0]);
                    AsmOperand end = LowerOperand(op.Operands[1]);
                    nodes.Add(new AsmCommentNode("REP-FOR setup: body length TBD"));
                    // Compute iteration count: end - start
                    // Need a temp register — this is a limitation at ASMIR level
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), end]));
                }
                break;

            case "repfor.iter":
                // REP body marker — no instruction needed
                break;

            case "noirq.begin":
                // REP #bodyLen, #1 (execute once with interrupts shielded)
                nodes.Add(new AsmCommentNode("noirq: body length TBD"));
                nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), new AsmImmediateOperand(1)]));
                break;

            case "noirq.end":
                // No instruction needed — REP handles the shielding
                break;

            default:
                nodes.Add(new AsmCommentNode($"pseudo.{pseudoOp}"));
                break;
        }
    }

    private static void LowerYield(List<AsmNode> nodes, LirOpInstruction op)
    {
        // Coroutine yield/yieldto — requires CALLD instruction.
        // Full lowering deferred to calling convention pass.
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

    private static void LowerTerminator(List<AsmNode> nodes, string functionName, LirTerminator terminator)
    {
        switch (terminator)
        {
            case LirGotoTerminator goto_:
                EmitPhiMoves(nodes, goto_.Arguments, functionName, goto_.TargetLabel);
                nodes.Add(Emit("JMP", new AsmSymbolOperand($"{functionName}_{goto_.TargetLabel}")));
                break;

            case LirBranchTerminator branch:
                LowerBranch(nodes, functionName, branch);
                break;

            case LirReturnTerminator ret:
                // TODO: CC lowering will move return values to PA/PB/registers
                if (ret.Values.Count > 0)
                    nodes.Add(new AsmCommentNode($"return values: {ret.Values.Count}"));
                nodes.Add(Emit("RET"));
                break;

            case LirUnreachableTerminator:
                // Infinite loop trap — JMP to self
                nodes.Add(new AsmCommentNode("unreachable"));
                // Emit JMP #$ as a symbol reference to current address
                nodes.Add(Emit("JMP", new AsmSymbolOperand("$")));
                break;
        }
    }

    private static void LowerBranch(List<AsmNode> nodes, string functionName, LirBranchTerminator branch)
    {
        // The condition is a register holding a boolean/integer value.
        // Test it and branch: TJZ cond, #false_label (jump if zero = false)
        // Then fall through to true path.
        AsmRegisterOperand cond = OpReg(branch.Condition);
        string trueLabel = $"{functionName}_{branch.TrueLabel}";
        string falseLabel = $"{functionName}_{branch.FalseLabel}";

        // If there are no φ-moves for either branch, use simple TJZ
        if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
        {
            nodes.Add(Emit("TJZ", cond, new AsmSymbolOperand(falseLabel)));
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
        else
        {
            // With φ-moves we need: test, then conditional jumps with moves
            // CMP cond, #0 WZ
            // IF_Z: emit false φ-moves, JMP false_label
            // emit true φ-moves, JMP true_label
            nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));

            // False path (Z=1, condition was zero)
            EmitPhiMovesConditioned(nodes, branch.FalseArguments, functionName, branch.FalseLabel, "IF_Z");
            nodes.Add(Emit("JMP", new AsmSymbolOperand(falseLabel), predicate: "IF_Z"));

            // True path (fall-through when NZ)
            EmitPhiMoves(nodes, branch.TrueArguments, functionName, branch.TrueLabel);
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
    }

    /// <summary>
    /// Emit MOV instructions for SSA φ-arguments (block parameter passing).
    /// The target registers are the block parameters of the destination block.
    /// At ASMIR level, we emit MOV %rN, %rM where N is the parameter register
    /// and M is the argument register. The register allocator will handle
    /// potential conflicts (parallel moves).
    /// </summary>
    private static void EmitPhiMoves(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        string functionName,
        string targetLabel)
    {
        // φ-moves are emitted as plain MOVs. The register allocator
        // or a later pass must handle move ordering / swap conflicts.
        for (int i = 0; i < arguments.Count; i++)
        {
            AsmOperand src = LowerOperand(arguments[i]);
            // We emit a comment-style marker; the actual parameter register
            // mapping requires knowing the target block's parameter list,
            // which is a TODO for the register allocator.
            nodes.Add(new AsmCommentNode($"phi[{i}] = {src.Format()} -> {functionName}_{targetLabel}"));
        }
    }

    private static void EmitPhiMovesConditioned(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        string functionName,
        string targetLabel,
        string predicate)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            AsmOperand src = LowerOperand(arguments[i]);
            nodes.Add(new AsmCommentNode($"{predicate} phi[{i}] = {src.Format()} -> {functionName}_{targetLabel}"));
        }
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
