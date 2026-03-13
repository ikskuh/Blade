using System;
using System.Collections.Generic;
using System.Globalization;
using Blade.IR.Lir;

namespace Blade.IR.Asm;

public static class AsmLowerer
{
    public static AsmModule Lower(LirModule module)
    {
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            functions.Add(LowerFunction(function));
        return new AsmModule(functions);
    }

    private static AsmFunction LowerFunction(LirFunction function)
    {
        List<AsmNode> nodes = [];
        nodes.Add(new AsmDirectiveNode($"function {function.Name}"));
        foreach (LirBlock block in function.Blocks)
        {
            string blockLabel = $"{function.Name}_{block.Label}";
            nodes.Add(new AsmLabelNode(blockLabel));

            foreach (LirBlockParameter parameter in block.Parameters)
            {
                nodes.Add(new AsmInstructionNode(
                    "PARAM",
                    [$"{parameter.Register}", parameter.Type.Name, parameter.Name],
                    predicate: null));
            }

            foreach (LirInstruction instruction in block.Instructions)
                nodes.Add(LowerInstruction(instruction));

            foreach (AsmInstructionNode terminatorInstruction in LowerTerminator(function.Name, block.Terminator))
                nodes.Add(terminatorInstruction);
        }

        return new AsmFunction(function.Name, function.IsEntryPoint, nodes);
    }

    private static AsmInstructionNode LowerInstruction(LirInstruction instruction)
    {
        List<string> operands = [];
        if (instruction.Destination is LirVirtualRegister destination)
            operands.Add(destination.ToString());

        foreach (LirOperand operand in instruction.Operands)
            operands.Add(FormatOperand(operand));

        string opcode = NormalizeOpcode(instruction.Opcode);
        return new AsmInstructionNode(opcode, operands, instruction.Predicate);
    }

    private static IEnumerable<AsmInstructionNode> LowerTerminator(string functionName, LirTerminator terminator)
    {
        switch (terminator)
        {
            case LirGotoTerminator mirGoto:
                yield return new AsmInstructionNode(
                    "JMP",
                    [FormatBlockReference(functionName, mirGoto.TargetLabel), FormatOperandList(mirGoto.Arguments)],
                    predicate: null);
                break;

            case LirBranchTerminator branch:
                yield return new AsmInstructionNode(
                    "BRANCH",
                    [
                        FormatOperand(branch.Condition),
                        FormatBlockReference(functionName, branch.TrueLabel),
                        FormatOperandList(branch.TrueArguments),
                        FormatBlockReference(functionName, branch.FalseLabel),
                        FormatOperandList(branch.FalseArguments),
                    ],
                    predicate: null);
                break;

            case LirReturnTerminator ret:
                yield return new AsmInstructionNode("RET", [FormatOperandList(ret.Values)], predicate: null);
                break;

            case LirUnreachableTerminator:
                yield return new AsmInstructionNode("TRAP", [], predicate: null);
                break;
        }
    }

    private static string FormatOperandList(IReadOnlyList<LirOperand> operands)
    {
        if (operands.Count == 0)
            return "()";

        List<string> parts = new(operands.Count);
        foreach (LirOperand operand in operands)
            parts.Add(FormatOperand(operand));
        return $"({string.Join(", ", parts)})";
    }

    private static string FormatBlockReference(string functionName, string blockLabel) => $"{functionName}_{blockLabel}";

    private static string NormalizeOpcode(string opcode)
    {
        return opcode
            .Replace('.', '_')
            .Replace(':', '_')
            .Replace('@', '_')
            .ToUpperInvariant();
    }

    private static string FormatOperand(LirOperand operand)
    {
        return operand switch
        {
            LirRegisterOperand register => register.Register.ToString(),
            LirSymbolOperand symbol => symbol.Symbol,
            LirImmediateOperand immediate => FormatImmediate(immediate.Value),
            _ => "<?>",
        };
    }

    private static string FormatImmediate(object? value)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "1" : "0",
            string s => $"\"{s}\"",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? "<?>",
        };
    }
}
