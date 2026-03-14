using System.Collections.Generic;
using System.Linq;

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

    public List<int> SuccessorBlockIndices { get; } = [];
    public HashSet<int> Defs { get; } = [];
    public HashSet<int> Uses { get; } = [];
    public HashSet<int> LiveIn { get; } = [];
    public HashSet<int> LiveOut { get; } = [];

    /// <summary>
    /// Set of call instruction indices (into the function's node list)
    /// contained in this block.
    /// </summary>
    public List<int> CallIndices { get; } = [];
}

/// <summary>
/// Result of liveness analysis for a single function.
/// </summary>
public sealed class FunctionLiveness
{
    public FunctionLiveness(
        string functionName,
        IReadOnlyList<BasicBlock> blocks,
        IReadOnlyDictionary<int, HashSet<int>> interferenceGraph,
        HashSet<int> liveAcrossCallRegisters)
    {
        FunctionName = functionName;
        Blocks = blocks;
        InterferenceGraph = interferenceGraph;
        LiveAcrossCallRegisters = liveAcrossCallRegisters;
    }

    public string FunctionName { get; }
    public IReadOnlyList<BasicBlock> Blocks { get; }

    /// <summary>
    /// Interference graph: register ID -> set of register IDs that interfere.
    /// Two registers interfere if they are simultaneously live at any program point.
    /// </summary>
    public IReadOnlyDictionary<int, HashSet<int>> InterferenceGraph { get; }

    /// <summary>
    /// Set of virtual register IDs that are live across at least one call instruction.
    /// These registers must not share slots with the called function's registers.
    /// </summary>
    public HashSet<int> LiveAcrossCallRegisters { get; }
}

/// <summary>
/// Performs intra-function liveness analysis on ASMIR, producing an interference graph
/// and identifying registers that are live across call instructions.
/// </summary>
public static class LivenessAnalyzer
{
    public static FunctionLiveness Analyze(AsmFunction function)
    {
        IReadOnlyList<AsmNode> nodes = function.Nodes;

        // Step 1: Build basic blocks and CFG
        List<BasicBlock> blocks = BuildBasicBlocks(nodes);
        Dictionary<string, int> labelToBlock = BuildLabelMap(nodes, blocks);
        BuildCfgEdges(nodes, blocks, labelToBlock);

        // Step 2: Compute defs and uses per block
        ComputeDefsAndUses(nodes, blocks);

        // Step 3: Iterative backward dataflow
        ComputeLiveness(blocks);

        // Step 4: Build interference graph from instruction-level liveness
        (Dictionary<int, HashSet<int>> interference, HashSet<int> liveAcrossCall) =
            BuildInterferenceGraph(nodes, blocks);

        return new FunctionLiveness(function.Name, blocks, interference, liveAcrossCall);
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
            else if (nodes[i] is AsmInstructionNode instruction &&
                     (P2OpcodeInfo.IsBranch(instruction.Opcode) || P2OpcodeInfo.IsReturn(instruction.Opcode)))
            {
                if (i + 1 < nodes.Count)
                    leaders.Add(i + 1);
            }
        }

        List<int> sortedLeaders = leaders.OrderBy(x => x).ToList();
        List<BasicBlock> blocks = new(sortedLeaders.Count);

        for (int i = 0; i < sortedLeaders.Count; i++)
        {
            int start = sortedLeaders[i];
            int end = i + 1 < sortedLeaders.Count ? sortedLeaders[i + 1] : nodes.Count;
            blocks.Add(new BasicBlock(start, end));
        }

        return blocks;
    }

    private static Dictionary<string, int> BuildLabelMap(
        IReadOnlyList<AsmNode> nodes,
        List<BasicBlock> blocks)
    {
        Dictionary<string, int> labelToBlock = [];
        for (int b = 0; b < blocks.Count; b++)
        {
            for (int i = blocks[b].StartIndex; i < blocks[b].EndIndex; i++)
            {
                if (nodes[i] is AsmLabelNode label)
                    labelToBlock[label.Name] = b;
            }
        }
        return labelToBlock;
    }

    private static void BuildCfgEdges(
        IReadOnlyList<AsmNode> nodes,
        List<BasicBlock> blocks,
        Dictionary<string, int> labelToBlock)
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

            bool isBranch = P2OpcodeInfo.IsBranch(lastInstruction.Opcode);
            bool isReturn = P2OpcodeInfo.IsReturn(lastInstruction.Opcode);

            if (isReturn)
            {
                // No successors
                continue;
            }

            if (isBranch)
            {
                // Find branch target
                AsmSymbolOperand? target = lastInstruction.Operands
                    .OfType<AsmSymbolOperand>()
                    .FirstOrDefault();

                if (target is not null && labelToBlock.TryGetValue(target.Name, out int targetBlock))
                    block.SuccessorBlockIndices.Add(targetBlock);

                // Unconditional JMP without predicate has no fall-through
                bool isUnconditionalJump = lastInstruction.Opcode == "JMP" && lastInstruction.Predicate is null;
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

                    case AsmImplicitUseNode implicitUse:
                        foreach (AsmOperand operand in implicitUse.Operands)
                        {
                            if (operand is AsmRegisterOperand reg)
                                AddUse(block, reg.RegisterId);
                        }
                        break;

                    case AsmInlineTextNode inlineText:
                        // Conservative: all bindings are both defs and uses
                        foreach (AsmOperand operand in inlineText.Bindings.Values)
                        {
                            if (operand is AsmRegisterOperand reg)
                            {
                                AddUse(block, reg.RegisterId);
                                AddDef(block, reg.RegisterId);
                            }
                        }
                        break;
                }
            }
        }
    }

    private static void ProcessInstruction(AsmInstructionNode instruction, BasicBlock block)
    {
        string opcode = instruction.Opcode;

        if (P2OpcodeInfo.HasNoRegisterEffect(opcode))
            return;

        bool isPredicated = instruction.Predicate is not null;

        if (instruction.Operands.Count == 0)
            return;

        // Handle calls specially: they may use/def specific operands
        if (P2OpcodeInfo.IsCall(opcode))
        {
            // CALLPA/CALLPB: operand[0] is read (PA/PB), operand[1] is the target (read)
            // CALL: operand[0] is the target (read)
            foreach (AsmOperand operand in instruction.Operands)
            {
                if (operand is AsmRegisterOperand reg)
                    AddUse(block, reg.RegisterId);
            }
            return;
        }

        AsmOperand dest = instruction.Operands[0];

        if (P2OpcodeInfo.IsReadOnly(opcode))
        {
            // Both operands are reads only
            foreach (AsmOperand operand in instruction.Operands)
            {
                if (operand is AsmRegisterOperand reg)
                    AddUse(block, reg.RegisterId);
            }
            return;
        }

        if (P2OpcodeInfo.IsBranch(opcode))
        {
            // Branches: TJZ/TJNZ read operand[0]; DJNZ/DJZ read-modify-write operand[0]
            if (P2OpcodeInfo.ReadsDestination(opcode) && dest is AsmRegisterOperand djReg)
            {
                AddUse(block, djReg.RegisterId);
                AddDef(block, djReg.RegisterId);
            }
            else if (dest is AsmRegisterOperand branchReg)
            {
                AddUse(block, branchReg.RegisterId);
            }
            // Target operand is a symbol, not a register
            return;
        }

        // Standard two-operand instructions
        bool definesD = P2OpcodeInfo.DefinesDestination(opcode);
        bool readsD = P2OpcodeInfo.ReadsDestination(opcode);

        // If predicated, the old value of D may survive — treat as both def and use
        if (isPredicated && definesD && dest is AsmRegisterOperand predDest)
        {
            AddUse(block, predDest.RegisterId);
        }

        // Source operands (operand[1..])
        for (int i = 1; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand srcReg)
                AddUse(block, srcReg.RegisterId);
        }

        // Destination
        if (readsD && dest is AsmRegisterOperand rmwDest)
        {
            AddUse(block, rmwDest.RegisterId);
        }

        if (definesD && dest is AsmRegisterOperand defDest)
        {
            AddDef(block, defDest.RegisterId);
        }
    }

    private static void AddUse(BasicBlock block, int registerId)
    {
        // A use is only recorded if the register was not already defined in this block
        // (reaching definition from this block shadows the incoming value)
        if (!block.Defs.Contains(registerId))
            block.Uses.Add(registerId);
    }

    private static void AddDef(BasicBlock block, int registerId)
    {
        block.Defs.Add(registerId);
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
                    foreach (int reg in blocks[succIdx].LiveIn)
                    {
                        if (block.LiveOut.Add(reg))
                            changed = true;
                    }
                }

                // LiveIn = Uses union (LiveOut - Defs)
                foreach (int reg in block.Uses)
                {
                    if (block.LiveIn.Add(reg))
                        changed = true;
                }

                foreach (int reg in block.LiveOut)
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
    private static (Dictionary<int, HashSet<int>> Interference, HashSet<int> LiveAcrossCall)
        BuildInterferenceGraph(IReadOnlyList<AsmNode> nodes, List<BasicBlock> blocks)
    {
        Dictionary<int, HashSet<int>> interference = [];
        HashSet<int> liveAcrossCall = [];

        foreach (BasicBlock block in blocks)
        {
            // Start with LiveOut and walk backwards
            HashSet<int> live = new(block.LiveOut);

            for (int i = block.EndIndex - 1; i >= block.StartIndex; i--)
            {
                switch (nodes[i])
                {
                    case AsmInstructionNode instruction:
                    {
                        string opcode = instruction.Opcode;

                        if (P2OpcodeInfo.HasNoRegisterEffect(opcode))
                            continue;

                        // If this is a call, all currently-live registers are live across it
                        if (P2OpcodeInfo.IsCall(opcode))
                        {
                            foreach (int reg in live)
                                liveAcrossCall.Add(reg);
                        }

                        // Extract defs and uses for this single instruction
                        List<int> defs = [];
                        List<int> uses = [];
                        ExtractInstructionDefsUses(instruction, defs, uses);

                        // Remove defs from live set (unless predicated — conditional def)
                        if (instruction.Predicate is null)
                        {
                            foreach (int def in defs)
                                live.Remove(def);
                        }

                        // All defs interfere with everything currently live
                        foreach (int def in defs)
                        {
                            EnsureNode(interference, def);
                            foreach (int other in live)
                            {
                                if (other != def)
                                {
                                    AddEdge(interference, def, other);
                                }
                            }
                        }

                        // Add uses to live set
                        foreach (int use in uses)
                            live.Add(use);

                        break;
                    }

                    case AsmImplicitUseNode implicitUse:
                        foreach (AsmOperand operand in implicitUse.Operands)
                        {
                            if (operand is not AsmRegisterOperand reg)
                                continue;
                            EnsureNode(interference, reg.RegisterId);
                            live.Add(reg.RegisterId);
                        }
                        break;

                    case AsmInlineTextNode inlineText:
                    {
                        foreach (AsmOperand operand in inlineText.Bindings.Values)
                        {
                            if (operand is AsmRegisterOperand reg)
                            {
                                EnsureNode(interference, reg.RegisterId);
                                // Conservative: interferes with everything live
                                foreach (int other in live)
                                {
                                    if (other != reg.RegisterId)
                                        AddEdge(interference, reg.RegisterId, other);
                                }
                                live.Add(reg.RegisterId);
                            }
                        }
                        break;
                    }
                }
            }
        }

        return (interference, liveAcrossCall);
    }

    private static void ExtractInstructionDefsUses(
        AsmInstructionNode instruction,
        List<int> defs,
        List<int> uses)
    {
        string opcode = instruction.Opcode;
        bool isPredicated = instruction.Predicate is not null;

        if (instruction.Operands.Count == 0)
            return;

        if (P2OpcodeInfo.IsCall(opcode))
        {
            foreach (AsmOperand operand in instruction.Operands)
            {
                if (operand is AsmRegisterOperand reg)
                    uses.Add(reg.RegisterId);
            }
            return;
        }

        AsmOperand dest = instruction.Operands[0];

        if (P2OpcodeInfo.IsReadOnly(opcode))
        {
            foreach (AsmOperand operand in instruction.Operands)
            {
                if (operand is AsmRegisterOperand reg)
                    uses.Add(reg.RegisterId);
            }
            return;
        }

        if (P2OpcodeInfo.IsBranch(opcode))
        {
            if (P2OpcodeInfo.ReadsDestination(opcode) && dest is AsmRegisterOperand djReg)
            {
                uses.Add(djReg.RegisterId);
                defs.Add(djReg.RegisterId);
            }
            else if (dest is AsmRegisterOperand branchReg)
            {
                uses.Add(branchReg.RegisterId);
            }
            return;
        }

        bool definesD = P2OpcodeInfo.DefinesDestination(opcode);
        bool readsD = P2OpcodeInfo.ReadsDestination(opcode);

        // Predicated: old value may survive
        if (isPredicated && definesD && dest is AsmRegisterOperand predDest)
            uses.Add(predDest.RegisterId);

        // Source operands
        for (int i = 1; i < instruction.Operands.Count; i++)
        {
            if (instruction.Operands[i] is AsmRegisterOperand srcReg)
                uses.Add(srcReg.RegisterId);
        }

        if (readsD && dest is AsmRegisterOperand rmwDest)
            uses.Add(rmwDest.RegisterId);

        if (definesD && dest is AsmRegisterOperand defDest)
            defs.Add(defDest.RegisterId);
    }

    private static void EnsureNode(Dictionary<int, HashSet<int>> graph, int id)
    {
        if (!graph.ContainsKey(id))
            graph[id] = [];
    }

    private static void AddEdge(Dictionary<int, HashSet<int>> graph, int a, int b)
    {
        if (!graph.TryGetValue(a, out HashSet<int>? neighborsA))
        {
            neighborsA = [];
            graph[a] = neighborsA;
        }
        neighborsA.Add(b);

        if (!graph.TryGetValue(b, out HashSet<int>? neighborsB))
        {
            neighborsB = [];
            graph[b] = neighborsB;
        }
        neighborsB.Add(a);
    }
}
