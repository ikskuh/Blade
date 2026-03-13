using System.Text;

namespace Blade.IR.Asm;

public static class AsmTextWriter
{
    public static string Write(AsmModule module)
    {
        StringBuilder sb = new();
        sb.AppendLine("; ASMIR v1");
        sb.AppendLine();

        foreach (AsmFunction function in module.Functions)
            WriteFunction(sb, function);

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, AsmFunction function)
    {
        sb.Append("function ");
        sb.Append(function.Name);
        if (function.IsEntryPoint)
            sb.Append(" entry");
        sb.AppendLine();
        sb.AppendLine("{");
        foreach (AsmNode node in function.Nodes)
            WriteNode(sb, node);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteNode(StringBuilder sb, AsmNode node)
    {
        switch (node)
        {
            case AsmDirectiveNode directive:
                sb.Append("  .");
                sb.AppendLine(directive.Text);
                break;

            case AsmLabelNode label:
                sb.Append("  ");
                sb.Append(label.Name);
                sb.AppendLine(":");
                break;

            case AsmInstructionNode instruction:
                sb.Append("    ");
                if (!string.IsNullOrWhiteSpace(instruction.Predicate))
                {
                    sb.Append('[');
                    sb.Append(instruction.Predicate);
                    sb.Append("] ");
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
