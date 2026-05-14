using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Asm;

/// <summary>
/// A basic block in the intra-function control flow graph.
/// </summary>
public sealed class BasicBlock(int startIndex, int endIndex)
{
    /// <summary>Inclusive start index into the function's node list.</summary>
    public int StartIndex { get; } = startIndex;

    /// <summary>Exclusive end index into the function's node list.</summary>
    public int EndIndex { get; } = endIndex;

    public Collection<int> SuccessorBlockIndices { get; } = new();
    public HashSet<VirtualAsmValue> Defs { get; } = [];
    public HashSet<VirtualAsmValue> Uses { get; } = [];
    public HashSet<VirtualAsmValue> LiveIn { get; } = [];
    public HashSet<VirtualAsmValue> LiveOut { get; } = [];

    /// <summary>
    /// Set of call instruction indices (into the function's node list)
    /// contained in this block.
    /// </summary>
    public Collection<int> CallIndices { get; } = new();
}

/// <summary>
/// Result of liveness analysis for a single function.
/// </summary>
public sealed class FunctionLiveness(
    AsmFunction function,
    IReadOnlyList<BasicBlock> blocks,
    IReadOnlyDictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> interferenceGraph,
    HashSet<VirtualAsmValue> liveAcrossCallRegisters,
    IReadOnlyDictionary<int, HashSet<VirtualAsmValue>> liveRegistersByCallInstruction,
    IReadOnlyDictionary<int, HashSet<VirtualAsmValue>> liveRegistersAfterInstruction)
{
    public AsmFunction Function { get; } = Requires.NotNull(function);
    public IReadOnlyList<BasicBlock> Blocks { get; } = blocks;

    /// <summary>
    /// Interference graph: value -> set of values that interfere.
    /// Two values interfere if they are simultaneously live at any program point.
    /// </summary>
    public IReadOnlyDictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> InterferenceGraph { get; } = interferenceGraph;

    /// <summary>
    /// Set of virtual values that are live across at least one call instruction.
    /// These values must not share slots with the called function's values.
    /// </summary>
    public HashSet<VirtualAsmValue> LiveAcrossCallRegisters { get; } = liveAcrossCallRegisters;

    /// <summary>
    /// Per-call-site live set captured immediately before the call instruction executes.
    /// </summary>
    public IReadOnlyDictionary<int, HashSet<VirtualAsmValue>> LiveRegistersByCallInstruction { get; } = liveRegistersByCallInstruction;

    /// <summary>
    /// Per-instruction live-out set after each instruction executes.
    /// </summary>
    public IReadOnlyDictionary<int, HashSet<VirtualAsmValue>> LiveRegistersAfterInstruction { get; } = liveRegistersAfterInstruction;
}

/// <summary>
/// Performs intra-function liveness analysis on ASMIR, producing an interference graph
/// and identifying values that are live across call instructions.
/// </summary>
public static class LivenessAnalyzer
{
    public static FunctionLiveness Analyze(AsmFunction function)
    {
        Requires.NotNull(function);

        IReadOnlyList<AsmNode> nodes = function.Nodes;

        List<BasicBlock> blocks = BuildBasicBlocks(nodes);
        Dictionary<ControlFlowLabelSymbol, int> labelToBlock = BuildLabelMap(nodes, blocks);
        BuildCfgEdges(nodes, blocks, labelToBlock);
        ComputeDefsAndUses(nodes, blocks);
        ComputeLiveness(blocks);

        (Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> interference, HashSet<VirtualAsmValue> liveAcrossCall, Dictionary<int, HashSet<VirtualAsmValue>> liveByCall, Dictionary<int, HashSet<VirtualAsmValue>> liveAfterInstruction) =
            BuildInterferenceGraph(nodes, blocks);

        return new FunctionLiveness(function, blocks, interference, liveAcrossCall, liveByCall, liveAfterInstruction);
    }

    private static List<BasicBlock> BuildBasicBlocks(IReadOnlyList<AsmNode> nodes)
    {
        HashSet<int> leaders = [0];

        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is AsmLabelNode)
            {
                leaders.Add(i);
            }
            else if (nodes[i] is AsmInstructionNode instruction
                     && (instruction.Form.IsBranch || instruction.Form.IsJump || instruction.Form.IsReturn))
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
                if (b + 1 < blocks.Count)
                    block.SuccessorBlockIndices.Add(b + 1);
                continue;
            }

            bool isBranch = lastInstruction.Form.IsBranch || lastInstruction.Form.IsJump;
            bool isReturn = lastInstruction.Form.IsReturn;

            if (isReturn)
                continue;

            if (isBranch)
            {
                AsmSymbolOperand? target = FindImmediateSymbolTarget(lastInstruction);

                if (target is { Symbol: ControlFlowLabelSymbol label } && labelToBlock.TryGetValue(label, out int targetBlock))
                    block.SuccessorBlockIndices.Add(targetBlock);

                bool isUnconditionalJump = lastInstruction.Mnemonic == P2Mnemonic.JMP && lastInstruction.Condition is null;
                if (!isUnconditionalJump && b + 1 < blocks.Count)
                    block.SuccessorBlockIndices.Add(b + 1);
            }
            else if (b + 1 < blocks.Count)
            {
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
                if (nodes[i] is AsmInstructionNode instruction)
                {
                    if (instruction.Form.IsCall)
                        block.CallIndices.Add(i);

                    ProcessInstruction(instruction, block);
                }
            }
        }
    }

    private static void ProcessInstruction(AsmInstructionNode instruction, BasicBlock block)
    {
        if (instruction.Form.HasNoRegisterEffect)
            return;

        List<VirtualAsmValue> defs = [];
        List<VirtualAsmValue> uses = [];
        ExtractInstructionDefsUses(instruction, defs, uses);

        foreach (VirtualAsmValue register in uses)
            AddUse(block, register);

        foreach (VirtualAsmValue register in defs)
            AddDef(block, register);
    }

    private static void AddUse(BasicBlock block, VirtualAsmValue register)
    {
        if (!block.Defs.Contains(register))
            block.Uses.Add(register);
    }

    private static void AddDef(BasicBlock block, VirtualAsmValue register)
    {
        block.Defs.Add(register);
    }

    private static void ComputeLiveness(List<BasicBlock> blocks)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = blocks.Count - 1; b >= 0; b--)
            {
                BasicBlock block = blocks[b];

                foreach (int succIdx in block.SuccessorBlockIndices)
                {
                    foreach (VirtualAsmValue reg in blocks[succIdx].LiveIn)
                    {
                        if (block.LiveOut.Add(reg))
                            changed = true;
                    }
                }

                foreach (VirtualAsmValue reg in block.Uses)
                {
                    if (block.LiveIn.Add(reg))
                        changed = true;
                }

                foreach (VirtualAsmValue reg in block.LiveOut)
                {
                    if (!block.Defs.Contains(reg) && block.LiveIn.Add(reg))
                        changed = true;
                }
            }
        }
    }

    private static (
        Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> Interference,
        HashSet<VirtualAsmValue> LiveAcrossCall,
        Dictionary<int, HashSet<VirtualAsmValue>> LiveByCallInstruction,
        Dictionary<int, HashSet<VirtualAsmValue>> LiveAfterInstruction)
        BuildInterferenceGraph(IReadOnlyList<AsmNode> nodes, List<BasicBlock> blocks)
    {
        Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> interference = [];
        HashSet<VirtualAsmValue> liveAcrossCall = [];
        Dictionary<int, HashSet<VirtualAsmValue>> liveByCallInstruction = [];
        Dictionary<int, HashSet<VirtualAsmValue>> liveAfterInstruction = [];

        foreach (BasicBlock block in blocks)
        {
            HashSet<VirtualAsmValue> live = new(block.LiveOut);

            for (int i = block.EndIndex - 1; i >= block.StartIndex; i--)
            {
                if (nodes[i] is not AsmInstructionNode instruction)
                    continue;

                if (instruction.Form.HasNoRegisterEffect)
                    continue;

                liveAfterInstruction[i] = [.. live];

                if (instruction.Form.IsCall)
                {
                    liveByCallInstruction[i] = [.. live];
                    foreach (VirtualAsmValue reg in live)
                        liveAcrossCall.Add(reg);
                }

                List<VirtualAsmValue> defs = [];
                List<VirtualAsmValue> uses = [];
                ExtractInstructionDefsUses(instruction, defs, uses);

                if (instruction.Condition is null)
                {
                    foreach (VirtualAsmValue def in defs)
                        live.Remove(def);
                }

                foreach (VirtualAsmValue def in defs)
                {
                    EnsureNode(interference, def);
                    foreach (VirtualAsmValue other in live)
                    {
                        if (other != def)
                            AddEdge(interference, def, other);
                    }
                }

                AddIntraInstructionReadWriteInterference(interference, instruction);

                if (instruction.IsPhiMove)
                {
                    foreach (VirtualAsmValue use in uses)
                    {
                        EnsureNode(interference, use);
                        foreach (VirtualAsmValue other in live)
                        {
                            if (other != use)
                                AddEdge(interference, use, other);
                        }
                    }
                }

                foreach (VirtualAsmValue use in uses)
                    live.Add(use);
            }
        }

        return (interference, liveAcrossCall, liveByCallInstruction, liveAfterInstruction);
    }

    private static void ExtractInstructionDefsUses(
        AsmInstructionNode instruction,
        List<VirtualAsmValue> defs,
        List<VirtualAsmValue> uses)
    {
        bool isPredicated = instruction.Condition is not null;

        if (instruction.FlagInput.C is not null)
            uses.Add(instruction.FlagInput.C);
        if (instruction.FlagInput.Z is not null)
            uses.Add(instruction.FlagInput.Z);

        void AddFlagDef(VirtualAsmFlag? flag)
        {
            if (flag is null)
                return;

            if (isPredicated)
                uses.Add(flag);

            defs.Add(flag);
        }

        AddFlagDef(instruction.FlagOutput.C);
        AddFlagDef(instruction.FlagOutput.Z);

        if (instruction.Operands.Count == 0)
            return;

        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            VirtualAsmValue register;
            if (instruction.Operands[operandIndex] is AsmRegisterOperand registerOperand)
                register = registerOperand.Value;
            else
                continue;

            P2OperandAccess access = instruction.Form.Operands[operandIndex].Access;

            if (access is P2OperandAccess.Read or P2OperandAccess.ReadWrite)
                uses.Add(register);

            if (access is P2OperandAccess.Write or P2OperandAccess.ReadWrite)
            {
                if (isPredicated && access == P2OperandAccess.Write)
                    uses.Add(register);

                defs.Add(register);
            }
        }
    }

    private static void AddIntraInstructionReadWriteInterference(
        Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> interference,
        AsmInstructionNode instruction)
    {
        if (instruction.Mnemonic == P2Mnemonic.MOV && instruction.Operands.Count == 2)
            return;

        List<VirtualAsmValue> explicitReads = CollectExplicitReads(instruction);
        List<VirtualAsmValue> operandReads = CollectOperandReads(instruction);
        List<VirtualAsmValue> allReads = [.. explicitReads];
        allReads.AddRange(operandReads);

        List<VirtualAsmValue> explicitWrites = [];
        if (instruction.FlagOutput.C is not null)
            explicitWrites.Add(instruction.FlagOutput.C);
        if (instruction.FlagOutput.Z is not null)
            explicitWrites.Add(instruction.FlagOutput.Z);

        foreach (VirtualAsmValue writeRegister in explicitWrites)
        {
            EnsureNode(interference, writeRegister);
            foreach (VirtualAsmValue readRegister in explicitReads)
            {
                if (readRegister != writeRegister)
                    AddEdge(interference, writeRegister, readRegister);
            }
        }

        for (int writeOperandIndex = 0; writeOperandIndex < instruction.Operands.Count; writeOperandIndex++)
        {
            VirtualAsmValue writeRegister;
            if (instruction.Operands[writeOperandIndex] is AsmRegisterOperand writeOperand)
                writeRegister = writeOperand.Value;
            else
                continue;

            P2OperandAccess writeAccess = instruction.Form.Operands[writeOperandIndex].Access;
            if (writeAccess is not P2OperandAccess.Write and not P2OperandAccess.ReadWrite)
                continue;

            EnsureNode(interference, writeRegister);

            for (int readOperandIndex = 0; readOperandIndex < instruction.Operands.Count; readOperandIndex++)
            {
                if (readOperandIndex == writeOperandIndex)
                    continue;

                VirtualAsmValue readRegister;
                if (instruction.Operands[readOperandIndex] is AsmRegisterOperand readOperand)
                    readRegister = readOperand.Value;
                else
                    continue;

                P2OperandAccess readAccess = instruction.Form.Operands[readOperandIndex].Access;
                if (readAccess is not P2OperandAccess.Read and not P2OperandAccess.ReadWrite)
                    continue;

                if (readRegister != writeRegister)
                    AddEdge(interference, writeRegister, readRegister);
            }

            foreach (VirtualAsmValue readRegister in explicitReads)
            {
                if (readRegister != writeRegister)
                    AddEdge(interference, writeRegister, readRegister);
            }
        }

        for (int leftIndex = 0; leftIndex < allReads.Count; leftIndex++)
        {
            VirtualAsmValue left = allReads[leftIndex];
            for (int rightIndex = leftIndex + 1; rightIndex < allReads.Count; rightIndex++)
            {
                VirtualAsmValue right = allReads[rightIndex];
                if (right != left)
                    AddEdge(interference, left, right);
            }
        }
    }

    private static List<VirtualAsmValue> CollectExplicitReads(AsmInstructionNode instruction)
    {
        List<VirtualAsmValue> explicitReads = [];
        if (instruction.FlagInput.C is not null)
            explicitReads.Add(instruction.FlagInput.C);
        if (instruction.FlagInput.Z is not null)
            explicitReads.Add(instruction.FlagInput.Z);

        return explicitReads;
    }

    private static List<VirtualAsmValue> CollectOperandReads(AsmInstructionNode instruction)
    {
        List<VirtualAsmValue> operandReads = [];
        for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            if (instruction.Operands[operandIndex] is not AsmRegisterOperand operand)
                continue;

            P2OperandAccess access = instruction.Form.Operands[operandIndex].Access;
            if (access is P2OperandAccess.Read or P2OperandAccess.ReadWrite)
                operandReads.Add(operand.Value);
        }

        return operandReads;
    }

    private static AsmSymbolOperand? FindImmediateSymbolTarget(AsmInstructionNode instruction)
    {
        for (int operandIndex = instruction.Operands.Count - 1; operandIndex >= 0; operandIndex--)
        {
            if (instruction.Form.Operands[operandIndex].Type != P2OperandType.BranchTarget)
                continue;

            return instruction.Operands[operandIndex] as AsmSymbolOperand;
        }

        return null;
    }

    private static void EnsureNode(Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> graph, VirtualAsmValue register)
    {
        if (!graph.ContainsKey(register))
            graph[register] = [];
    }

    private static void AddEdge(Dictionary<VirtualAsmValue, HashSet<VirtualAsmValue>> graph, VirtualAsmValue a, VirtualAsmValue b)
    {
        if (!graph.TryGetValue(a, out HashSet<VirtualAsmValue>? neighborsA))
        {
            neighborsA = [];
            graph[a] = neighborsA;
        }
        neighborsA.Add(b);

        if (!graph.TryGetValue(b, out HashSet<VirtualAsmValue>? neighborsB))
        {
            neighborsB = [];
            graph[b] = neighborsB;
        }
        neighborsB.Add(a);
    }
}
