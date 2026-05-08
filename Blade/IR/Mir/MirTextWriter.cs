using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Blade.Reports;
using Blade.Semantics;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade.IR.Mir;

/// <summary>
/// Emits MIR modules as human-readable text.
/// </summary>
public static class MirTextWriter
{
    /// <summary>
    /// Emits the supplied modules into the provided report builder.
    /// </summary>
    public static void Write(ITextReportBuilder builder, IReadOnlyList<MirModule> modules)
    {
        Requires.NotNull(builder);
        Requires.NotNull(modules);

        Writer writer = new(builder);
        writer.WriteModules(modules);
    }

    /// <summary>
    /// Renders one MIR module as plain text.
    /// </summary>
    public static string Write(MirModule module)
    {
        Requires.NotNull(module);
        return Write([module]);
    }

    /// <summary>
    /// Renders the supplied modules as plain text.
    /// </summary>
    public static string Write(IReadOnlyList<MirModule> modules)
    {
        Requires.NotNull(modules);

        StringBuilder builder = new();
        Write(new PlainTextReportBuilder(builder), modules);
        return builder.ToString();
    }

    private sealed class Writer(ITextReportBuilder builder) : TextReportBuilderBase(builder)
    {
        public void WriteModules(IReadOnlyList<MirModule> modules)
        {
            AppendLine((Comment, "; MIR v1"));
            NewLine();

            foreach (MirModule module in modules)
            {
                AppendLine((Comment, $"; image {module.Image.Task.Name}"));
                NewLine();

                foreach (MirFunction function in module.Functions)
                    WriteFunction(function);
            }
        }

        private void WriteFunction(MirFunction function)
        {
            ValueFormatter formatter = new();
            BlockFormatter blockFormatter = new(function.Blocks);

            Append((Keyword, "fn"), ' ', (FunctionName, function, function.Name), ' ', (Keyword, "kind"), '=', (Literal, function.Kind.ToString()));
            if (function.IsEntryPoint)
                Append(' ', (Keyword, "entry"));
            Append(' ', (Keyword, "returns"), '=', '(');
            for (int i = 0; i < function.ReturnTypes.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                Append((TypeName, function.ReturnTypes[i], function.ReturnTypes[i].Name));
            }

            AppendLine(')');
            AppendLine('{');

            foreach (MirBlock block in function.Blocks)
                WriteBlock(block, formatter, blockFormatter);

            AppendLine('}');
            NewLine();
        }

        private void WriteBlock(MirBlock block, ValueFormatter formatter, BlockFormatter blockFormatter)
        {
            Append(Space(2));
            WriteBlockRef(block.Ref, blockFormatter);
            Append('(');
            for (int i = 0; i < block.Parameters.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');

                MirBlockParameter parameter = block.Parameters[i];
                WriteValue(parameter.Value, formatter);
                Append(':');
                Append(TypeName, parameter.Type, parameter.Type.Name);
                Append(' ');
                Append(VariableName, parameter, parameter.Name);
            }

            AppendLine(')', ':');
            foreach (MirInstruction instruction in block.Instructions)
                WriteInstruction(instruction, formatter);

            WriteTerminator(block.Terminator, formatter, blockFormatter);
        }

        private void WriteInstruction(MirInstruction instruction, ValueFormatter formatter)
        {
            Append(Space(4));
            if (instruction.Result is MirValueId result)
            {
                WriteValue(result, formatter);
                Append(':');
                if (instruction.ResultType is not null)
                    Append(TypeName, instruction.ResultType, instruction.ResultType.Name);
                else
                    Append(Literal, "<unknown>");
                Append(' ', '=', ' ');
            }

            switch (instruction)
            {
                case MirConstantInstruction constant:
                    Append((Keyword, "const"), ' ', (Literal, FormatConstant(constant.Value)));
                    break;

                case MirLoadPlaceInstruction loadPlace:
                    Append((Keyword, "load.place"), ' ', (VariableName, loadPlace.Place, loadPlace.Place.EmittedName));
                    break;

                case MirCopyInstruction copy:
                    Append((Keyword, "copy"), ' ');
                    WriteValue(copy.Source, formatter);
                    break;

                case MirUnaryInstruction unary:
                    Append((Keyword, "unary"), '.', (Literal, unary.Operator.ToString()), ' ');
                    WriteValue(unary.Operand, formatter);
                    break;

                case MirBinaryInstruction binary:
                    Append((Keyword, "binary"), '.', (Literal, binary.Operator.ToString()));
                    if (binary.ComparisonLoweringKind != ComparisonLoweringKind.Default)
                        Append('[', (Literal, binary.ComparisonLoweringKind.ToString()), ']');
                    Append(' ');
                    WriteValue(binary.Left, formatter);
                    Append(',', ' ');
                    WriteValue(binary.Right, formatter);
                    break;

                case MirPointerOffsetInstruction pointerOffset:
                    Append((Keyword, "ptr.offset"), '.', (Literal, pointerOffset.OperatorKind.ToString()), '[', (Literal, pointerOffset.Stride.ToString(CultureInfo.InvariantCulture)), ']', ' ');
                    WriteValue(pointerOffset.BaseAddress, formatter);
                    Append(',', ' ');
                    WriteValue(pointerOffset.Delta, formatter);
                    break;

                case MirPointerDifferenceInstruction pointerDifference:
                    Append((Keyword, "ptr.diff"), '[', (Literal, pointerDifference.Stride.ToString(CultureInfo.InvariantCulture)), ']', ' ');
                    WriteValue(pointerDifference.Left, formatter);
                    Append(',', ' ');
                    WriteValue(pointerDifference.Right, formatter);
                    break;

                case MirConvertInstruction convert:
                    Append((Keyword, "convert"), ' ');
                    WriteValue(convert.Operand, formatter);
                    break;

                case MirStructLiteralInstruction structLiteral:
                    Append((Keyword, "structlit"));
                    foreach (MirStructLiteralField field in structLiteral.Fields)
                        Append('.', (Literal, field.Member.Name));
                    if (structLiteral.Fields.Count > 0)
                    {
                        Append(' ');
                        for (int i = 0; i < structLiteral.Fields.Count; i++)
                        {
                            if (i > 0)
                                Append(',', ' ');
                            WriteValue(structLiteral.Fields[i].Value, formatter);
                        }
                    }
                    break;

                case MirLoadMemberInstruction loadMember:
                    Append((Keyword, "load.member"), '.', (Literal, loadMember.Member.Name), '.', (Literal, loadMember.Member.ByteOffset.ToString(CultureInfo.InvariantCulture)), ' ');
                    WriteValue(loadMember.Receiver, formatter);
                    break;

                case MirLoadIndexInstruction loadIndex:
                    Append((Keyword, "load.index"), '.', (Literal, FormatStorageClass(loadIndex.StorageClass)), ' ');
                    WriteValue(loadIndex.Indexed, formatter);
                    Append(',', ' ');
                    WriteValue(loadIndex.Index, formatter);
                    break;

                case MirLoadDerefInstruction loadDeref:
                    Append((Keyword, "load.deref"), '.', (Literal, FormatStorageClass(loadDeref.StorageClass)), ' ');
                    WriteValue(loadDeref.Address, formatter);
                    break;

                case MirBitfieldExtractInstruction extract:
                    Append((Keyword, "bitfield.extract"), '.', (Literal, extract.Member.BitOffset.ToString(CultureInfo.InvariantCulture)), '.', (Literal, extract.Member.BitWidth.ToString(CultureInfo.InvariantCulture)), ' ');
                    WriteValue(extract.Receiver, formatter);
                    break;

                case MirBitfieldInsertInstruction insertBitfield:
                    Append((Keyword, "bitfield.insert"), '.', (Literal, insertBitfield.Member.BitOffset.ToString(CultureInfo.InvariantCulture)), '.', (Literal, insertBitfield.Member.BitWidth.ToString(CultureInfo.InvariantCulture)), ' ');
                    WriteValue(insertBitfield.Receiver, formatter);
                    Append(',', ' ');
                    WriteValue(insertBitfield.Value, formatter);
                    break;

                case MirInsertMemberInstruction insertMember:
                    Append((Keyword, "insert.member"), '.', (Literal, insertMember.Member.Name), '.', (Literal, insertMember.Member.ByteOffset.ToString(CultureInfo.InvariantCulture)), ' ');
                    WriteValue(insertMember.Receiver, formatter);
                    Append(',', ' ');
                    WriteValue(insertMember.Value, formatter);
                    break;

                case MirCallInstruction call:
                    Append((Keyword, "call"), ' ', (FunctionName, call.Function, call.Function.Name), '(');
                    WriteValueList(call.Arguments, formatter);
                    Append(')');
                    if (call.ExtraResults.Count > 0)
                    {
                        Append(' ', (Keyword, "extra"), '=', '[');
                        for (int i = 0; i < call.ExtraResults.Count; i++)
                        {
                            if (i > 0)
                                Append(',', ' ');
                            WriteValue(call.ExtraResults[i].Value, formatter);
                            Append(':');
                            Append(TypeName, call.ExtraResults[i].Type, call.ExtraResults[i].Type.Name);
                        }
                        Append(']');
                    }
                    break;

                case MirIntrinsicCallInstruction intrinsic:
                    Append((Keyword, "intrinsic"), ' ', '@', (Literal, intrinsic.Mnemonic.ToString()), '(');
                    WriteValueList(intrinsic.Arguments, formatter);
                    Append(')');
                    break;

                case MirStoreIndexInstruction storeIndex:
                    Append((Keyword, "store"), ' ', (Keyword, "index"), '.', (Literal, FormatStorageClass(storeIndex.StorageClass)), '(');
                    WriteValue(storeIndex.Indexed, formatter);
                    Append(',', ' ');
                    WriteValue(storeIndex.Index, formatter);
                    Append(',', ' ');
                    WriteValue(storeIndex.Value, formatter);
                    Append(')');
                    break;

                case MirStoreDerefInstruction storeDeref:
                    Append((Keyword, "store"), ' ', (Keyword, "deref"), '.', (Literal, FormatStorageClass(storeDeref.StorageClass)), '(');
                    WriteValue(storeDeref.Address, formatter);
                    Append(',', ' ');
                    WriteValue(storeDeref.Value, formatter);
                    Append(')');
                    break;

                case MirStorePlaceInstruction storePlace:
                    Append((Keyword, "store.place"), ' ', (VariableName, storePlace.Place, storePlace.Place.EmittedName), '(');
                    WriteValue(storePlace.Value, formatter);
                    Append(')');
                    break;

                case MirUpdatePlaceInstruction updatePlace:
                    Append((Keyword, "update.place"), ' ', (VariableName, updatePlace.Place, updatePlace.Place.EmittedName), ' ', (Literal, updatePlace.OperatorKind.ToString()));
                    if (updatePlace.PointerArithmeticStride is int stride)
                        Append('[', (Literal, stride.ToString(CultureInfo.InvariantCulture)), ']');
                    Append(' ');
                    WriteValue(updatePlace.Value, formatter);
                    break;

                case MirInlineAsmInstruction inlineAsm:
                    Append((Keyword, inlineAsm.Volatility == AsmVolatility.Volatile ? "inlineasm.volatile" : "inlineasm"));
                    if (inlineAsm.FlagOutput is not null)
                        Append(' ', '-', '>', ' ', '@', (Literal, Assert.NotNull(inlineAsm.FlagOutput.ToString())));
                    if (inlineAsm.Bindings.Count > 0)
                    {
                        Append(' ');
                        for (int i = 0; i < inlineAsm.Bindings.Count; i++)
                        {
                            if (i > 0)
                                Append(',', ' ');

                            MirInlineAsmBinding binding = inlineAsm.Bindings[i];
                            Append((Literal, binding.PlaceholderText), '=');
                            if (binding.Value is MirValueId value)
                                WriteValue(value, formatter);
                            else
                                Append((VariableName, Assert.NotNull(binding.Place), Assert.NotNull(binding.Place?.EmittedName)));
                            Append(':', (Literal, FormatInlineAsmAccess(binding.Access)));
                        }
                    }
                    break;

                case MirYieldInstruction:
                    Append((Keyword, "yield"));
                    break;

                case MirYieldToInstruction yieldTo:
                    Append((Keyword, "yieldto"), ':', (FunctionName, yieldTo.TargetFunction, yieldTo.TargetFunction.Name));
                    if (yieldTo.Arguments.Count > 0)
                    {
                        Append(' ');
                        WriteValueList(yieldTo.Arguments, formatter);
                    }
                    break;

                case MirRepSetupInstruction repSetup:
                    Append((Keyword, "rep.setup"), ' ');
                    WriteValue(repSetup.Count, formatter);
                    break;

                case MirRepIterInstruction repIter:
                    Append((Keyword, "rep.iter"), ' ');
                    WriteValue(repIter.Count, formatter);
                    break;

                case MirRepForSetupInstruction repForSetup:
                    Append((Keyword, "repfor.setup"), ' ');
                    WriteValue(repForSetup.Start, formatter);
                    Append(',', ' ');
                    WriteValue(repForSetup.End, formatter);
                    break;

                case MirRepForIterInstruction repForIter:
                    Append((Keyword, "repfor.iter"), ' ');
                    for (int i = 0; i < repForIter.CarrierValues.Count; i++)
                    {
                        if (i > 0)
                            Append(',', ' ');

                        WriteValue(repForIter.CarrierValues[i], formatter);
                        Append(' ', '<', '-', ' ');
                        WriteValue(repForIter.CurrentValues[i], formatter);
                        if (repForIter.IndexCarrierOrdinal == i)
                            Append(' ', '[', '+', (Literal, "1"), ']');
                    }
                    break;

                case MirNoIrqBeginInstruction:
                    Append((Keyword, "noirq.begin"));
                    break;

                case MirNoIrqEndInstruction:
                    Append((Keyword, "noirq.end"));
                    break;

                default:
                    Assert.Unreachable($"Unhandled MIR instruction '{instruction.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }

            if (instruction.HasSideEffects)
                Append(' ', (Comment, "; sidefx"));
            NewLine();
        }

        private void WriteTerminator(MirTerminator terminator, ValueFormatter formatter, BlockFormatter blockFormatter)
        {
            Append(Space(4));
            switch (terminator)
            {
                case MirGotoTerminator mirGoto:
                    Append((Keyword, "goto"), ' ');
                    WriteBlockRef(mirGoto.Target, blockFormatter);
                    Append('(');
                    WriteValueList(mirGoto.Arguments, formatter);
                    AppendLine(')');
                    break;

                case MirBranchTerminator branch:
                    Append((Keyword, "branch"), ' ', (Keyword, "cond"), '=');
                    WriteValue(branch.Condition, formatter);
                    if (branch.ConditionFlag is not null)
                        Append(' ', '[', (Keyword, "flag"), ':', (Literal, branch.ConditionFlag.Value.ToString()), ']');
                    Append(',', ' ', (Keyword, "true"), '=');
                    WriteBlockRef(branch.TrueTarget, blockFormatter);
                    Append('(');
                    WriteValueList(branch.TrueArguments, formatter);
                    Append(')', ',', ' ', (Keyword, "false"), '=');
                    WriteBlockRef(branch.FalseTarget, blockFormatter);
                    Append('(');
                    WriteValueList(branch.FalseArguments, formatter);
                    AppendLine(')');
                    break;

                case MirReturnTerminator ret:
                    Append((Keyword, "ret"), ' ');
                    WriteValueList(ret.Values, formatter);
                    NewLine();
                    break;

                case MirUnreachableTerminator:
                    AppendLine((Keyword, "unreachable"));
                    break;

                default:
                    Assert.Unreachable($"Unhandled MIR terminator '{terminator.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void WriteValueList(IReadOnlyList<MirValueId> values, ValueFormatter formatter)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                WriteValue(values[i], formatter);
            }
        }

        private void WriteValue(MirValueId value, ValueFormatter formatter)
        {
            Append(VariableName, value, formatter.Format(value));
        }

        private void WriteBlockRef(MirBlockRef blockRef, BlockFormatter formatter)
        {
            Append(VariableName, blockRef, formatter.Format(blockRef));
        }
    }

    private static string FormatConstant(BladeValue? value)
    {
        return value?.Format() ?? "null";
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

    private static string FormatStorageClass(AddressSpace storageClass)
    {
        return storageClass switch
        {
            AddressSpace.Lut => "lut",
            AddressSpace.Hub => "hub",
            _ => "cog",
        };
    }

    private sealed class ValueFormatter
    {
        private readonly Dictionary<VirtualMirFlag, string> _flagIds = [];
        private readonly Dictionary<VirtualMirRegister, string> _registerIds = [];

        public string Format(MirValueId value)
        {
            return value switch
            {
                VirtualMirRegister register => Format(register),
                VirtualMirFlag flag => Format(flag),
                _ => Assert.UnreachableValue<string>(),
            };
        }

        private string Format(VirtualMirRegister register)
        {
            if (!_registerIds.TryGetValue(register, out string? registerId))
            {
                registerId = $"%v{_registerIds.Count}";
                _registerIds.Add(register, registerId);
            }

            return registerId;
        }

        private string Format(VirtualMirFlag flag)
        {
            if (!_flagIds.TryGetValue(flag, out string? flagId))
            {
                flagId = $"%f{_flagIds.Count}";
                _flagIds.Add(flag, flagId);
            }

            return flagId;
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
        {
            return $"bb{_ids[blockRef]}";
        }
    }
}
