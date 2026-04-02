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
        ValueFormatter formatter = new();
        BlockFormatter blockFormatter = new(function.Blocks);

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
            WriteBlock(sb, block, formatter, blockFormatter);

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteBlock(StringBuilder sb, MirBlock block, ValueFormatter formatter, BlockFormatter blockFormatter)
    {
        sb.Append("  ");
        sb.Append(blockFormatter.Format(block.Ref));
        sb.Append('(');
        for (int i = 0; i < block.Parameters.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            MirBlockParameter parameter = block.Parameters[i];
            sb.Append(formatter.Format(parameter.Value));
            sb.Append(':');
            sb.Append(parameter.Type.Name);
            sb.Append(' ');
            sb.Append(parameter.Name);
        }

        sb.AppendLine("):");
        foreach (MirInstruction instruction in block.Instructions)
            WriteInstruction(sb, instruction, formatter);

        WriteTerminator(sb, block.Terminator, formatter, blockFormatter);
    }

    private static void WriteInstruction(StringBuilder sb, MirInstruction instruction, ValueFormatter formatter)
    {
        sb.Append("    ");
        if (instruction.Result is MirValueId result)
        {
            sb.Append(formatter.Format(result));
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
                sb.Append("load @");
                sb.Append(load.SymbolName);
                break;

            case MirLoadPlaceInstruction loadPlace:
                sb.Append("load.place ");
                sb.Append(loadPlace.Place.EmittedName);
                break;

            case MirCopyInstruction copy:
                sb.Append("copy ");
                sb.Append(formatter.Format(copy.Source));
                break;

            case MirUnaryInstruction unary:
                sb.Append("unary.");
                sb.Append(unary.Operator);
                sb.Append(' ');
                sb.Append(formatter.Format(unary.Operand));
                break;

            case MirBinaryInstruction binary:
                sb.Append("binary.");
                sb.Append(binary.Operator);
                sb.Append(' ');
                sb.Append(formatter.Format(binary.Left));
                sb.Append(", ");
                sb.Append(formatter.Format(binary.Right));
                break;

            case MirPointerOffsetInstruction pointerOffset:
                sb.Append("ptr.offset.");
                sb.Append(pointerOffset.OperatorKind);
                sb.Append('[');
                sb.Append(pointerOffset.Stride);
                sb.Append("] ");
                sb.Append(formatter.Format(pointerOffset.BaseAddress));
                sb.Append(", ");
                sb.Append(formatter.Format(pointerOffset.Delta));
                break;

            case MirPointerDifferenceInstruction pointerDifference:
                sb.Append("ptr.diff[");
                sb.Append(pointerDifference.Stride);
                sb.Append("] ");
                sb.Append(formatter.Format(pointerDifference.Left));
                sb.Append(", ");
                sb.Append(formatter.Format(pointerDifference.Right));
                break;

            case MirConvertInstruction convert:
                sb.Append("convert ");
                sb.Append(formatter.Format(convert.Operand));
                break;

            case MirRangeInstruction range:
                sb.Append("range ");
                sb.Append(formatter.Format(range.Start));
                sb.Append(", ");
                sb.Append(formatter.Format(range.End));
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
                        sb.Append(formatter.Format(structLiteral.Fields[i].Value));
                    }
                }
                break;

            case MirLoadMemberInstruction loadMember:
                sb.Append("load.member.");
                sb.Append(loadMember.Member.Name);
                sb.Append('.');
                sb.Append(loadMember.Member.ByteOffset);
                sb.Append(' ');
                sb.Append(formatter.Format(loadMember.Receiver));
                break;

            case MirLoadIndexInstruction loadIndex:
                sb.Append("load.index.");
                sb.Append(FormatStorageClass(loadIndex.StorageClass));
                sb.Append(' ');
                sb.Append(formatter.Format(loadIndex.Indexed));
                sb.Append(", ");
                sb.Append(formatter.Format(loadIndex.Index));
                break;

            case MirLoadDerefInstruction loadDeref:
                sb.Append("load.deref.");
                sb.Append(FormatStorageClass(loadDeref.StorageClass));
                sb.Append(' ');
                sb.Append(formatter.Format(loadDeref.Address));
                break;

            case MirBitfieldExtractInstruction extract:
                sb.Append("bitfield.extract.");
                sb.Append(extract.Member.BitOffset);
                sb.Append('.');
                sb.Append(extract.Member.BitWidth);
                sb.Append(' ');
                sb.Append(formatter.Format(extract.Receiver));
                break;

            case MirBitfieldInsertInstruction insertBitfield:
                sb.Append("bitfield.insert.");
                sb.Append(insertBitfield.Member.BitOffset);
                sb.Append('.');
                sb.Append(insertBitfield.Member.BitWidth);
                sb.Append(' ');
                sb.Append(formatter.Format(insertBitfield.Receiver));
                sb.Append(", ");
                sb.Append(formatter.Format(insertBitfield.Value));
                break;

            case MirInsertMemberInstruction insertMember:
                sb.Append("insert.member.");
                sb.Append(insertMember.Member.Name);
                sb.Append('.');
                sb.Append(insertMember.Member.ByteOffset);
                sb.Append(' ');
                sb.Append(formatter.Format(insertMember.Receiver));
                sb.Append(", ");
                sb.Append(formatter.Format(insertMember.Value));
                break;

            case MirCallInstruction call:
                sb.Append("call ");
                sb.Append(call.Function.Name);
                sb.Append('(');
                WriteValueList(sb, call.Arguments, formatter);
                sb.Append(')');
                if (call.ExtraResults.Count > 0)
                {
                    sb.Append(" extra=[");
                    for (int i = 0; i < call.ExtraResults.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(formatter.Format(call.ExtraResults[i].Value));
                        sb.Append(':');
                        sb.Append(call.ExtraResults[i].Type.Name);
                    }
                    sb.Append(']');
                }
                break;

            case MirIntrinsicCallInstruction intrinsic:
                sb.Append("intrinsic @");
                sb.Append(P2InstructionMetadata.GetMnemonicText(intrinsic.Mnemonic));
                sb.Append('(');
                WriteValueList(sb, intrinsic.Arguments, formatter);
                sb.Append(')');
                break;

            case MirStoreIndexInstruction storeIndex:
                sb.Append("store index.");
                sb.Append(FormatStorageClass(storeIndex.StorageClass));
                sb.Append('(');
                sb.Append(formatter.Format(storeIndex.Indexed));
                sb.Append(", ");
                sb.Append(formatter.Format(storeIndex.Index));
                sb.Append(", ");
                sb.Append(formatter.Format(storeIndex.Value));
                sb.Append(')');
                break;

            case MirStoreDerefInstruction storeDeref:
                sb.Append("store deref.");
                sb.Append(FormatStorageClass(storeDeref.StorageClass));
                sb.Append('(');
                sb.Append(formatter.Format(storeDeref.Address));
                sb.Append(", ");
                sb.Append(formatter.Format(storeDeref.Value));
                sb.Append(')');
                break;

            case MirStorePlaceInstruction storePlace:
                sb.Append("store.place ");
                sb.Append(storePlace.Place.EmittedName);
                sb.Append('(');
                sb.Append(formatter.Format(storePlace.Value));
                sb.Append(')');
                break;

            case MirUpdatePlaceInstruction updatePlace:
                sb.Append("update.place ");
                sb.Append(updatePlace.Place.EmittedName);
                sb.Append(' ');
                sb.Append(updatePlace.OperatorKind);
                if (updatePlace.PointerArithmeticStride is int stride)
                {
                    sb.Append('[');
                    sb.Append(stride);
                    sb.Append(']');
                }
                sb.Append(' ');
                sb.Append(formatter.Format(updatePlace.Value));
                break;

            case MirInlineAsmInstruction inlineAsm:
                sb.Append(inlineAsm.Volatility == AsmVolatility.Volatile ? "inlineasm.volatile" : "inlineasm");
                if (inlineAsm.FlagOutput is not null)
                {
                    sb.Append(" -> @");
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
                        sb.Append(binding.PlaceholderText);
                        sb.Append('=');
                        if (binding.Value is MirValueId value)
                            sb.Append(formatter.Format(value));
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
                sb.Append(yieldTo.TargetFunction.Name);
                if (yieldTo.Arguments.Count > 0)
                {
                    sb.Append(' ');
                    WriteValueList(sb, yieldTo.Arguments, formatter);
                }
                break;

            case MirRepSetupInstruction repSetup:
                sb.Append("rep.setup ");
                sb.Append(formatter.Format(repSetup.Count));
                break;

            case MirRepIterInstruction repIter:
                sb.Append("rep.iter ");
                sb.Append(formatter.Format(repIter.Count));
                break;

            case MirRepForSetupInstruction repForSetup:
                sb.Append("repfor.setup ");
                sb.Append(formatter.Format(repForSetup.Start));
                sb.Append(", ");
                sb.Append(formatter.Format(repForSetup.End));
                break;

            case MirRepForIterInstruction repForIter:
                sb.Append("repfor.iter ");
                sb.Append(formatter.Format(repForIter.Start));
                sb.Append(", ");
                sb.Append(formatter.Format(repForIter.End));
                break;

            case MirNoIrqBeginInstruction:
                sb.Append("noirq.begin");
                break;

            case MirNoIrqEndInstruction:
                sb.Append("noirq.end");
                break;
        }

        if (instruction.HasSideEffects)
            sb.Append(" ; sidefx");
        sb.AppendLine();
    }

    private static void WriteTerminator(StringBuilder sb, MirTerminator terminator, ValueFormatter formatter, BlockFormatter blockFormatter)
    {
        sb.Append("    ");
        switch (terminator)
        {
            case MirGotoTerminator mirGoto:
                sb.Append("goto ");
                sb.Append(blockFormatter.Format(mirGoto.Target));
                sb.Append('(');
                WriteValueList(sb, mirGoto.Arguments, formatter);
                sb.AppendLine(")");
                break;

            case MirBranchTerminator branch:
                sb.Append("branch ");
                sb.Append(formatter.Format(branch.Condition));
                if (branch.ConditionFlag is not null)
                {
                    sb.Append(" [flag:");
                    sb.Append(branch.ConditionFlag.Value.ToString());
                    sb.Append(']');
                }
                sb.Append(" ? ");
                sb.Append(blockFormatter.Format(branch.TrueTarget));
                sb.Append('(');
                WriteValueList(sb, branch.TrueArguments, formatter);
                sb.Append(") : ");
                sb.Append(blockFormatter.Format(branch.FalseTarget));
                sb.Append('(');
                WriteValueList(sb, branch.FalseArguments, formatter);
                sb.AppendLine(")");
                break;

            case MirReturnTerminator ret:
                sb.Append("ret ");
                WriteValueList(sb, ret.Values, formatter);
                sb.AppendLine();
                break;

            case MirUnreachableTerminator:
                sb.AppendLine("unreachable");
                break;
        }
    }

    private static void WriteValueList(StringBuilder sb, IReadOnlyList<MirValueId> values, ValueFormatter formatter)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(formatter.Format(values[i]));
        }
    }

    private static string FormatConstant(BladeValue? value)
    {
        return value?.Value switch
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

    private sealed class ValueFormatter
    {
        private readonly Dictionary<MirValueId, int> _ids = [];

        public string Format(MirValueId value)
        {
            if (!_ids.TryGetValue(value, out int id))
            {
                id = _ids.Count;
                _ids.Add(value, id);
            }

            return $"%v{id}";
        }
    }

    private sealed class BlockFormatter
    {
        private readonly Dictionary<MirBlockRef, int> _ids = [];

        public BlockFormatter(IReadOnlyList<MirBlock> blocks)
        {
            for (int i = 0; i < blocks.Count; i++)
                _ids[blocks[i].Ref] = i;
        }

        public string Format(MirBlockRef blockRef)
            => $"bb{_ids[blockRef]}";
    }
}
