using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Blade;
using Blade.Semantics;

namespace Blade.IR.Lir;

public static class LirTextWriter
{
    public static string Write(LirModule module)
    {
        Requires.NotNull(module);

        StringBuilder sb = new();
        sb.AppendLine("; LIR v1");
        sb.AppendLine();

        foreach (LirFunction function in module.Functions)
            WriteFunction(sb, function);

        return sb.ToString();
    }

    private static void WriteFunction(StringBuilder sb, LirFunction function)
    {
        RegisterFormatter formatter = new();
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
        foreach (LirBlock block in function.Blocks)
            WriteBlock(sb, block, formatter, blockFormatter);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteBlock(StringBuilder sb, LirBlock block, RegisterFormatter formatter, BlockFormatter blockFormatter)
    {
        sb.Append("  ");
        sb.Append(blockFormatter.Format(block.Ref));
        sb.Append('(');
        for (int i = 0; i < block.Parameters.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            LirBlockParameter parameter = block.Parameters[i];
            sb.Append(formatter.Format(parameter.Register));
            sb.Append(':');
            sb.Append(parameter.Type.Name);
            sb.Append(' ');
            sb.Append(parameter.Name);
        }

        sb.AppendLine("):");

        foreach (LirInstruction instruction in block.Instructions)
            WriteInstruction(sb, instruction, formatter);

        WriteTerminator(sb, block.Terminator, formatter, blockFormatter);
    }

    private static void WriteInstruction(StringBuilder sb, LirInstruction instruction, RegisterFormatter formatter)
    {
        if (instruction is LirInlineAsmInstruction inlineAsm)
        {
            WriteInlineAsmInstruction(sb, inlineAsm, formatter);
            return;
        }

        sb.Append("    ");
        if (instruction.Destination is LirVirtualRegister destination)
        {
            sb.Append(formatter.Format(destination));
            sb.Append(':');
            sb.Append(instruction.ResultType?.Name ?? "<unknown>");
            sb.Append(" = ");
        }

        if (instruction.Predicate is P2ConditionCode predicate)
        {
            sb.Append('[');
            sb.Append(FormatPredicate(predicate));
            sb.Append("] ");
        }

        sb.Append(instruction.DisplayName);
        sb.Append(' ');
        WriteOperandList(sb, instruction.Operands, formatter);
        if (instruction.WritesC || instruction.WritesZ)
        {
            sb.Append(" flags=");
            if (instruction.WritesC)
                sb.Append('C');
            if (instruction.WritesZ)
                sb.Append('Z');
        }

        if (instruction.HasSideEffects)
            sb.Append(" ; sidefx");
        sb.AppendLine();
    }

    private static void WriteInlineAsmInstruction(StringBuilder sb, LirInlineAsmInstruction instruction, RegisterFormatter formatter)
    {
        sb.Append("    ");
        sb.Append(instruction.Volatility == AsmVolatility.Volatile ? "inlineasm.volatile" : "inlineasm");
        if (instruction.FlagOutput is not null)
        {
            sb.Append(" -> @");
            sb.Append(instruction.FlagOutput);
        }

        if (instruction.Bindings.Count > 0)
        {
            sb.Append(' ');
            for (int i = 0; i < instruction.Bindings.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                LirInlineAsmBinding binding = instruction.Bindings[i];
                sb.Append(binding.PlaceholderText);
                sb.Append('=');
                sb.Append(FormatOperand(binding.Operand, formatter));
                sb.Append(':');
                sb.Append(FormatInlineAsmAccess(binding.Access));
            }
        }

        sb.Append(" ; sidefx");
        sb.AppendLine();
    }

    private static void WriteTerminator(StringBuilder sb, LirTerminator terminator, RegisterFormatter formatter, BlockFormatter blockFormatter)
    {
        sb.Append("    ");
        switch (terminator)
        {
            case LirGotoTerminator gotoTerminator:
                sb.Append("goto ");
                sb.Append(blockFormatter.Format(gotoTerminator.Target));
                sb.Append('(');
                WriteOperandList(sb, gotoTerminator.Arguments, formatter);
                sb.AppendLine(")");
                break;

            case LirBranchTerminator branchTerminator:
                sb.Append("branch cond=");
                sb.Append(FormatOperand(branchTerminator.Condition, formatter));
                sb.Append(", true=");
                sb.Append(blockFormatter.Format(branchTerminator.TrueTarget));
                sb.Append('(');
                WriteOperandList(sb, branchTerminator.TrueArguments, formatter);
                sb.Append("), false=");
                sb.Append(blockFormatter.Format(branchTerminator.FalseTarget));
                sb.Append('(');
                WriteOperandList(sb, branchTerminator.FalseArguments, formatter);
                sb.AppendLine(")");
                break;

            case LirReturnTerminator returnTerminator:
                sb.Append("ret ");
                WriteOperandList(sb, returnTerminator.Values, formatter);
                sb.AppendLine();
                break;

            case LirUnreachableTerminator:
                sb.AppendLine("unreachable");
                break;
        }
    }

    private static void WriteOperandList(StringBuilder sb, IReadOnlyList<LirOperand> operands, RegisterFormatter formatter)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(FormatOperand(operands[i], formatter));
        }
    }

    private static string FormatOperand(LirOperand operand, RegisterFormatter formatter)
    {
        return operand switch
        {
            LirRegisterOperand register => formatter.Format(register.Register),
            LirImmediateOperand immediate => $"{immediate.Value.Format()}:{immediate.Type.Name}",
            LirPlaceOperand place => $"%place({place.Place.EmittedName})",
            _ => Assert.UnreachableValue<string>($"Unhandled class {operand.GetType()}"),
        };
    }

    private static string FormatPredicate(P2ConditionCode predicate)
    {
        string text = P2InstructionMetadata.GetConditionPrefixText(predicate);
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

        public string Format(LirVirtualRegister register)
        {
            if (!_ids.TryGetValue(register, out int id))
            {
                id = _ids.Count;
                _ids.Add(register, id);
            }

            return $"%r{id}";
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
            => $"bb{_ids[blockRef]}";
    }
}
