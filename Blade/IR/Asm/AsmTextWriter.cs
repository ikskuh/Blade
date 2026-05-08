using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Blade;

namespace Blade.IR.Asm;

public static class AsmTextWriter
{
    public static string Write(AsmModule module)
    {
        Requires.NotNull(module);
        return Write([module]);
    }

    public static string Write(IReadOnlyList<AsmModule> modules)
    {
        Requires.NotNull(modules);

        StringBuilder sb = new();
        sb.AppendLine("; ASMIR v2");
        sb.AppendLine();

        foreach (AsmModule module in modules)
        {
            sb.Append("; image ");
            sb.AppendLine(module.Image.Task.Name);
            sb.AppendLine();

            foreach (AsmFunction function in module.Functions)
                WriteFunction(sb, function);

            foreach (AsmDataBlock block in module.DataBlocks)
                WriteDataBlock(sb, block);
        }

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
                    sb.Append(P2MetadataSyntax.GetConditionPrefixText(condition));
                    sb.Append(' ');
                }

                sb.Append(instruction.Mnemonic.ToString());
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

                string flagInput = FormatFlagInput(instruction.FlagInput, formatter);
                if (flagInput.Length > 0)
                {
                    sb.Append(' ');
                    sb.Append(flagInput);
                }

                string flagOutput = FormatFlagOutput(instruction.FlagOutput, formatter);
                if (flagOutput.Length > 0)
                {
                    sb.Append(' ');
                    sb.Append(flagOutput);
                }

                sb.AppendLine();
                break;

            case AsmInlineDataNode inlineData:
                sb.Append("    ");
                sb.Append(inlineData.Directive);
                if (inlineData.Values.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < inlineData.Values.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(FormatInlineDataValue(inlineData.Values[i], formatter));
                    }
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

    private static string FormatFlagInput(AsmFlagInput input, RegisterFormatter formatter)
    {
        List<string> parts = [];
        if (input.C is not null)
            parts.Add($"C={formatter.FormatFlag(input.C)}");
        if (input.Z is not null)
            parts.Add($"Z={formatter.FormatFlag(input.Z)}");
        return string.Join(", ", parts);
    }

    private static string FormatFlagOutput(AsmFlagOutput output, RegisterFormatter formatter)
    {
        if (output.Effect == P2FlagEffect.None)
            return string.Empty;

        if (!output.Any)
            return output.Effect.ToString();

        if (output.Effect == P2FlagEffect.WC && output.C is not null)
            return $"WC={formatter.FormatFlag(output.C)}";

        if (output.Effect == P2FlagEffect.WZ && output.Z is not null)
            return $"WZ={formatter.FormatFlag(output.Z)}";

        if (output.Effect == P2FlagEffect.WCZ && output.C is not null && output.Z is not null)
            return $"WCZ=({formatter.FormatFlag(output.C)}, {formatter.FormatFlag(output.Z)})";

        List<string> parts = [output.Effect.ToString()];
        if (output.C is not null)
            parts.Add($"WC={formatter.FormatFlag(output.C)}");
        if (output.Z is not null)
            parts.Add($"WZ={formatter.FormatFlag(output.Z)}");
        return string.Join(" ", parts);
    }

    private static string FormatOperand(AsmOperand operand, RegisterFormatter formatter)
    {
        return operand switch
        {
            AsmRegisterOperand register => formatter.Format(register.Value),
            _ => operand.Format(),
        };
    }

    private static string FormatInlineDataValue(AsmInlineDataValue value, RegisterFormatter formatter)
    {
        return value switch
        {
            AsmInlineDataOperandValue operandValue when operandValue.Operand is AsmRegisterOperand register && !operandValue.PreserveImmediateSyntax
                => formatter.Format(register.Value),
            AsmInlineDataOperandValue operandValue when operandValue.PreserveImmediateSyntax
                => FormatOperand(operandValue.Operand, formatter),
            AsmInlineDataOperandValue operandValue
                => FormatDataLikeOperand(operandValue.Operand, formatter),
            AsmInlineDataRawSymbolValue raw when raw.PreserveImmediateSyntax
                => "#" + raw.Name,
            AsmInlineDataRawSymbolValue raw
                => raw.Name,
            _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
        };
    }

    private static string FormatDataLikeOperand(AsmOperand operand, RegisterFormatter formatter)
    {
        return operand switch
        {
            AsmRegisterOperand register => formatter.Format(register.Value),
            AsmImmediateOperand immediate => immediate.Value.ToString(CultureInfo.InvariantCulture),
            AsmSymbolOperand symbol => symbol.Name,
            _ => operand.Format(),
        };
    }

    private sealed class RegisterFormatter
    {
        private readonly Dictionary<VirtualAsmValue, int> _ids = [];

        public string Format(VirtualAsmValue value)
        {
            if (!_ids.TryGetValue(value, out int id))
            {
                id = _ids.Count;
                _ids.Add(value, id);
            }

            return $"%r{id}";
        }

        public string FormatFlag(VirtualAsmFlag flag)
        {
            if (!_ids.TryGetValue(flag, out int id))
            {
                id = _ids.Count;
                _ids.Add(flag, id);
            }

            return $"%f{id}";
        }
    }
}
