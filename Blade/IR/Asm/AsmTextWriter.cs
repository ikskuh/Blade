using System.Collections.Generic;
using System.Globalization;
using Blade.Reports;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade.IR.Asm;

/// <summary>
/// Emits ASMIR modules as human-readable text.
/// </summary>
public sealed class AsmTextWriter : TextReportBuilderBase
{
    private AsmTextWriter(ITextReportBuilder builder)
        : base(builder)
    {
    }

    /// <summary>
    /// Emits the supplied modules into the provided report builder.
    /// </summary>
    public static void Write(ITextReportBuilder builder, IReadOnlyList<AsmModule> modules)
    {
        Requires.NotNull(builder);
        Requires.NotNull(modules);

        AsmTextWriter writer = new(builder);
        writer.WriteModules(modules);
    }

    private void WriteModules(IReadOnlyList<AsmModule> modules)
    {
        AppendLine((Comment, "; ASMIR v2"));
        NewLine();

        foreach (AsmModule module in modules)
        {
            AppendLine((Comment, $"; image {module.Image.Task.Name}"));
            NewLine();

            foreach (AsmFunction function in module.Functions)
                WriteFunction(function);

            foreach (AsmDataBlock block in module.DataBlocks)
                WriteDataBlock(block);
        }
    }

    private void WriteFunction(AsmFunction function)
    {
        RegisterFormatter formatter = new();

        Append((Keyword, "function"), ' ', (FunctionName, function, function.Name));
        if (function.IsEntryPoint)
            Append(' ', (Keyword, "entry"));
        Append(' ', '[', (Literal, function.CcTier.ToString()), ']');
        NewLine();
        AppendLine('{');
        foreach (AsmNode node in function.Nodes)
            WriteNode(node, formatter);
        AppendLine('}');
        NewLine();
    }

    private void WriteNode(AsmNode node, RegisterFormatter formatter)
    {
        switch (node)
        {
            case AsmLabelNode label:
                AppendLine(Space(2), (VariableName, label.Label, label.Name), ':');
                break;

            case AsmCommentNode comment:
                AppendLine(Space(4), (Comment, "' " + comment.Text));
                break;

            case AsmInstructionNode instruction:
                Append(Space(4));
                if (instruction.Condition is P2ConditionCode condition)
                    Append((Keyword, P2MetadataSyntax.GetConditionPrefixText(condition)), ' ');

                Append(Keyword, instruction.Mnemonic.ToString());
                if (instruction.Operands.Count > 0)
                {
                    Append(' ');
                    for (int i = 0; i < instruction.Operands.Count; i++)
                    {
                        if (i > 0)
                            Append(',', ' ');
                        WriteInlineOperand(instruction.Operands[i], formatter);
                    }
                }

                WriteFlagInput(instruction.FlagInput, formatter);
                WriteFlagOutput(instruction.FlagOutput, formatter);

                NewLine();
                break;

            case AsmInlineDataNode inlineData:
                Append(Space(4));
                Append(Directive, inlineData.Directive.ToString());
                if (inlineData.Values.Count > 0)
                {
                    Append(' ');
                    for (int i = 0; i < inlineData.Values.Count; i++)
                    {
                        if (i > 0)
                            Append(',', ' ');
                        Append(Literal, FormatInlineDataValue(inlineData.Values[i], formatter));
                    }
                }

                NewLine();
                break;

            case AsmVolatileRegionBeginNode:
                AppendLine(Space(4), (Directive, ".volatile_begin"));
                break;

            case AsmVolatileRegionEndNode:
                AppendLine(Space(4), (Directive, ".volatile_end"));
                break;

            default:
                Assert.Unreachable($"Unhandled ASMIR node '{node.GetType().Name}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private void WriteDataBlock(AsmDataBlock block)
    {
        Append((Keyword, "data"), ' ', (Literal, block.Kind.ToString()));
        NewLine();
        AppendLine('{');
        foreach (AsmDataDefinition definition in block.Definitions)
            WriteDefinition(definition);
        AppendLine('}');
        NewLine();
    }

    private void WriteDefinition(AsmDataDefinition definition)
    {
        switch (definition)
        {
            case AsmAllocatedStorageDefinition allocated:
                Append(Space(2), (VariableName, allocated.Symbol, allocated.Symbol.Name), ':', ' ', (Directive, allocated.Directive.ToString()), ' ');
                WriteAllocatedValues(allocated);
                NewLine();
                break;

            case AsmExternalBindingDefinition external:
                AppendLine(Space(2), (Keyword, "extern"), ' ', (VariableName, external.Symbol, external.Symbol.Name));
                break;

            default:
                Assert.Unreachable($"Unhandled ASMIR data definition '{definition.GetType().Name}'."); // pragma: force-coverage
                break; // pragma: force-coverage
        }
    }

    private void WriteInlineOperand(AsmOperand operand, RegisterFormatter formatter)
    {
        switch (operand)
        {
            case AsmRegisterOperand register:
                Append(VariableName, register.Value, formatter.Format(register.Value));
                return;

            case AsmSymbolOperand symbol:
                Append(VariableName, symbol.Symbol, symbol.Name);
                return;

            default:
                Append(Literal, operand.Format());
                return;
        }
    }

    private void WriteAllocatedValues(AsmAllocatedStorageDefinition allocated)
    {
        if (allocated.InitialValues is null || allocated.InitialValues.Count == 0)
        {
            Append(Literal, "0");
            if (allocated.Count > 1)
                Append(' ', '[', (Literal, allocated.Count.ToString(CultureInfo.InvariantCulture)), ']');
            return;
        }

        if (allocated.InitialValues.Count == 1)
        {
            Append(Literal, allocated.InitialValues[0].Format());
            if (allocated.Count > 1)
                Append(' ', '[', (Literal, allocated.Count.ToString(CultureInfo.InvariantCulture)), ']');
            return;
        }

        for (int i = 0; i < allocated.InitialValues.Count; i++)
        {
            if (i > 0)
                Append(',', ' ');
            Append(Literal, allocated.InitialValues[i].Format());
        }

        if (allocated.Count > 1)
            Append(' ', '[', (Literal, allocated.Count.ToString(CultureInfo.InvariantCulture)), ']');
    }

    private void WriteFlagInput(AsmFlagInput input, RegisterFormatter formatter)
    {
        if (input.C is not null)
        {
            Append(' ', (Keyword, "C"), '=');
            Append((VariableName, input.C, formatter.FormatFlag(input.C)));
        }

        if (input.Z is not null)
        {
            if (input.C is not null)
                Append(',', ' ');
            else
                Append(' ');

            Append((Keyword, "Z"), '=');
            Append((VariableName, input.Z, formatter.FormatFlag(input.Z)));
        }
    }

    private void WriteFlagOutput(AsmFlagOutput output, RegisterFormatter formatter)
    {
        if (output.Effect == P2FlagEffect.None)
            return;

        if (!output.Any)
        {
            Append(' ', (Keyword, output.Effect.ToString()));
            return;
        }

        if (output.Effect == P2FlagEffect.WC && output.C is not null)
        {
            Append(' ', (Keyword, "WC"), '=');
            Append((VariableName, output.C, formatter.FormatFlag(output.C)));
            return;
        }

        if (output.Effect == P2FlagEffect.WZ && output.Z is not null)
        {
            Append(' ', (Keyword, "WZ"), '=');
            Append((VariableName, output.Z, formatter.FormatFlag(output.Z)));
            return;
        }

        if (output.Effect == P2FlagEffect.WCZ && output.C is not null && output.Z is not null)
        {
            Append(' ', (Keyword, "WCZ"), '=', '(');
            Append((VariableName, output.C, formatter.FormatFlag(output.C)));
            Append(',', ' ');
            Append((VariableName, output.Z, formatter.FormatFlag(output.Z)));
            Append(')');
            return;
        }

        Append(' ', (Keyword, output.Effect.ToString()));
        if (output.C is not null)
        {
            Append(' ', (Keyword, "WC"), '=');
            Append((VariableName, output.C, formatter.FormatFlag(output.C)));
        }

        if (output.Z is not null)
        {
            Append(' ', (Keyword, "WZ"), '=');
            Append((VariableName, output.Z, formatter.FormatFlag(output.Z)));
        }
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

    private static string FormatOperand(AsmOperand operand, RegisterFormatter formatter)
    {
        return operand switch
        {
            AsmRegisterOperand register => formatter.Format(register.Value),
            _ => operand.Format(),
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
