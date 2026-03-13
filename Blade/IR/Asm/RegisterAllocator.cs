using System;
using System.Collections.Generic;
using System.Linq;

namespace Blade.IR.Asm;

/// <summary>
/// Maps virtual registers (%rN) to physical P2 COG registers.
/// Uses a simple linear scan approach — functions are small (≤511 instructions)
/// so a sophisticated allocator is unnecessary.
/// </summary>
public static class RegisterAllocator
{
    /// <summary>
    /// P2 special register addresses. These are reserved and cannot be used
    /// for general-purpose allocation.
    /// </summary>
    private static class P2Registers
    {
        // Dual-purpose registers $1F0–$1F7
        public const int IJMP3 = 0x1F0;
        public const int IRET3 = 0x1F1;
        public const int IJMP2 = 0x1F2;
        public const int IRET2 = 0x1F3;
        public const int IJMP1 = 0x1F4;
        public const int IRET1 = 0x1F5;
        public const int PA = 0x1F6;
        public const int PB = 0x1F7;

        // I/O and pointer registers $1F8–$1FF
        public const int PTRA = 0x1F8;
        public const int PTRB = 0x1F9;
        public const int DIRA = 0x1FA;
        public const int DIRB = 0x1FB;
        public const int OUTA = 0x1FC;
        public const int OUTB = 0x1FD;
        public const int INA = 0x1FE;
        public const int INB = 0x1FF;

        // Usable general-purpose range
        public const int FirstUsable = 0x000;
        public const int LastUsable = 0x1EF; // 496 registers
    }

    /// <summary>
    /// Allocate physical registers for all functions in the module.
    /// Returns a new AsmModule with virtual registers replaced by physical ones.
    /// </summary>
    public static AsmModule Allocate(AsmModule module)
    {
        List<AsmFunction> functions = new(module.Functions.Count);

        // Collect all symbol-register mappings across functions (for global variables)
        Dictionary<string, int> symbolRegisters = [];
        int nextGlobalReg = P2Registers.FirstUsable;

        // First pass: assign registers for global symbols (variables)
        foreach (AsmFunction function in module.Functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction)
                {
                    foreach (AsmOperand operand in instruction.Operands)
                    {
                        if (operand is AsmSymbolOperand sym
                            && !IsSpecialSymbol(sym.Name)
                            && !IsLabel(sym.Name, module))
                        {
                            if (!symbolRegisters.ContainsKey(sym.Name))
                                symbolRegisters[sym.Name] = nextGlobalReg++;
                        }
                    }
                }
            }
        }

        foreach (AsmFunction function in module.Functions)
        {
            AsmFunction allocated = AllocateFunction(function, symbolRegisters, ref nextGlobalReg);
            functions.Add(allocated);
        }

        return new AsmModule(functions);
    }

    private static AsmFunction AllocateFunction(
        AsmFunction function,
        Dictionary<string, int> symbolRegisters,
        ref int nextGlobalReg)
    {
        // Collect all virtual register IDs used in this function
        HashSet<int> usedVirtRegs = [];
        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
            {
                foreach (AsmOperand operand in instruction.Operands)
                {
                    if (operand is AsmRegisterOperand reg)
                        usedVirtRegs.Add(reg.RegisterId);
                }
            }
        }

        // Simple allocation: map each virtual register to a physical register.
        // Start after global symbol registers.
        Dictionary<int, int> virtualToPhysical = [];
        int nextReg = nextGlobalReg;

        foreach (int virtReg in usedVirtRegs.OrderBy(r => r))
        {
            if (nextReg > P2Registers.LastUsable)
                throw new InvalidOperationException(
                    $"Register overflow in function '{function.Name}': " +
                    $"need more than {P2Registers.LastUsable - P2Registers.FirstUsable + 1} registers");
            virtualToPhysical[virtReg] = nextReg++;
        }

        // Rewrite nodes with physical registers
        List<AsmNode> newNodes = new(function.Nodes.Count);
        foreach (AsmNode node in function.Nodes)
        {
            switch (node)
            {
                case AsmInstructionNode instruction:
                    List<AsmOperand> newOperands = new(instruction.Operands.Count);
                    foreach (AsmOperand operand in instruction.Operands)
                        newOperands.Add(RewriteOperand(operand, virtualToPhysical, symbolRegisters));
                    newNodes.Add(new AsmInstructionNode(
                        instruction.Opcode, newOperands, instruction.Predicate, instruction.FlagEffect));
                    break;

                default:
                    newNodes.Add(node);
                    break;
            }
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, newNodes);
    }

    private static AsmOperand RewriteOperand(
        AsmOperand operand,
        Dictionary<int, int> virtualToPhysical,
        Dictionary<string, int> symbolRegisters)
    {
        switch (operand)
        {
            case AsmRegisterOperand reg:
                if (virtualToPhysical.TryGetValue(reg.RegisterId, out int physAddr))
                    return new AsmPhysicalRegisterOperand(physAddr, FormatRegisterName(physAddr));
                return reg; // Leave unmapped registers as-is

            case AsmSymbolOperand sym:
                if (IsSpecialRegisterName(sym.Name))
                    return new AsmPhysicalRegisterOperand(GetSpecialRegisterAddress(sym.Name), sym.Name);
                if (symbolRegisters.TryGetValue(sym.Name, out int symAddr))
                    return new AsmPhysicalRegisterOperand(symAddr, FormatRegisterName(symAddr));
                // Labels and unresolved symbols stay as symbols
                return sym;

            default:
                return operand;
        }
    }

    private static string FormatRegisterName(int address)
    {
        // Named special registers
        return address switch
        {
            P2Registers.IJMP3 => "IJMP3",
            P2Registers.IRET3 => "IRET3",
            P2Registers.IJMP2 => "IJMP2",
            P2Registers.IRET2 => "IRET2",
            P2Registers.IJMP1 => "IJMP1",
            P2Registers.IRET1 => "IRET1",
            P2Registers.PA => "PA",
            P2Registers.PB => "PB",
            P2Registers.PTRA => "PTRA",
            P2Registers.PTRB => "PTRB",
            P2Registers.DIRA => "DIRA",
            P2Registers.DIRB => "DIRB",
            P2Registers.OUTA => "OUTA",
            P2Registers.OUTB => "OUTB",
            P2Registers.INA => "INA",
            P2Registers.INB => "INB",
            _ => $"r{address:X3}",
        };
    }

    private static bool IsSpecialSymbol(string name)
    {
        return name == "$" || name == "PA" || name == "PB"
            || name == "PTRA" || name == "PTRB"
            || IsSpecialRegisterName(name);
    }

    private static bool IsSpecialRegisterName(string name)
    {
        return name is "PA" or "PB" or "PTRA" or "PTRB"
            or "DIRA" or "DIRB" or "OUTA" or "OUTB" or "INA" or "INB"
            or "IJMP1" or "IRET1" or "IJMP2" or "IRET2" or "IJMP3" or "IRET3";
    }

    private static int GetSpecialRegisterAddress(string name)
    {
        return name switch
        {
            "IJMP3" => P2Registers.IJMP3,
            "IRET3" => P2Registers.IRET3,
            "IJMP2" => P2Registers.IJMP2,
            "IRET2" => P2Registers.IRET2,
            "IJMP1" => P2Registers.IJMP1,
            "IRET1" => P2Registers.IRET1,
            "PA" => P2Registers.PA,
            "PB" => P2Registers.PB,
            "PTRA" => P2Registers.PTRA,
            "PTRB" => P2Registers.PTRB,
            "DIRA" => P2Registers.DIRA,
            "DIRB" => P2Registers.DIRB,
            "OUTA" => P2Registers.OUTA,
            "OUTB" => P2Registers.OUTB,
            "INA" => P2Registers.INA,
            "INB" => P2Registers.INB,
            _ => throw new InvalidOperationException($"Unknown special register: {name}"),
        };
    }

    private static bool IsLabel(string name, AsmModule module)
    {
        // Check if name matches any label in the module
        foreach (AsmFunction function in module.Functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmLabelNode label && label.Name == name)
                    return true;
            }

            // Function names are also labels
            if (function.Name == name)
                return true;
        }
        return false;
    }
}
