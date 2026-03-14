using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Blade.IR.Mir;

public static class MirTextWriter
{
    public static string Write(MirModule module)
    {
        StringBuilder sb = new();
        sb.AppendLine("; MIR v1");
        sb.AppendLine();

        foreach (MirFunction function in module.Functions)
            WriteFunction(sb, function);

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, MirFunction function)
    {
        sb.Append("fn ");
        sb.Append(function.Name);
        sb.Append(" kind=");
        sb.Append(function.Kind);
        if (function.IsEntryPoint)
            sb.Append(" entry");
        sb.Append(" returns=(");
        for (int i = 0; i < function.ReturnTypes.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(function.ReturnTypes[i].Name);
        }

        sb.AppendLine(")");
        sb.AppendLine("{");

        foreach (MirBlock block in function.Blocks)
            WriteBlock(sb, block);

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteBlock(StringBuilder sb, MirBlock block)
    {
        sb.Append("  ");
        sb.Append(block.Label);
        sb.Append('(');
        for (int i = 0; i < block.Parameters.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            MirBlockParameter parameter = block.Parameters[i];
            sb.Append(parameter.Value);
            sb.Append(':');
            sb.Append(parameter.Type.Name);
            sb.Append(" ");
            sb.Append(parameter.Name);
        }

        sb.AppendLine("):");
        foreach (MirInstruction instruction in block.Instructions)
            WriteInstruction(sb, instruction);

        WriteTerminator(sb, block.Terminator);
    }

    private static void WriteInstruction(StringBuilder sb, MirInstruction instruction)
    {
        sb.Append("    ");
        if (instruction.Result is MirValueId result)
        {
            sb.Append(result);
            sb.Append(':');
            sb.Append(instruction.ResultType?.Name ?? "<unknown>");
            sb.Append(" = ");
        }

        switch (instruction)
        {
            case MirConstantInstruction constant:
                sb.Append("const ");
                sb.Append(FormatConstant(constant.Value));
                break;

            case MirLoadSymbolInstruction load:
                sb.Append("load ");
                sb.Append('@');
                sb.Append(load.SymbolName);
                break;

            case MirLoadPlaceInstruction loadPlace:
                sb.Append("load.place ");
                sb.Append(loadPlace.Place.EmittedName);
                break;

            case MirCopyInstruction copy:
                sb.Append("copy ");
                sb.Append(copy.Source);
                break;

            case MirUnaryInstruction unary:
                sb.Append("unary.");
                sb.Append(unary.Operator);
                sb.Append(' ');
                sb.Append(unary.Operand);
                break;

            case MirBinaryInstruction binary:
                sb.Append("binary.");
                sb.Append(binary.Operator);
                sb.Append(' ');
                sb.Append(binary.Left);
                sb.Append(", ");
                sb.Append(binary.Right);
                break;

            case MirSelectInstruction select:
                sb.Append("select ");
                sb.Append(select.Condition);
                sb.Append(" ? ");
                sb.Append(select.WhenTrue);
                sb.Append(" : ");
                sb.Append(select.WhenFalse);
                break;

            case MirOpInstruction op:
                sb.Append(op.Opcode);
                if (op.Operands.Count > 0)
                {
                    sb.Append(' ');
                    WriteValueList(sb, op.Operands);
                }

                break;

            case MirCallInstruction call:
                sb.Append("call ");
                sb.Append(call.FunctionName);
                sb.Append('(');
                WriteValueList(sb, call.Arguments);
                sb.Append(')');
                break;

            case MirIntrinsicCallInstruction intrinsic:
                sb.Append("intrinsic @");
                sb.Append(intrinsic.IntrinsicName);
                sb.Append('(');
                WriteValueList(sb, intrinsic.Arguments);
                sb.Append(')');
                break;

            case MirStoreInstruction store:
                sb.Append("store ");
                sb.Append(store.Target);
                sb.Append('(');
                WriteValueList(sb, store.Operands);
                sb.Append(')');
                break;

            case MirStorePlaceInstruction storePlace:
                sb.Append("store.place ");
                sb.Append(storePlace.Place.EmittedName);
                sb.Append('(');
                sb.Append(storePlace.Value);
                sb.Append(')');
                break;

            case MirUpdatePlaceInstruction updatePlace:
                sb.Append("update.place ");
                sb.Append(updatePlace.Place.EmittedName);
                sb.Append(' ');
                sb.Append(updatePlace.OperatorKind);
                sb.Append(' ');
                sb.Append(updatePlace.Value);
                break;

            case MirInlineAsmInstruction inlineAsm:
                sb.Append(inlineAsm.Volatility == AsmVolatility.Volatile ? "inlineasm.volatile" : "inlineasm");
                if (inlineAsm.FlagOutput is not null)
                {
                    sb.Append(" -> ");
                    sb.Append(inlineAsm.FlagOutput);
                }
                if (inlineAsm.Bindings.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < inlineAsm.Bindings.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        MirInlineAsmBinding binding = inlineAsm.Bindings[i];
                        sb.Append(binding.Name);
                        sb.Append('=');
                        if (binding.Value is MirValueId value)
                            sb.Append(value);
                        else
                            sb.Append(binding.Place?.EmittedName ?? "<none>");
                    }
                }
                break;

            case MirPseudoInstruction pseudo:
                sb.Append("pseudo ");
                sb.Append(pseudo.Opcode);
                if (pseudo.Operands.Count > 0)
                {
                    sb.Append(' ');
                    WriteValueList(sb, pseudo.Operands);
                }

                break;
        }

        if (instruction.HasSideEffects)
            sb.Append(" ; sidefx");
        sb.AppendLine();
    }

    private static void WriteTerminator(StringBuilder sb, MirTerminator terminator)
    {
        sb.Append("    ");
        switch (terminator)
        {
            case MirGotoTerminator mirGoto:
                sb.Append("goto ");
                sb.Append(mirGoto.TargetLabel);
                sb.Append('(');
                WriteValueList(sb, mirGoto.Arguments);
                sb.AppendLine(")");
                break;

            case MirBranchTerminator branch:
                sb.Append("branch ");
                sb.Append(branch.Condition);
                sb.Append(" ? ");
                sb.Append(branch.TrueLabel);
                sb.Append('(');
                WriteValueList(sb, branch.TrueArguments);
                sb.Append(") : ");
                sb.Append(branch.FalseLabel);
                sb.Append('(');
                WriteValueList(sb, branch.FalseArguments);
                sb.AppendLine(")");
                break;

            case MirReturnTerminator ret:
                sb.Append("ret ");
                WriteValueList(sb, ret.Values);
                sb.AppendLine();
                break;

            case MirUnreachableTerminator:
                sb.AppendLine("unreachable");
                break;
        }
    }

    private static void WriteValueList(StringBuilder sb, IReadOnlyList<MirValueId> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(values[i]);
        }
    }

    private static string FormatConstant(object? value)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => $"\"{s}\"",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "<?>",
        };
    }
}
