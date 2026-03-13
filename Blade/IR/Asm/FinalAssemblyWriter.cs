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
            sb.AppendLine(function.Name);
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
                sb.Append("    ");
                sb.AppendLine(directive.Text);
                break;

            case AsmLabelNode label:
                sb.Append(label.Name);
                sb.AppendLine(":");
                break;

            case AsmInstructionNode instruction:
                sb.Append("    ");
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
                        sb.Append(instruction.Operands[i]);
                    }
                }

                sb.AppendLine();
                break;
        }
    }
}
