using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Mir;

public static class MirTextWriter
{
    public static string Write(MirModule module)
    {
        Requires.NotNull(module);

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
            sb.Append(' ');
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

            case MirConvertInstruction convert:
                sb.Append("convert ");
                sb.Append(convert.Operand);
                break;

            case MirRangeInstruction range:
                sb.Append("range ");
                sb.Append(range.Start);
                sb.Append(", ");
                sb.Append(range.End);
                break;

            case MirStructLiteralInstruction structLiteral:
                sb.Append("structlit");
                foreach (MirStructLiteralField field in structLiteral.Fields)
                {
                    sb.Append('.');
                    sb.Append(field.Member.Name);
                }
                if (structLiteral.Fields.Count > 0)
                {
                    sb.Append(' ');
                    for (int i = 0; i < structLiteral.Fields.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(structLiteral.Fields[i].Value);
                    }
                }
                break;

            case MirLoadMemberInstruction loadMember:
                sb.Append("load.member.");
                sb.Append(loadMember.Member.Name);
                sb.Append('.');
                sb.Append(loadMember.Member.ByteOffset);
                sb.Append(' ');
                sb.Append(loadMember.Receiver);
                break;

            case MirLoadIndexInstruction loadIndex:
                sb.Append("load.index.");
                sb.Append(FormatStorageClass(loadIndex.StorageClass));
                sb.Append(' ');
                sb.Append(loadIndex.Indexed);
                sb.Append(", ");
                sb.Append(loadIndex.Index);
                break;

            case MirLoadDerefInstruction loadDeref:
                sb.Append("load.deref.");
                sb.Append(FormatStorageClass(loadDeref.StorageClass));
                sb.Append(' ');
                sb.Append(loadDeref.Address);
                break;

            case MirBitfieldExtractInstruction extract:
                sb.Append("bitfield.extract.");
                sb.Append(extract.Member.BitOffset);
                sb.Append('.');
                sb.Append(extract.Member.BitWidth);
                sb.Append(' ');
                sb.Append(extract.Receiver);
                break;

            case MirBitfieldInsertInstruction insertBitfield:
                sb.Append("bitfield.insert.");
                sb.Append(insertBitfield.Member.BitOffset);
                sb.Append('.');
                sb.Append(insertBitfield.Member.BitWidth);
                sb.Append(' ');
                sb.Append(insertBitfield.Receiver);
                sb.Append(", ");
                sb.Append(insertBitfield.Value);
                break;

            case MirInsertMemberInstruction insertMember:
                sb.Append("insert.member.");
                sb.Append(insertMember.Member.Name);
                sb.Append('.');
                sb.Append(insertMember.Member.ByteOffset);
                sb.Append(' ');
                sb.Append(insertMember.Receiver);
                sb.Append(", ");
                sb.Append(insertMember.Value);
                break;

            case MirCallInstruction call:
                sb.Append("call ");
                sb.Append(call.FunctionName);
                sb.Append('(');
                WriteValueList(sb, call.Arguments);
                sb.Append(')');
                if (call.ExtraResults.Count > 0)
                {
                    sb.Append(" extra=[");
                    for (int i = 0; i < call.ExtraResults.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(call.ExtraResults[i].Value);
                        sb.Append(':');
                        sb.Append(call.ExtraResults[i].Type.Name);
                    }
                    sb.Append(']');
                }
                break;

            case MirIntrinsicCallInstruction intrinsic:
                sb.Append("intrinsic @");
                sb.Append(intrinsic.IntrinsicName);
                sb.Append('(');
                WriteValueList(sb, intrinsic.Arguments);
                sb.Append(')');
                break;

            case MirStoreIndexInstruction storeIndex:
                sb.Append("store index.");
                sb.Append(FormatStorageClass(storeIndex.StorageClass));
                sb.Append('(');
                sb.Append(storeIndex.Indexed);
                sb.Append(", ");
                sb.Append(storeIndex.Index);
                sb.Append(", ");
                sb.Append(storeIndex.Value);
                sb.Append(')');
                break;

            case MirStoreDerefInstruction storeDeref:
                sb.Append("store deref.");
                sb.Append(FormatStorageClass(storeDeref.StorageClass));
                sb.Append('(');
                sb.Append(storeDeref.Address);
                sb.Append(", ");
                sb.Append(storeDeref.Value);
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
                        sb.Append(':');
                        sb.Append(FormatInlineAsmAccess(binding.Access));
                    }
                }
                break;

            case MirYieldInstruction:
                sb.Append("yield");
                break;

            case MirYieldToInstruction yieldTo:
                sb.Append("yieldto:");
                sb.Append(yieldTo.TargetFunctionName);
                if (yieldTo.Arguments.Count > 0)
                {
                    sb.Append(' ');
                    WriteValueList(sb, yieldTo.Arguments);
                }
                break;

            case MirRepSetupInstruction repSetup:
                sb.Append("rep.setup ");
                sb.Append(repSetup.Count);
                break;

            case MirRepIterInstruction repIter:
                sb.Append("rep.iter ");
                sb.Append(repIter.Count);
                break;

            case MirRepForSetupInstruction repForSetup:
                sb.Append("repfor.setup ");
                sb.Append(repForSetup.Start);
                sb.Append(", ");
                sb.Append(repForSetup.End);
                break;

            case MirRepForIterInstruction repForIter:
                sb.Append("repfor.iter ");
                sb.Append(repForIter.Start);
                sb.Append(", ");
                sb.Append(repForIter.End);
                break;

            case MirNoIrqBeginInstruction:
                sb.Append("noirq.begin");
                break;

            case MirNoIrqEndInstruction:
                sb.Append("noirq.end");
                break;

            case MirErrorStatementInstruction:
                sb.Append("error.statement");
                break;

            case MirErrorStoreInstruction errorStore:
                sb.Append("store.error ");
                sb.Append(errorStore.Value);
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
                if (branch.ConditionFlag is not null)
                {
                    sb.Append(" [flag:");
                    sb.Append(branch.ConditionFlag.Value.ToString());
                    sb.Append(']');
                }
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

    private static string FormatInlineAsmAccess(InlineAsmBindingAccess access)
    {
        return access switch
        {
            InlineAsmBindingAccess.Read => "r",
            InlineAsmBindingAccess.Write => "w",
            InlineAsmBindingAccess.ReadWrite => "rw",
            _ => "?",
        };
    }

    private static string FormatStorageClass(VariableStorageClass storageClass)
    {
        return storageClass switch
        {
            VariableStorageClass.Lut => "lut",
            VariableStorageClass.Hub => "hub",
            _ => "reg",
        };
    }
}
