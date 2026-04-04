using System.Collections.Generic;
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

        foreach (AsmDataBlock block in module.DataBlocks)
            WriteDataBlock(sb, block);

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, AsmFunction function)
    {
        RegisterFormatter formatter = new();

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
            WriteNode(sb, node, formatter);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteNode(StringBuilder sb, AsmNode node, RegisterFormatter formatter)
    {
        switch (node)
        {
            case AsmLabelNode label:
                sb.Append("  ");
                sb.Append(label.Name);
                sb.AppendLine(":");
                break;

            case AsmCommentNode comment:
                sb.Append("    ' ");
                sb.AppendLine(comment.Text);
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
                        sb.Append(FormatOperand(instruction.Operands[i], formatter));
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

    private static void WriteDataBlock(StringBuilder sb, AsmDataBlock block)
    {
        sb.Append("data ");
        sb.Append(block.Kind);
        sb.AppendLine();
        sb.AppendLine("{");
        foreach (AsmDataDefinition definition in block.Definitions)
        {
            switch (definition)
            {
                case AsmAllocatedStorageDefinition allocated:
                    sb.Append("  ");
                    sb.Append(allocated.Symbol.Name);
                    sb.Append(": ");
                    sb.Append(allocated.Directive);
                    sb.Append(' ');
                    if (allocated.InitialValues is null || allocated.InitialValues.Count == 0)
                    {
                        sb.Append('0');
                    }
                    else if (allocated.InitialValues.Count == 1)
                    {
                        sb.Append(allocated.InitialValues[0].Format());
                    }
                    else
                    {
                        for (int i = 0; i < allocated.InitialValues.Count; i++)
                        {
                            if (i > 0)
                                sb.Append(", ");
                            sb.Append(allocated.InitialValues[i].Format());
                        }
                    }
                    if (allocated.Count > 1)
                    {
                        sb.Append(" [");
                        sb.Append(allocated.Count);
                        sb.Append(']');
                    }

                    sb.AppendLine();
                    break;
                case AsmExternalBindingDefinition external:
                    sb.Append("  extern ");
                    sb.AppendLine(external.Symbol.Name);
                    break;
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string FormatFlagEffect(P2FlagEffect effect)
    {
        return effect == P2FlagEffect.None ? string.Empty : effect.ToString();
    }

    private static string FormatOperand(AsmOperand operand, RegisterFormatter formatter)
    {
        return operand switch
        {
            AsmRegisterOperand register => formatter.Format(register.Register),
            _ => operand.Format(),
        };
    }

    private sealed class RegisterFormatter
    {
        private readonly Dictionary<VirtualAsmRegister, int> _ids = [];

        public string Format(VirtualAsmRegister register)
        {
            if (!_ids.TryGetValue(register, out int id))
            {
                id = _ids.Count;
                _ids.Add(register, id);
            }

            return $"%r{id}";
        }
    }
}
