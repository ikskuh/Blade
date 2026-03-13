using System;
using System.Collections.Generic;
using System.Linq;

namespace Blade.IR.Asm;

/// <summary>
/// Maps virtual registers (%rN) to labeled P2 COG registers.
///
/// On the P2, code and data share the same 512×32-bit COG register file ($000–$1FF).
/// Instructions occupy the low addresses, data/variables occupy addresses after code.
/// Register allocation must run after all code has been generated so we know the
/// code size and can place data registers above it.
///
/// Registers are emitted as labeled LONGs (e.g., "fn_foo_arg0 LONG 0") so FlexSpin
/// can resolve them and the output is human-readable.
/// </summary>
public static class RegisterAllocator
{
    /// <summary>
    /// P2 special register addresses. Reserved — cannot be used for allocation.
    /// </summary>
    private static class P2Registers
    {
        public const int IJMP3 = 0x1F0;
        public const int IRET3 = 0x1F1;
        public const int IJMP2 = 0x1F2;
        public const int IRET2 = 0x1F3;
        public const int IJMP1 = 0x1F4;
        public const int IRET1 = 0x1F5;
        public const int PA = 0x1F6;
        public const int PB = 0x1F7;
        public const int PTRA = 0x1F8;
        public const int PTRB = 0x1F9;
        public const int DIRA = 0x1FA;
        public const int DIRB = 0x1FB;
        public const int OUTA = 0x1FC;
        public const int OUTB = 0x1FD;
        public const int INA = 0x1FE;
        public const int INB = 0x1FF;

        public const int LastUsable = 0x1EF; // 496 general purpose
    }

    /// <summary>
    /// Allocate registers for all functions in the module.
    /// Virtual registers and symbol references are replaced with labeled register names.
    /// Register definitions (LONG directives) are appended after all code.
    /// </summary>
    public static AsmModule Allocate(AsmModule module)
    {
        // Phase 1: Collect all virtual registers and variable symbols across the whole program.
        // Each gets a unique label name.
        Dictionary<string, string> symbolLabels = []; // variable symbol → label name
        Dictionary<string, Dictionary<int, string>> functionVirtLabels = []; // fn → virtId → label

        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, string> virtLabels = [];

            foreach (AsmNode node in function.Nodes)
            {
                if (node is not AsmInstructionNode instruction)
                    continue;

                foreach (AsmOperand operand in instruction.Operands)
                {
                    switch (operand)
                    {
                        case AsmRegisterOperand reg:
                            if (!virtLabels.ContainsKey(reg.RegisterId))
                            {
                                string label = SanitizeLabel($"{function.Name}_r{reg.RegisterId}");
                                virtLabels[reg.RegisterId] = label;
                            }
                            break;

                        case AsmSymbolOperand sym:
                            if (!IsSpecialSymbol(sym.Name)
                                && !IsLabelInModule(sym.Name, module)
                                && !symbolLabels.ContainsKey(sym.Name))
                            {
                                string label = SanitizeLabel($"var_{sym.Name}");
                                symbolLabels[sym.Name] = label;
                            }
                            break;
                    }
                }
            }

            functionVirtLabels[function.Name] = virtLabels;
        }

        // Phase 2: Rewrite instructions to use label names instead of virtual registers.
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, string> virtLabels = functionVirtLabels[function.Name];
            List<AsmNode> newNodes = new(function.Nodes.Count);

            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction)
                {
                    List<AsmOperand> newOperands = new(instruction.Operands.Count);
                    foreach (AsmOperand operand in instruction.Operands)
                        newOperands.Add(RewriteOperand(operand, virtLabels, symbolLabels, module));
                    newNodes.Add(new AsmInstructionNode(
                        instruction.Opcode, newOperands, instruction.Predicate, instruction.FlagEffect));
                }
                else
                {
                    newNodes.Add(node);
                }
            }

            functions.Add(new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, newNodes));
        }

        // Phase 3: Append register definitions as labeled LONGs after the last function.
        // These will be placed after code in the COG address space.
        // FlexSpin handles the address assignment.
        if (functions.Count > 0)
        {
            // Find entry point function (or last function) to append data
            int targetIdx = functions.Count - 1;
            for (int i = 0; i < functions.Count; i++)
            {
                if (functions[i].IsEntryPoint)
                {
                    targetIdx = i;
                    break;
                }
            }

            AsmFunction target = functions[targetIdx];
            List<AsmNode> extendedNodes = new(target.Nodes);

            // Collect all unique register labels
            HashSet<string> emitted = [];
            extendedNodes.Add(new AsmCommentNode("--- register file ---"));

            // Variable symbols first
            foreach ((string _, string label) in symbolLabels.OrderBy(kv => kv.Value))
            {
                if (emitted.Add(label))
                {
                    extendedNodes.Add(new AsmLabelNode(label));
                    extendedNodes.Add(new AsmDirectiveNode("LONG 0"));
                }
            }

            // Virtual registers per function
            foreach (AsmFunction fn in functions)
            {
                if (!functionVirtLabels.TryGetValue(fn.Name, out Dictionary<int, string>? virtLabels))
                    continue;

                foreach ((int _, string label) in virtLabels.OrderBy(kv => kv.Key))
                {
                    if (emitted.Add(label))
                    {
                        extendedNodes.Add(new AsmLabelNode(label));
                        extendedNodes.Add(new AsmDirectiveNode("LONG 0"));
                    }
                }
            }

            functions[targetIdx] = new AsmFunction(
                target.Name, target.IsEntryPoint, target.CcTier, extendedNodes);
        }

        return new AsmModule(functions);
    }

    private static AsmOperand RewriteOperand(
        AsmOperand operand,
        Dictionary<int, string> virtLabels,
        Dictionary<string, string> symbolLabels,
        AsmModule module)
    {
        switch (operand)
        {
            case AsmRegisterOperand reg:
                if (virtLabels.TryGetValue(reg.RegisterId, out string? regLabel))
                    return new AsmSymbolOperand(regLabel);
                return reg;

            case AsmSymbolOperand sym:
                if (IsSpecialRegisterName(sym.Name))
                    return sym; // PA, PB, etc. stay as-is — FlexSpin knows them
                if (symbolLabels.TryGetValue(sym.Name, out string? symLabel))
                    return new AsmSymbolOperand(symLabel);
                return sym; // Labels, function names, "$" stay as-is

            default:
                return operand;
        }
    }

    private static string SanitizeLabel(string name)
    {
        // Replace characters that aren't valid in PASM2 labels
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        }
        return new string(chars);
    }

    private static bool IsSpecialSymbol(string name)
    {
        return name == "$" || IsSpecialRegisterName(name);
    }

    private static bool IsSpecialRegisterName(string name)
    {
        return name is "PA" or "PB" or "PTRA" or "PTRB"
            or "DIRA" or "DIRB" or "OUTA" or "OUTB" or "INA" or "INB"
            or "IJMP1" or "IRET1" or "IJMP2" or "IRET2" or "IJMP3" or "IRET3";
    }

    private static bool IsLabelInModule(string name, AsmModule module)
    {
        foreach (AsmFunction function in module.Functions)
        {
            if (function.Name == name)
                return true;
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmLabelNode label && label.Name == name)
                    return true;
            }
        }
        return false;
    }
}
