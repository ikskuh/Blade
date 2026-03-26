using System.Text;
using Blade;

namespace Blade.IR.Asm;

public static class AsmTextWriter
{
    public static string Write(AsmModule module)
    {
        Requires.NotNull(module);

        StringBuilder sb = new();
        sb.AppendLine("; ASMIR v2");
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
        sb.Append(" [");
        sb.Append(function.CcTier);
        sb.Append(']');
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

            case AsmCommentNode comment:
                sb.Append("    ' ");
                sb.AppendLine(comment.Text);
                break;

            case AsmImplicitUseNode implicitUse:
                sb.Append("    .use ");
                for (int i = 0; i < implicitUse.Operands.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(implicitUse.Operands[i].Format());
                }
                sb.AppendLine();
                break;

            case AsmInstructionNode instruction:
                sb.Append("    ");
                if (instruction.Condition is P2ConditionCode condition)
                {
                    sb.Append(P2InstructionMetadata.GetConditionPrefixText(condition));
                    sb.Append(' ');
                }

                sb.Append(P2InstructionMetadata.GetMnemonicText(instruction.Mnemonic));
                if (instruction.Operands.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(instruction.Operands[i].Format());
                    }
                }

                if (instruction.FlagEffect != P2FlagEffect.None)
                {
                    sb.Append(' ');
                    sb.Append(FormatFlagEffect(instruction.FlagEffect));
                }

                sb.AppendLine();
                break;

            case AsmVolatileRegionBeginNode:
                sb.AppendLine("    .volatile_begin");
                break;

            case AsmVolatileRegionEndNode:
                sb.AppendLine("    .volatile_end");
                break;
        }
    }

    private static string FormatFlagEffect(P2FlagEffect effect)
    {
        return effect == P2FlagEffect.None ? string.Empty : effect.ToString();
    }
}
