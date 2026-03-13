using System.Text;

namespace Blade.IR.Asm;

public static class FinalAssemblyWriter
{
    /// <summary>
    /// Opcodes where the S-field symbol operand is a jump/call target address
    /// (needs # prefix). All other symbol operands are register references.
    /// </summary>
    private static bool IsJumpOrCallOpcode(string opcode)
    {
        return opcode is "JMP" or "TJZ" or "TJNZ" or "TJF" or "TJNF"
            or "DJNZ" or "DJZ" or "CALL" or "CALLPA" or "CALLPB"
            or "CALLB" or "CALLD" or "CALLA" or "LOC";
    }

    public static string Write(AsmModule module)
    {
        StringBuilder sb = new();
        sb.AppendLine("DAT");
        sb.AppendLine("    org 0");
        sb.AppendLine("    ' --- Blade compiler output ---");

        foreach (AsmFunction function in module.Functions)
        {
            sb.AppendLine();
            sb.Append("    ' function ");
            sb.Append(function.Name);
            sb.Append(" (");
            sb.Append(function.CcTier);
            sb.AppendLine(")");
            foreach (AsmNode node in function.Nodes)
                WriteNode(sb, node);
        }

        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, AsmNode node)
    {
        switch (node)
        {
            case AsmDirectiveNode directive:
                // Emit data directives (LONG, etc.) — these define register storage
                if (!string.IsNullOrWhiteSpace(directive.Text))
                {
                    sb.Append("            ");
                    sb.AppendLine(directive.Text);
                }
                break;

            case AsmLabelNode label:
                sb.Append(label.Name);
                sb.AppendLine();
                break;

            case AsmCommentNode comment:
                sb.Append("    ' ");
                sb.AppendLine(comment.Text);
                break;

            case AsmInstructionNode instruction:
                sb.Append("            ");
                if (!string.IsNullOrWhiteSpace(instruction.Predicate))
                {
                    sb.Append(instruction.Predicate);
                    sb.Append(' ');
                }

                sb.Append(instruction.Opcode);
                if (instruction.Operands.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatOperand(instruction, i));
                    }
                }

                if (instruction.FlagEffect != AsmFlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(FormatFlagEffect(instruction.FlagEffect));
                }

                sb.AppendLine();
                break;
        }
    }

    private static string FormatOperand(AsmInstructionNode instruction, int operandIndex)
    {
        AsmOperand operand = instruction.Operands[operandIndex];

        return operand switch
        {
            AsmPhysicalRegisterOperand phys => phys.Name,
            AsmRegisterOperand virt => virt.Format(),
            AsmImmediateOperand imm => imm.Format(),
            AsmSymbolOperand sym => FormatSymbolOperand(sym, instruction, operandIndex),
            _ => operand.Format(),
        };
    }

    /// <summary>
    /// Format a symbol operand with or without # prefix depending on context.
    /// - Jump/call targets in S-field: #label (immediate address)
    /// - Register references in D-field or S-field: plain label
    /// - Special registers (PA, PB, etc.): plain name
    /// </summary>
    private static string FormatSymbolOperand(
        AsmSymbolOperand sym,
        AsmInstructionNode instruction,
        int operandIndex)
    {
        // Special register names: always plain
        if (IsSpecialRegisterName(sym.Name))
            return sym.Name;

        // $ (current address): always prefixed
        if (sym.Name == "$")
            return "#$";

        // Jump/call opcodes: the target (last operand) gets # prefix
        if (IsJumpOrCallOpcode(instruction.Opcode))
        {
            // For CALLPA/CALLPB: operand[0] = PA/PB, operand[1] = target
            // For JMP/CALL: operand[0] = target
            // For TJZ: operand[0] = cond, operand[1] = target
            int targetIndex = instruction.Operands.Count - 1;
            if (operandIndex == targetIndex)
                return $"#{sym.Name}";
        }

        // Default: register reference (no # prefix)
        return sym.Name;
    }

    private static bool IsSpecialRegisterName(string name)
    {
        return name is "PA" or "PB" or "PTRA" or "PTRB"
            or "DIRA" or "DIRB" or "OUTA" or "OUTB" or "INA" or "INB"
            or "IJMP1" or "IRET1" or "IJMP2" or "IRET2" or "IJMP3" or "IRET3";
    }

    private static string FormatFlagEffect(AsmFlagEffect effect)
    {
        return effect switch
        {
            AsmFlagEffect.WC => "WC",
            AsmFlagEffect.WZ => "WZ",
            AsmFlagEffect.WCZ => "WCZ",
            _ => string.Empty,
        };
    }
}
