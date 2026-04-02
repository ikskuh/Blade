using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Asm;

/// <summary>
/// A basic block in the intra-function control flow graph.
/// </summary>
public sealed class BasicBlock
{
    public BasicBlock(int startIndex, int endIndex)
    {
        StartIndex = startIndex;
        EndIndex = endIndex;
    }

    /// <summary>Inclusive start index into the function's node list.</summary>
    public int StartIndex { get; }

    /// <summary>Exclusive end index into the function's node list.</summary>
    public int EndIndex { get; }

    public Collection<int> SuccessorBlockIndices { get; } = new();
    public HashSet<VirtualAsmRegister> Defs { get; } = [];
    public HashSet<VirtualAsmRegister> Uses { get; } = [];
    public HashSet<VirtualAsmRegister> LiveIn { get; } = [];
    public HashSet<VirtualAsmRegister> LiveOut { get; } = [];

    /// <summary>
    /// Set of call instruction indices (into the function's node list)
    /// contained in this block.
    /// </summary>
    public Collection<int> CallIndices { get; } = new();
}

/// <summary>
/// Result of liveness analysis for a single function.
/// </summary>
public sealed class FunctionLiveness
{
    public FunctionLiveness(
        AsmFunction function,
        IReadOnlyList<BasicBlock> blocks,
        IReadOnlyDictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> interferenceGraph,
        HashSet<VirtualAsmRegister> liveAcrossCallRegisters,
        IReadOnlyDictionary<int, HashSet<VirtualAsmRegister>> liveRegistersByCallInstruction,
        IReadOnlyDictionary<int, HashSet<VirtualAsmRegister>> liveRegistersAfterInstruction)
    {
        Function = Requires.NotNull(function);
        Blocks = blocks;
        InterferenceGraph = interferenceGraph;
        LiveAcrossCallRegisters = liveAcrossCallRegisters;
        LiveRegistersByCallInstruction = liveRegistersByCallInstruction;
        LiveRegistersAfterInstruction = liveRegistersAfterInstruction;
    }

    public AsmFunction Function { get; }
    public IReadOnlyList<BasicBlock> Blocks { get; }

    /// <summary>
    /// Interference graph: register ID -> set of register IDs that interfere.
    /// Two registers interfere if they are simultaneously live at any program point.
    /// </summary>
    public IReadOnlyDictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> InterferenceGraph { get; }

    /// <summary>
    /// Set of virtual register IDs that are live across at least one call instruction.
    /// These registers must not share slots with the called function's registers.
    /// </summary>
    public HashSet<VirtualAsmRegister> LiveAcrossCallRegisters { get; }

    /// <summary>
    /// Per-call-site live set captured immediately before the call instruction executes.
    /// </summary>
    public IReadOnlyDictionary<int, HashSet<VirtualAsmRegister>> LiveRegistersByCallInstruction { get; }

    /// <summary>
    /// Per-instruction live-out set after each instruction executes.
    /// </summary>
    public IReadOnlyDictionary<int, HashSet<VirtualAsmRegister>> LiveRegistersAfterInstruction { get; }
}

/// <summary>
/// Performs intra-function liveness analysis on ASMIR, producing an interference graph
/// and identifying registers that are live across call instructions.
/// </summary>
public static class LivenessAnalyzer
{
    public static FunctionLiveness Analyze(AsmFunction function)
    {
        Requires.NotNull(function);

        IReadOnlyList<AsmNode> nodes = function.Nodes;

        // Step 1: Build basic blocks and CFG
        List<BasicBlock> blocks = BuildBasicBlocks(nodes);
        Dictionary<ControlFlowLabelSymbol, int> labelToBlock = BuildLabelMap(nodes, blocks);
        BuildCfgEdges(nodes, blocks, labelToBlock);

        // Step 2: Compute defs and uses per block
        ComputeDefsAndUses(nodes, blocks);

        // Step 3: Iterative backward dataflow
        ComputeLiveness(blocks);

        // Step 4: Build interference graph from instruction-level liveness
        (Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> interference, HashSet<VirtualAsmRegister> liveAcrossCall, Dictionary<int, HashSet<VirtualAsmRegister>> liveByCall, Dictionary<int, HashSet<VirtualAsmRegister>> liveAfterInstruction) =
            BuildInterferenceGraph(nodes, blocks);

        return new FunctionLiveness(function, blocks, interference, liveAcrossCall, liveByCall, liveAfterInstruction);
    }

    private static List<BasicBlock> BuildBasicBlocks(IReadOnlyList<AsmNode> nodes)
    {
        // Identify leaders: first node, labels, instruction after a control flow instruction
        HashSet<int> leaders = [0];

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is AsmLabelNode)
            {
                leaders.Add(i);
            }
            else if (nodes[i] is AsmInstructionNode instruction
                     && P2InstructionMetadata.TryGetInstructionForm(instruction.Mnemonic, instruction.Operands.Count, out P2InstructionFormInfo form)
                     && (form.IsBranch || form.IsReturn))
            {
                if (i + 1 < nodes.Count)
                    leaders.Add(i + 1);
            }
        }

        List<int> sortedLeaders = leaders.Order().ToList();
        List<BasicBlock> blocks = new(sortedLeaders.Count);

        for (int i = 0; i < sortedLeaders.Count; i++)
        {
            int start = sortedLeaders[i];
            int end = i + 1 < sortedLeaders.Count ? sortedLeaders[i + 1] : nodes.Count;
            blocks.Add(new BasicBlock(start, end));
        }

        return blocks;
    }

    private static Dictionary<ControlFlowLabelSymbol, int> BuildLabelMap(
        IReadOnlyList<AsmNode> nodes,
        List<BasicBlock> blocks)
    {
        Dictionary<ControlFlowLabelSymbol, int> labelToBlock = [];
        for (int b = 0; b < blocks.Count; b++)
        {
            for (int i = blocks[b].StartIndex; i < blocks[b].EndIndex; i++)
            {
                if (nodes[i] is AsmLabelNode label)
                    labelToBlock[label.Label] = b;
            }
        }
        return labelToBlock;
    }

    private static void BuildCfgEdges(
        IReadOnlyList<AsmNode> nodes,
        List<BasicBlock> blocks,
        Dictionary<ControlFlowLabelSymbol, int> labelToBlock)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            BasicBlock block = blocks[b];
            AsmInstructionNode? lastInstruction = null;

            for (int i = block.EndIndex - 1; i >= block.StartIndex; i--)
            {
                if (nodes[i] is AsmInstructionNode inst)
                {
                    lastInstruction = inst;
                    break;
                }
            }

            if (lastInstruction is null)
            {
                // Block has no instructions (just labels/comments) — falls through
                if (b + 1 < blocks.Count)
                    block.SuccessorBlockIndices.Add(b + 1);
                continue;
            }

            bool hasForm = P2InstructionMetadata.TryGetInstructionForm(
                lastInstruction.Mnemonic,
                lastInstruction.Operands.Count,
                out P2InstructionFormInfo lastInstructionForm);
            bool isBranch = hasForm && lastInstructionForm.IsBranch;
            bool isReturn = hasForm && lastInstructionForm.IsReturn;

            if (isReturn)
            {
                // No successors
                continue;
            }

            if (isBranch)
            {
                // Find branch target
                AsmSymbolOperand? target = hasForm
                    ? FindImmediateSymbolTarget(lastInstruction)
                    : null;

                if (target is { Symbol: ControlFlowLabelSymbol label } && labelToBlock.TryGetValue(label, out int targetBlock))
                    block.SuccessorBlockIndices.Add(targetBlock);

                // Unconditional JMP without predicate has no fall-through
                bool isUnconditionalJump = lastInstruction.Mnemonic == P2Mnemonic.JMP && lastInstruction.Condition is null;
                if (!isUnconditionalJump && b + 1 < blocks.Count)
                    block.SuccessorBlockIndices.Add(b + 1);
            }
            else
            {
                // Regular instruction — falls through
                if (b + 1 < blocks.Count)
                    block.SuccessorBlockIndices.Add(b + 1);
            }
        }
    }

    private static void ComputeDefsAndUses(IReadOnlyList<AsmNode> nodes, List<BasicBlock> blocks)
    {
        foreach (BasicBlock block in blocks)
        {
            for (int i = block.StartIndex; i < block.EndIndex; i++)
            {
                switch (nodes[i])
                {
                    case AsmInstructionNode instruction:
                        ProcessInstruction(instruction, block);
                        break;
                }
            }
        }
    }

    private static void ProcessInstruction(AsmInstructionNode instruction, BasicBlock block)
    {
        if (P2InstructionMetadata.HasNoRegisterEffect(instruction.Mnemonic, instruction.Operands.Count))
            return;

        List<VirtualAsmRegister> defs = [];
        List<VirtualAsmRegister> uses = [];
        ExtractInstructionDefsUses(instruction, defs, uses);

        foreach (VirtualAsmRegister register in uses)
            AddUse(block, register);

        foreach (VirtualAsmRegister register in defs)
            AddDef(block, register);
    }

    private static void AddUse(BasicBlock block, VirtualAsmRegister register)
    {
        // A use is only recorded if the register was not already defined in this block
        // (reaching definition from this block shadows the incoming value)
        if (!block.Defs.Contains(register))
            block.Uses.Add(register);
    }

    private static void AddDef(BasicBlock block, VirtualAsmRegister register)
    {
        block.Defs.Add(register);
    }

    private static void ComputeLiveness(List<BasicBlock> blocks)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            // Process blocks in reverse order for faster convergence
            for (int b = blocks.Count - 1; b >= 0; b--)
            {
                BasicBlock block = blocks[b];

                // LiveOut = union of LiveIn of all successors
                foreach (int succIdx in block.SuccessorBlockIndices)
                {
                    foreach (VirtualAsmRegister reg in blocks[succIdx].LiveIn)
                    {
                        if (block.LiveOut.Add(reg))
                            changed = true;
                    }
                }

                // LiveIn = Uses union (LiveOut - Defs)
                foreach (VirtualAsmRegister reg in block.Uses)
                {
                    if (block.LiveIn.Add(reg))
                        changed = true;
                }

                foreach (VirtualAsmRegister reg in block.LiveOut)
                {
                    if (!block.Defs.Contains(reg))
                    {
                        if (block.LiveIn.Add(reg))
                            changed = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds the interference graph by walking instructions within each block,
    /// maintaining a precise live set. Also identifies registers live across calls.
    /// </summary>
    private static (
        Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> Interference,
        HashSet<VirtualAsmRegister> LiveAcrossCall,
        Dictionary<int, HashSet<VirtualAsmRegister>> LiveByCallInstruction,
        Dictionary<int, HashSet<VirtualAsmRegister>> LiveAfterInstruction)
        BuildInterferenceGraph(IReadOnlyList<AsmNode> nodes, List<BasicBlock> blocks)
    {
        Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> interference = [];
        HashSet<VirtualAsmRegister> liveAcrossCall = [];
        Dictionary<int, HashSet<VirtualAsmRegister>> liveByCallInstruction = [];
        Dictionary<int, HashSet<VirtualAsmRegister>> liveAfterInstruction = [];

        foreach (BasicBlock block in blocks)
        {
            // Start with LiveOut and walk backwards
            HashSet<VirtualAsmRegister> live = new(block.LiveOut);

            for (int i = block.EndIndex - 1; i >= block.StartIndex; i--)
            {
                switch (nodes[i])
                {
                    case AsmInstructionNode instruction:
                    {
                        if (P2InstructionMetadata.HasNoRegisterEffect(instruction.Mnemonic, instruction.Operands.Count))
                            continue;

                        liveAfterInstruction[i] = [.. live];

                        // If this is a call, all currently-live registers are live across it
                        if (P2InstructionMetadata.IsCall(instruction.Mnemonic, instruction.Operands.Count))
                        {
                            liveByCallInstruction[i] = [.. live];
                            foreach (VirtualAsmRegister reg in live)
                                liveAcrossCall.Add(reg);
                        }

                        // Extract defs and uses for this single instruction
                        List<VirtualAsmRegister> defs = [];
                        List<VirtualAsmRegister> uses = [];
                        ExtractInstructionDefsUses(instruction, defs, uses);

                        // Remove defs from live set (unless predicated — conditional def)
                        if (instruction.Condition is null)
                        {
                            foreach (VirtualAsmRegister def in defs)
                                live.Remove(def);
                        }

                        // All defs interfere with everything currently live
                        foreach (VirtualAsmRegister def in defs)
                        {
                            EnsureNode(interference, def);
                            foreach (VirtualAsmRegister other in live)
                            {
                                if (other != def)
                                {
                                    AddEdge(interference, def, other);
                                }
                            }
                        }

                        // Phi moves are parallel copies at control-flow edges. Their source
                        // registers must remain distinct from the other values already live
                        // across the same phi-move bundle, even though they are only uses.
                        if (instruction.IsPhiMove)
                        {
                            foreach (VirtualAsmRegister use in uses)
                            {
                                EnsureNode(interference, use);
                                foreach (VirtualAsmRegister other in live)
                                {
                                    if (other != use)
                                        AddEdge(interference, use, other);
                                }
                            }
                        }

                        // Add uses to live set
                        foreach (VirtualAsmRegister use in uses)
                            live.Add(use);

                        break;
                    }
                }
            }
        }

        return (interference, liveAcrossCall, liveByCallInstruction, liveAfterInstruction);
    }

    private static void ExtractInstructionDefsUses(
        AsmInstructionNode instruction,
        List<VirtualAsmRegister> defs,
        List<VirtualAsmRegister> uses)
    {
        bool isPredicated = instruction.Condition is not null;

        if (instruction.Operands.Count == 0)
            return;

        if (!P2InstructionMetadata.TryGetInstructionForm(
                instruction.Mnemonic,
                instruction.Operands.Count,
                out _))
        {
            foreach (AsmOperand operand in instruction.Operands)
            {
                if (operand is AsmRegisterOperand reg)
                {
                    uses.Add(reg.Register);
                    defs.Add(reg.Register);
                }
            }
            return;
        }

        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            if (instruction.Operands[operandIndex] is not AsmRegisterOperand register)
                continue;

            P2OperandAccess access = P2InstructionMetadata.GetOperandAccess(
                instruction.Mnemonic,
                instruction.Operands.Count,
                operandIndex);

            if (access is P2OperandAccess.Read or P2OperandAccess.ReadWrite)
                uses.Add(register.Register);

            if (access is P2OperandAccess.Write or P2OperandAccess.ReadWrite)
            {
                if (isPredicated && access == P2OperandAccess.Write)
                    uses.Add(register.Register);

                defs.Add(register.Register);
            }
        }
    }

    private static AsmSymbolOperand? FindImmediateSymbolTarget(AsmInstructionNode instruction)
    {
        for (int operandIndex = instruction.Operands.Count - 1; operandIndex >= 0; operandIndex--)
        {
            if (!P2InstructionMetadata.UsesImmediateSymbolSyntax(instruction.Mnemonic, instruction.Operands.Count, operandIndex))
                continue;

            return instruction.Operands[operandIndex] as AsmSymbolOperand;
        }

        return null;
    }

    private static void EnsureNode(Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> graph, VirtualAsmRegister register)
    {
        if (!graph.ContainsKey(register))
            graph[register] = [];
    }

    private static void AddEdge(Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> graph, VirtualAsmRegister a, VirtualAsmRegister b)
    {
        if (!graph.TryGetValue(a, out HashSet<VirtualAsmRegister>? neighborsA))
        {
            neighborsA = [];
            graph[a] = neighborsA;
        }
        neighborsA.Add(b);

        if (!graph.TryGetValue(b, out HashSet<VirtualAsmRegister>? neighborsB))
        {
            neighborsB = [];
            graph[b] = neighborsB;
        }
        neighborsB.Add(a);
    }
}
