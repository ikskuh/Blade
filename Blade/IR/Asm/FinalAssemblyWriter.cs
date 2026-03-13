using System.Text;

namespace Blade.IR.Asm;

public static class FinalAssemblyWriter
{
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
            case AsmDirectiveNode:
                // Directives are internal markers, not emitted in final PASM2
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
                        sb.Append(FormatOperand(instruction.Operands[i]));
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

    private static string FormatOperand(AsmOperand operand)
    {
        return operand switch
        {
            AsmPhysicalRegisterOperand phys => phys.Name,
            AsmRegisterOperand virt => virt.Format(), // Fallback for pre-regalloc output
            AsmImmediateOperand imm => imm.Format(),
            AsmSymbolOperand sym => sym.Format(),
            _ => operand.Format(),
        };
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
