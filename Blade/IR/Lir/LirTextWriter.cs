using System;
using System.Collections.Generic;
using System.Text;
using Blade.Reports;
using Blade.Semantics;

using static Blade.Reports.BasicTextSpanKind;
using static Blade.Reports.SemanticTextSpanKind;

namespace Blade.IR.Lir;

/// <summary>
/// Emits LIR modules as human-readable text.
/// </summary>
public static class LirTextWriter
{
    /// <summary>
    /// Emits the supplied modules into the provided report builder.
    /// </summary>
    public static void Write(ITextReportBuilder builder, IReadOnlyList<LirModule> modules)
    {
        Requires.NotNull(builder);
        Requires.NotNull(modules);

        Writer writer = new(builder);
        writer.WriteModules(modules);
    }


    private sealed class Writer(ITextReportBuilder builder) : TextReportBuilderBase(builder)
    {
        public void WriteModules(IReadOnlyList<LirModule> modules)
        {
            AppendLine((Comment, "; LIR v1"));
            NewLine();

            foreach (LirModule module in modules)
            {
                AppendLine((Comment, $"; image {module.Image.Task.Name}"));
                NewLine();

                foreach (LirFunction function in module.Functions)
                    WriteFunction(function);
            }
        }

        private void WriteFunction(LirFunction function)
        {
            RegisterFormatter formatter = new();
            BlockFormatter blockFormatter = new(function.Blocks);

            Append((Keyword, "fn"), ' ', (FunctionName, function, function.Name), ' ', (Keyword, "kind"), '=', (Literal, function.Kind.ToString()));
            if (function.IsEntryPoint)
                Append(' ', (Keyword, "entry"));
            Append(' ', (Keyword, "returns"), '=', '(');
            for (int i = 0; i < function.ReturnTypes.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                AppendType(function.ReturnTypes[i]);
            }

            AppendLine(')');
            AppendLine('{');
            foreach (LirBlock block in function.Blocks)
                WriteBlock(block, formatter, blockFormatter);
            AppendLine('}');
            NewLine();
        }

        private void WriteBlock(LirBlock block, RegisterFormatter formatter, BlockFormatter blockFormatter)
        {
            Append(Space(2));
            WriteBlockRef(block.Ref, blockFormatter);
            Append('(');
            for (int i = 0; i < block.Parameters.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');

                LirBlockParameter parameter = block.Parameters[i];
                WriteValue(parameter.Value, formatter);
                Append(':');
                AppendType(parameter.Type);
                Append(' ');
                Append(VariableName, parameter, parameter.Name);
            }

            AppendLine(')', ':');

            foreach (LirInstruction instruction in block.Instructions)
                WriteInstruction(instruction, formatter);

            WriteTerminator(block.Terminator, formatter, blockFormatter);
        }

        private void WriteInstruction(LirInstruction instruction, RegisterFormatter formatter)
        {
            Append(Space(4));

            if (instruction.Destination is VirtualLirValue destination)
            {
                WriteValue(destination, formatter);
                Append(':');
                if (instruction.ResultType is not null)
                    AppendType(instruction.ResultType);
                else
                    Append(Literal, "<unknown>");
                Append(' ', '=', ' ');
            }

            WriteInstructionModifiers(instruction);

            if (instruction is LirInlineAsmInstruction inlineAsm)
            {
                WriteInlineAsmInstruction(inlineAsm, formatter);
                return;
            }

            Append(Keyword, instruction.DisplayName);
            Append(' ');
            WriteOperandList(instruction.Operands, formatter);
            if (instruction.WritesC || instruction.WritesZ)
            {
                Append(' ', (Keyword, "flags"), '=');
                if (instruction.WritesC)
                    Append(Literal, "C");
                if (instruction.WritesZ)
                    Append(Literal, "Z");
            }
            NewLine();
        }

        private void WriteInlineAsmInstruction(LirInlineAsmInstruction instruction, RegisterFormatter formatter)
        {
            WriteInlineAsmKeyword(instruction.Volatility);
            if (instruction.FlagOutput is not null)
            {
                Append(' ', '-', '>', ' ', '@', (Keyword, instruction.FlagOutput.Value.ToString()));
            }

            if (instruction.Bindings.Count > 0)
            {
                Append(' ');
                for (int i = 0; i < instruction.Bindings.Count; i++)
                {
                    if (i > 0)
                        Append(',', ' ');

                    LirInlineAsmBinding binding = instruction.Bindings[i];
                    Append((Literal, binding.PlaceholderText), '=');
                    WriteOperand(binding.Operand, formatter);
                    Append(':', (Literal, FormatInlineAsmAccess(binding.Access)));
                }
            }

            NewLine();
        }

        private void WriteInstructionModifiers(LirInstruction instruction)
        {
            if (instruction.Predicate is P2ConditionCode predicate)
                Append('[', (Literal, FormatPredicate(predicate)), ']', ' ');

            if (instruction.HasSideEffects)
                Append((Keyword, "sidefx"), ' ');
        }

        private void WriteInlineAsmKeyword(AsmVolatility volatility)
        {
            if (volatility == AsmVolatility.Volatile)
                Append((Keyword, "volatile"), ' ');

            Append((Keyword, "inlineasm"));
        }

        private void WriteTerminator(LirTerminator terminator, RegisterFormatter formatter, BlockFormatter blockFormatter)
        {
            Append(Space(4));
            switch (terminator)
            {
                case LirGotoTerminator gotoTerminator:
                    Append((Keyword, "goto"), ' ');
                    WriteBlockRef(gotoTerminator.Target, blockFormatter);
                    Append('(');
                    WriteOperandList(gotoTerminator.Arguments, formatter);
                    AppendLine(')');
                    break;

                case LirBranchTerminator branchTerminator:
                    Append((Keyword, "branch"), ' ', (Keyword, "cond"), '=');
                    WriteOperand(branchTerminator.Condition, formatter);
                    Append(',', ' ', (Keyword, "true"), '=');
                    WriteBlockRef(branchTerminator.TrueTarget, blockFormatter);
                    Append('(');
                    WriteOperandList(branchTerminator.TrueArguments, formatter);
                    Append(')', ',', ' ', (Keyword, "false"), '=');
                    WriteBlockRef(branchTerminator.FalseTarget, blockFormatter);
                    Append('(');
                    WriteOperandList(branchTerminator.FalseArguments, formatter);
                    AppendLine(')');
                    break;

                case LirReturnTerminator returnTerminator:
                    Append((Keyword, "ret"), ' ');
                    WriteOperandList(returnTerminator.Values, formatter);
                    NewLine();
                    break;

                default:
                    Assert.Unreachable($"Unhandled LIR terminator '{terminator.GetType().Name}'."); // pragma: force-coverage
                    break; // pragma: force-coverage
            }
        }

        private void WriteOperandList(IReadOnlyList<LirOperand> operands, RegisterFormatter formatter)
        {
            for (int i = 0; i < operands.Count; i++)
            {
                if (i > 0)
                    Append(',', ' ');
                WriteOperand(operands[i], formatter);
            }
        }

        private void WriteOperand(LirOperand operand, RegisterFormatter formatter)
        {
            switch (operand)
            {
                case LirValueOperand value:
                    WriteValue(value.Value, formatter);
                    return;

                case LirRegisterOperand register:
                    WriteValue(register.Register, formatter);
                    return;

                case LirFlagOperand flag:
                    WriteValue(flag.Flag, formatter);
                    return;

                case LirImmediateOperand immediate:
                    Append((Literal, immediate.Value.Format()), ':');
                    AppendType(immediate.Type);
                    return;

                case LirPlaceOperand place:
                    Append((Keyword, "%place"), '(');
                    Append((VariableName, place.Place, place.Place.EmittedName));
                    Append(')');
                    return;

                default:
                    Assert.Unreachable($"Unhandled LIR operand '{operand.GetType().Name}'."); // pragma: force-coverage
                    return; // pragma: force-coverage
            }
        }

        private void WriteValue(VirtualLirValue value, RegisterFormatter formatter)
        {
            Append(VariableName, value, formatter.Format(value));
        }

        private void WriteBlockRef(LirBlockRef blockRef, BlockFormatter formatter)
        {
            Append(VariableName, blockRef, formatter.Format(blockRef));
        }
    }

    private static string FormatPredicate(P2ConditionCode predicate)
    {
        string text = P2MetadataSyntax.GetConditionPrefixText(predicate);
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = char.ToLowerInvariant(chars[i]);
        return new string(chars);
    }

    private static string FormatInlineAsmAccess(InlineAsmBindingAccess access)
    {
        return access switch
        {
            InlineAsmBindingAccess.Read => "r",
            InlineAsmBindingAccess.Write => "w",
            InlineAsmBindingAccess.ReadWrite => "rw",
            _ => Assert.UnreachableValue<string>(),
        };
    }

    private sealed class RegisterFormatter
    {
        private readonly Dictionary<LirVirtualRegister, int> _ids = [];
        private readonly Dictionary<VirtualLirFlag, int> _flagIds = [];

        public string Format(VirtualLirValue value)
        {
            return value switch
            {
                LirVirtualRegister register => Format(register),
                VirtualLirFlag flag => Format(flag),
                _ => Assert.UnreachableValue<string>(), // pragma: force-coverage
            };
        }

        private string Format(LirVirtualRegister register)
        {
            if (!_ids.TryGetValue(register, out int id))
            {
                id = _ids.Count;
                _ids.Add(register, id);
            }

            return $"%r{id}";
        }

        private string Format(VirtualLirFlag flag)
        {
            if (!_flagIds.TryGetValue(flag, out int id))
            {
                id = _flagIds.Count;
                _flagIds.Add(flag, id);
            }

            return $"%f{id}";
        }
    }

    private sealed class BlockFormatter
    {
        private readonly Dictionary<LirBlockRef, int> _ids = [];

        public BlockFormatter(IReadOnlyList<LirBlock> blocks)
        {
            for (int i = 0; i < blocks.Count; i++)
                _ids[blocks[i].Ref] = i;
        }

        public string Format(LirBlockRef blockRef)
        {
            return $"bb{_ids[blockRef]}";
        }
    }
}
