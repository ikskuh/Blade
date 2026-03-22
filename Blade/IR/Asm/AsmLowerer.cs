using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Blade;
using Blade.Diagnostics;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics;
using Blade.Semantics.Bound;
using Blade.Source;

namespace Blade.IR.Asm;

/// <summary>
/// Lowers LIR (virtual registers, high-level opcodes) to ASMIR
/// (real P2 mnemonics, virtual registers, label-based jumps).
/// Performs instruction selection and calling convention lowering.
/// </summary>
public static class AsmLowerer
{
    public static AsmModule Lower(LirModule module, DiagnosticBag? diagnostics = null)
    {
        Requires.NotNull(module);

        // Run call graph analysis to determine CC tiers and dead functions
        CallGraphResult cgResult = CallGraphAnalyzer.Analyze(module);

        // Build a map of function name → block label → block parameter registers
        // so φ-moves can emit actual MOV instructions to the right target registers.
        Dictionary<string, Dictionary<string, IReadOnlyList<LirBlockParameter>>> blockParamMap = [];
        foreach (LirFunction function in module.Functions)
        {
            Dictionary<string, IReadOnlyList<LirBlockParameter>> funcBlocks = [];
            foreach (LirBlock block in function.Blocks)
                funcBlocks[block.Label] = block.Parameters;
            blockParamMap[function.Name] = funcBlocks;
        }

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            // Eliminate dead (unreachable) functions from codegen
            if (cgResult.DeadFunctions.Contains(function.Name))
                continue;

            CallingConventionTier tier = cgResult.Tiers.GetValueOrDefault(function.Name, CallingConventionTier.General);
            LoweringContext ctx = new(function, functionOrdinal: functions.Count, tier, cgResult.Tiers, blockParamMap[function.Name], diagnostics);
            functions.Add(LowerFunction(ctx));
        }

        return new AsmModule(module.StoragePlaces, functions);
    }

    private sealed class LoweringContext
    {
        public LirFunction Function { get; }
        public int FunctionOrdinal { get; }
        public CallingConventionTier Tier { get; }
        public Dictionary<string, CallingConventionTier> CalleeTiers { get; }
        public Dictionary<string, IReadOnlyList<LirBlockParameter>> BlockParams { get; }
        public DiagnosticBag? Diagnostics { get; }
        public HashSet<string> ReportedUnsupportedLowerings { get; } = new(StringComparer.Ordinal);
        public int NextInlineAsmBlockOrdinal { get; set; }

        /// <summary>
        /// Registers whose values are only consumed as hardware flags (by a flag-aware branch),
        /// not as register values. Comparisons writing to these can skip materialization (WRZ/WRC).
        /// Recomputed per block.
        /// </summary>
        public HashSet<int> FlagOnlyRegisters { get; } = [];

        public LoweringContext(
            LirFunction function,
            int functionOrdinal,
            CallingConventionTier tier,
            Dictionary<string, CallingConventionTier> calleeTiers,
            Dictionary<string, IReadOnlyList<LirBlockParameter>> blockParams,
            DiagnosticBag? diagnostics)
        {
            Function = function;
            FunctionOrdinal = functionOrdinal;
            Tier = tier;
            CalleeTiers = calleeTiers;
            BlockParams = blockParams;
            Diagnostics = diagnostics;
        }
    }

    private static AsmFunction LowerFunction(LoweringContext ctx)
    {
        List<AsmNode> nodes = [];
        nodes.Add(new AsmDirectiveNode($"function {ctx.Function.Name}"));

        foreach (LirBlock block in ctx.Function.Blocks)
        {
            string blockLabel = $"{ctx.Function.Name}_{block.Label}";
            nodes.Add(new AsmLabelNode(blockLabel));

            ComputeFlagOnlyRegisters(ctx, block);

            foreach (LirInstruction instruction in block.Instructions)
                LowerInstruction(nodes, instruction, ctx);

            LowerTerminator(nodes, ctx, block.Terminator);
        }

        return new AsmFunction(ctx.Function.Name, ctx.Function.IsEntryPoint, ctx.Tier, nodes);
    }

    /// <summary>
    /// Identifies registers whose values are consumed only as hardware flags, not as register
    /// values. A comparison writing to such a register can skip materialization (WRZ/WRC).
    /// </summary>
    private static void ComputeFlagOnlyRegisters(LoweringContext ctx, LirBlock block)
    {
        ctx.FlagOnlyRegisters.Clear();

        // Only relevant when the terminator is a flag-aware branch.
        if (block.Terminator is not LirBranchTerminator branch || branch.ConditionFlag is null)
            return;

        Debug.Assert(branch.Condition is LirRegisterOperand, "Flag-aware branch condition must be a register operand");
        if (branch.Condition is not LirRegisterOperand condReg)
            return;

        int condId = condReg.Register.Id;

        // Check that no instruction in the block uses this register as an operand
        // (other than the instruction that defines it).
        foreach (LirInstruction instruction in block.Instructions)
        {
            foreach (LirOperand operand in instruction.Operands)
            {
                if (operand is LirRegisterOperand reg && reg.Register.Id == condId)
                    return; // Used as an operand somewhere — need materialization.
            }
        }

        // Also check terminator arguments (phi moves).
        foreach (LirOperand operand in branch.TrueArguments)
        {
            if (operand is LirRegisterOperand reg && reg.Register.Id == condId)
                return;
        }

        foreach (LirOperand operand in branch.FalseArguments)
        {
            if (operand is LirRegisterOperand reg && reg.Register.Id == condId)
                return;
        }

        ctx.FlagOnlyRegisters.Add(condId);
    }

    private static void LowerInstruction(List<AsmNode> nodes, LirInstruction instruction, LoweringContext ctx)
    {
        if (instruction is LirInlineAsmInstruction inlineAsm)
        {
            LowerInlineAsm(nodes, inlineAsm, ctx);
            return;
        }

        if (instruction is not LirOpInstruction op)
        {
            ReportUnsupportedOpcode(ctx, instruction.Span, instruction.Opcode);
            nodes.Add(new AsmCommentNode($"unknown instruction: {instruction.Opcode}"));
            return;
        }

        switch (op.Opcode)
        {
            case "const":
                LowerConst(nodes, op);
                break;
            case "mov":
                LowerMov(nodes, op);
                break;
            case "load.sym":
                LowerLoadSym(nodes, op);
                break;
            case "load.place":
                LowerLoadPlace(nodes, op);
                break;
            case "select":
                LowerSelect(nodes, op);
                break;
            case "call":
                LowerCall(nodes, op, ctx);
                break;
            case "call.extractC":
                LowerCallExtractFlag(nodes, op, isC: true);
                break;
            case "call.extractZ":
                LowerCallExtractFlag(nodes, op, isC: false);
                break;
            case "intrinsic":
                LowerIntrinsic(nodes, op);
                break;
            case "convert":
                LowerConvert(nodes, op);
                break;
            default:
                if (op.Opcode.StartsWith("structlit.", StringComparison.Ordinal))
                {
                    LowerStructLiteral(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("binary.", StringComparison.Ordinal))
                {
                    LowerBinary(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("unary.", StringComparison.Ordinal))
                {
                    LowerUnary(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("bitfield.extract.", StringComparison.Ordinal))
                {
                    LowerBitfieldExtract(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("bitfield.insert.", StringComparison.Ordinal))
                {
                    LowerBitfieldInsert(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("load.member.", StringComparison.Ordinal))
                {
                    LowerLoadMember(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("load.deref.", StringComparison.Ordinal))
                {
                    LowerLoadDeref(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("load.index.", StringComparison.Ordinal))
                {
                    LowerLoadIndex(nodes, op, ctx);
                }
                else if (op.Opcode == "store.place")
                {
                    LowerStorePlace(nodes, op);
                }
                else if (op.Opcode.StartsWith("update.place.", StringComparison.Ordinal))
                {
                    LowerUpdatePlace(nodes, op);
                }
                else if (op.Opcode.StartsWith("store.deref.", StringComparison.Ordinal))
                {
                    LowerStoreDeref(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("store.index.", StringComparison.Ordinal))
                {
                    LowerStoreIndex(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("insert.member.", StringComparison.Ordinal))
                {
                    LowerInsertMember(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("store.", StringComparison.Ordinal))
                {
                    LowerStore(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("pseudo.", StringComparison.Ordinal))
                {
                    LowerPseudo(nodes, op, ctx);
                }
                else if (op.Opcode.StartsWith("yieldto:", StringComparison.Ordinal)
                         || op.Opcode == "yield")
                {
                    LowerYield(nodes, op, ctx);
                }
                else
                {
                    ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                    nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                }
                break;
        }
    }

    private static void LowerInlineAsm(List<AsmNode> nodes, LirInlineAsmInstruction inlineAsm, LoweringContext ctx)
    {
        Dictionary<string, AsmOperand> bindings = new(StringComparer.Ordinal);
        foreach (LirInlineAsmBinding binding in inlineAsm.Bindings)
            bindings[binding.Name] = LowerOperand(binding.Operand);

        IReadOnlyDictionary<string, string> localLabels = CreateInlineAsmLocalLabels(ctx, inlineAsm.ParsedLines);

        if (inlineAsm.Volatility == AsmVolatility.Volatile)
        {
            LowerRawInlineAsm(nodes, inlineAsm.Body, inlineAsm.ParsedLines, bindings, localLabels, "inline asm volatile");
            return;
        }

        if (inlineAsm.FlagOutput is not null)
        {
            LowerRawInlineAsm(nodes, inlineAsm.Body, inlineAsm.ParsedLines, bindings, localLabels, $"inline asm flag-output {inlineAsm.FlagOutput}");
            return;
        }

        if (TryLowerTypedInlineAsm(nodes, inlineAsm, bindings, localLabels))
            return;

        LowerRawInlineAsm(nodes, inlineAsm.Body, inlineAsm.ParsedLines, bindings, localLabels, "inline asm raw fallback");
    }

    private static bool TryLowerTypedInlineAsm(
        List<AsmNode> nodes,
        LirInlineAsmInstruction inlineAsm,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels)
    {
        Queue<InlineAssemblyValidator.AsmLine> parsedLines = new(inlineAsm.ParsedLines);
        List<AsmNode> lowered = [];
        foreach (InlineAsmSourceLine sourceLine in ParseInlineAsmSourceLines(inlineAsm.Body))
        {
            if (sourceLine.IsBlank)
                continue;

            if (sourceLine.CommentText is not null && sourceLine.InstructionText is null)
            {
                lowered.Add(new AsmCommentNode(sourceLine.CommentText));
                continue;
            }

            if (sourceLine.InstructionText is null)
                continue;

            if (!parsedLines.TryDequeue(out InlineAssemblyValidator.AsmLine? line))
                return false;

            if (line.IsLabel)
            {
                if (string.IsNullOrWhiteSpace(line.LabelName))
                    return false;

                lowered.Add(new AsmLabelNode(RewriteInlineAsmLocalLabel(line.LabelName, localLabels)));
                if (sourceLine.CommentText is not null)
                    lowered.Add(new AsmCommentNode(sourceLine.CommentText));
                continue;
            }

            if (!TryLowerParsedInlineAsmLine(line, bindings, localLabels, out AsmInstructionNode? instruction))
                return false;

            lowered.Add(instruction!);
            if (sourceLine.CommentText is not null)
                lowered.Add(new AsmCommentNode(sourceLine.CommentText));
        }

        if (parsedLines.Count != 0)
            return false;

        nodes.Add(new AsmCommentNode("inline asm typed begin"));
        nodes.AddRange(lowered);
        nodes.Add(new AsmCommentNode("inline asm typed end"));
        return true;
    }

    private static void LowerRawInlineAsm(
        List<AsmNode> nodes,
        string body,
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels,
        string commentLabel)
    {
        nodes.Add(new AsmCommentNode($"{commentLabel} begin"));
        Queue<InlineAssemblyValidator.AsmLine> remainingLines = new(parsedLines);
        foreach (InlineAsmSourceLine sourceLine in ParseInlineAsmSourceLines(body))
        {
            if (sourceLine.IsBlank)
            {
                nodes.Add(new AsmInlineTextNode(string.Empty));
                continue;
            }

            if (sourceLine.InstructionText is null)
            {
                if (sourceLine.CommentText is not null)
                    nodes.Add(new AsmCommentNode(sourceLine.CommentText));
                continue;
            }

            if (remainingLines.TryPeek(out InlineAssemblyValidator.AsmLine? line) && line.IsLabel)
            {
                remainingLines.Dequeue();
                if (string.IsNullOrWhiteSpace(line.LabelName))
                    continue;

                nodes.Add(new AsmLabelNode(RewriteInlineAsmLocalLabel(line.LabelName, localLabels)));
                if (sourceLine.CommentText is not null)
                    nodes.Add(new AsmCommentNode(sourceLine.CommentText));
                continue;
            }

            if (remainingLines.Count > 0)
                remainingLines.Dequeue();

            nodes.Add(new AsmInlineTextNode(
                FormatRawInlineAsmText(sourceLine.InstructionText, sourceLine.CommentText),
                bindings,
                localLabels));
        }

        nodes.Add(new AsmCommentNode($"{commentLabel} end"));
    }

    private static IEnumerable<string> SplitInlineAsmBody(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.Length == 0)
            yield break;

        int start = 0;
        while (start <= normalized.Length)
        {
            int newline = normalized.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return normalized[start..];
                yield break;
            }

            yield return normalized[start..newline];
            start = newline + 1;

            if (start == normalized.Length)
            {
                yield return string.Empty;
                yield break;
            }
        }
    }

    private static bool TryLowerParsedInlineAsmLine(
        InlineAssemblyValidator.AsmLine line,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels,
        out AsmInstructionNode? instruction)
    {
        instruction = null;
        if (!TryMapFlagEffect(line.FlagEffect, out AsmFlagEffect flagEffect))
            return false;

        List<AsmOperand> operands = new(line.Operands.Count);
        foreach (string operandText in line.Operands)
        {
            if (!TryParseInlineAsmOperand(operandText, bindings, localLabels, out AsmOperand? operand))
                return false;
            operands.Add(operand!);
        }

        instruction = new AsmInstructionNode(line.Mnemonic, operands, line.Condition, flagEffect);
        return true;
    }

    private static bool TryMapFlagEffect(string? flagText, out AsmFlagEffect effect)
    {
        effect = AsmFlagEffect.None;
        if (string.IsNullOrWhiteSpace(flagText))
            return true;

        return flagText.ToUpperInvariant() switch
        {
            "WC" => SetFlagEffect(AsmFlagEffect.WC, out effect),
            "WZ" => SetFlagEffect(AsmFlagEffect.WZ, out effect),
            "WCZ" => SetFlagEffect(AsmFlagEffect.WCZ, out effect),
            _ => false,
        };
    }

    private static bool SetFlagEffect(AsmFlagEffect value, out AsmFlagEffect effect)
    {
        effect = value;
        return true;
    }

    private static bool TryParseInlineAsmOperand(
        string text,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels,
        out AsmOperand? operand)
    {
        operand = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '{')
        {
            if (trimmed[^1] != '}')
                return false;

            string name = trimmed[1..^1].Trim();
            if (name.Length == 0
                || name.Contains('{', StringComparison.Ordinal)
                || name.Contains('}', StringComparison.Ordinal)
                || !bindings.TryGetValue(name, out AsmOperand? bound))
            {
                return false;
            }

            operand = bound;
            return true;
        }

        if (trimmed.Contains('{', StringComparison.Ordinal)
            || trimmed.Contains('}', StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.StartsWith('#'))
        {
            string immediateText = trimmed[1..].Trim();
            if (immediateText == "$")
            {
                operand = new AsmSymbolOperand("$");
                return true;
            }

            if (TryParseInlineAsmImmediate(immediateText, out long immediate))
            {
                operand = new AsmImmediateOperand(immediate);
                return true;
            }

            if (IsPlainInlineAsmSymbol(immediateText))
            {
                operand = new AsmSymbolOperand(RewriteInlineAsmLocalLabel(immediateText, localLabels));
                return true;
            }

            return false;
        }

        if (trimmed.EndsWith(":"[0]))
        {
            string labelReference = trimmed[..^1].Trim();
            if (!IsPlainInlineAsmSymbol(labelReference))
                return false;

            operand = new AsmSymbolOperand(RewriteInlineAsmLocalLabel(labelReference, localLabels));
            return true;
        }

        if (trimmed == "$" || IsPlainInlineAsmSymbol(trimmed))
        {
            operand = new AsmSymbolOperand(RewriteInlineAsmLocalLabel(trimmed, localLabels));
            return true;
        }

        return false;
    }

    private static bool IsPlainInlineAsmSymbol(string text)
    {
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$')
                continue;
            return false;
        }

        return text.Length > 0;
    }

    private static IEnumerable<InlineAsmSourceLine> ParseInlineAsmSourceLines(string body)
    {
        foreach (string line in SplitInlineAsmBody(body))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                yield return new InlineAsmSourceLine(null, null, true);
                continue;
            }

            int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx < 0)
            {
                yield return new InlineAsmSourceLine(trimmed, null, false);
                continue;
            }

            string instructionText = line[..commentIdx].Trim();
            string commentText = NormalizeBladeComment(line[(commentIdx + 2)..]);
            yield return new InlineAsmSourceLine(
                instructionText.Length == 0 ? null : instructionText,
                commentText,
                false);
        }
    }

    private static string NormalizeBladeComment(string commentText)
        => commentText.TrimStart();

    private static string FormatRawInlineAsmText(string instructionText, string? commentText)
    {
        if (commentText is null)
            return instructionText;

        return instructionText.Length == 0
            ? $"'{(commentText.Length == 0 ? string.Empty : $" {commentText}")}"
            : $"{instructionText} '{(commentText.Length == 0 ? string.Empty : $" {commentText}")}";
    }

    private static IReadOnlyDictionary<string, string> CreateInlineAsmLocalLabels(
        LoweringContext ctx,
        IReadOnlyList<InlineAssemblyValidator.AsmLine> parsedLines)
    {
        Dictionary<string, string> localLabels = new(StringComparer.Ordinal);
        List<string> labelNames = parsedLines
            .Where(static line => line.IsLabel && !string.IsNullOrWhiteSpace(line.LabelName))
            .Select(static line => line.LabelName!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (labelNames.Count == 0)
            return localLabels;

        int blockOrdinal = ctx.NextInlineAsmBlockOrdinal++;
        foreach (string labelName in labelNames)
            localLabels[labelName] = $"__asm_{ctx.FunctionOrdinal}_{blockOrdinal}_{EncodeInlineAsmLabelComponent(labelName)}";

        return localLabels;
    }

    private static string RewriteInlineAsmLocalLabel(
        string label,
        IReadOnlyDictionary<string, string> localLabels)
    {
        return localLabels.TryGetValue(label, out string? rewritten) ? rewritten : label;
    }

    private static string EncodeInlineAsmLabelComponent(string label)
    {
        StringBuilder builder = new(label.Length);
        foreach (char ch in label)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
                continue;
            }

            builder.Append("_x");
            builder.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            builder.Append('_');
        }

        return builder.ToString();
    }

    private readonly record struct InlineAsmSourceLine(string? InstructionText, string? CommentText, bool IsBlank);

    private static bool TryParseInlineAsmImmediate(string text, out long value)
    {
        value = 0;
        if (text.Length == 0)
            return false;

        bool negative = false;
        string remainder = text;
        if (remainder[0] is '+' or '-')
        {
            negative = remainder[0] == '-';
            remainder = remainder[1..];
        }

        remainder = remainder.Replace("_", "", StringComparison.Ordinal);
        if (remainder.Length == 0)
            return false;

        try
        {
            if (remainder.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(remainder[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                    return false;
                value = negative ? -hex : hex;
                return true;
            }

            if (remainder.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                long binary = Convert.ToInt64(remainder[2..], 2);
                value = negative ? -binary : binary;
                return true;
            }

            if (!long.TryParse(remainder, NumberStyles.None, CultureInfo.InvariantCulture, out long decimalValue))
                return false;

            value = negative ? -decimalValue : decimalValue;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void LowerConst(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        object? rawValue = ((LirImmediateOperand)op.Operands[0]).Value;
        object? normalizedValue = rawValue;
        if (op.ResultType is not null && TypeFacts.TryNormalizeValue(rawValue, op.ResultType, out object? converted))
            normalizedValue = converted;
        long value = GetImmediateValue(new LirImmediateOperand(normalizedValue, op.ResultType ?? BuiltinTypes.Unknown));

        // Bool/bit constants: use BITH (set bit 0) or BITL (clear bit 0).
        if (IsSingleBitType(op.ResultType) && (value == 0 || value == 1))
        {
            nodes.Add(Emit(value == 1 ? "BITH" : "BITL", dest, new AsmImmediateOperand(0)));
            return;
        }

        nodes.Add(Emit("MOV", dest, new AsmImmediateOperand(value)));
    }

    private static void LowerMov(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        nodes.Add(Emit("MOV", dest, src));
    }

    private static void LowerLoadSym(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        string symbol = ((LirSymbolOperand)op.Operands[0]).Symbol;
        nodes.Add(Emit("MOV", dest, new AsmSymbolOperand(symbol)));
    }

    private static void LowerLoadPlace(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);

        // For array-typed places in LUT/Hub, produce the base address (not the
        // stored value) because subsequent index operations need an address to
        // offset from.  FormatPlaceOperand already renders #label (hub) or
        // #label - $200 (LUT), so a plain MOV gives us the address immediate.
        bool isArrayBase = op.ResultType is ArrayTypeSymbol;

        switch (place.Place.StorageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit(isArrayBase ? "MOV" : "RDLUT", dest, place));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(Emit(isArrayBase ? "MOV" : SelectHubReadOpcode(op.ResultType), dest, place));
                break;
            default:
                nodes.Add(Emit("MOV", dest, place));
                break;
        }
    }

    private static VariableStorageClass ParseStorageClassSuffix(string opcode, string prefix)
    {
        string suffix = opcode[prefix.Length..];
        return suffix switch
        {
            "lut" => VariableStorageClass.Lut,
            "hub" => VariableStorageClass.Hub,
            _ => VariableStorageClass.Reg,
        };
    }

    private static void LowerLoadDeref(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand pointer = OpReg(op.Operands[0]);
        VariableStorageClass storageClass = ParseStorageClassSuffix(op.Opcode, "load.deref.");

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit("RDLUT", dest, pointer));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(Emit(SelectHubReadOpcode(op.ResultType), dest, pointer));
                break;
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                break;
        }
    }

    private static void LowerLoadMember(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (!TryParseAggregateMemberOpcode(op.Opcode, "load.member.", out string memberName, out int byteOffset))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"invalid {op.Opcode}"));
            return;
        }

        if (!TryGetAggregateValueShape(memberName, byteOffset, op.ResultType, out AggregateAccessShape shape))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand receiver = OpReg(op.Operands[0]);
        EmitAggregateExtract(nodes, dest, receiver, shape, op.ResultType);
    }

    private static void LowerLoadIndex(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmOperand baseOp = LowerOperand(op.Operands[0]);
        AsmRegisterOperand index = OpReg(op.Operands[1]);
        VariableStorageClass storageClass = ParseStorageClassSuffix(op.Opcode, "load.index.");

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit("ADD", index, baseOp));
                nodes.Add(Emit("RDLUT", dest, index));
                break;
            case VariableStorageClass.Hub:
            {
                int elemSize = GetHubElementSize(op.ResultType);
                if (elemSize > 1)
                    nodes.Add(Emit("SHL", index, new AsmImmediateOperand(ShiftForSize(elemSize))));
                nodes.Add(Emit("ADD", index, baseOp));
                nodes.Add(Emit(SelectHubReadOpcode(op.ResultType), dest, index));
                break;
            }
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                break;
        }
    }

    private static void LowerStoreDeref(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        AsmRegisterOperand pointer = OpReg(op.Operands[0]);
        AsmOperand value = LowerOperand(op.Operands[^1]);
        VariableStorageClass storageClass = ParseStorageClassSuffix(op.Opcode, "store.deref.");

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(new AsmInstructionNode("WRLUT", [value, pointer]));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(op.ResultType), [value, pointer]));
                break;
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                break;
        }
    }

    private static void LowerStoreIndex(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        AsmOperand baseOp = LowerOperand(op.Operands[0]);
        AsmRegisterOperand index = OpReg(op.Operands[1]);
        AsmOperand value = LowerOperand(op.Operands[^1]);
        VariableStorageClass storageClass = ParseStorageClassSuffix(op.Opcode, "store.index.");

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit("ADD", index, baseOp));
                nodes.Add(new AsmInstructionNode("WRLUT", [value, index]));
                break;
            case VariableStorageClass.Hub:
            {
                int elemSize = GetHubElementSize(op.ResultType);
                if (elemSize > 1)
                    nodes.Add(Emit("SHL", index, new AsmImmediateOperand(ShiftForSize(elemSize))));
                nodes.Add(Emit("ADD", index, baseOp));
                nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(op.ResultType), [value, index]));
                break;
            }
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                break;
        }
    }

    private static void LowerInsertMember(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (!TryParseAggregateMemberOpcode(op.Opcode, "insert.member.", out string memberName, out int byteOffset))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"invalid {op.Opcode}"));
            return;
        }

        if (!TryGetAggregateMemberShape(op.ResultType, memberName, byteOffset, out AggregateAccessShape shape))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand receiver = OpReg(op.Operands[0]);
        AsmRegisterOperand value = OpReg(op.Operands[1]);
        EmitAggregateInsert(nodes, dest, receiver, value, shape);
    }

    private static int GetHubElementSize(TypeSymbol? type)
    {
        if (type is not null && TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            if (width <= 8) return 1;
            if (width <= 16) return 2;
        }

        return 4;
    }

    private static int ShiftForSize(int size) => size switch
    {
        2 => 1,
        4 => 2,
        _ => 0,
    };

    private static void LowerConvert(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        nodes.Add(Emit("MOV", dest, src));

        if (op.ResultType is null || !TypeFacts.TryGetIntegerWidth(op.ResultType, out int width) || width >= 32)
            return;

        nodes.Add(TypeFacts.IsSignedInteger(op.ResultType)
            ? Emit("SIGNX", dest, new AsmImmediateOperand(width - 1))
            : Emit("ZEROX", dest, new AsmImmediateOperand(width - 1)));
    }

    private static void LowerStructLiteral(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (op.ResultType is not StructTypeSymbol structType)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
            return;
        }

        if (!TryGetSingleWordAggregateSize(structType, out _))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
            return;
        }

        string[] parts = op.Opcode.Split('.');
        if (parts.Length < 2 || parts.Length != op.Operands.Count + 1)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"invalid {op.Opcode}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        nodes.Add(Emit("MOV", dest, new AsmImmediateOperand(0)));

        for (int i = 0; i < op.Operands.Count; i++)
        {
            string memberName = parts[i + 1];
            if (!structType.Members.TryGetValue(memberName, out AggregateMemberSymbol? member)
                || !TryGetAggregateMemberShape(structType, member.Name, member.ByteOffset, out AggregateAccessShape shape))
            {
                ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
                nodes.Add(new AsmCommentNode($"unhandled: {op.Opcode}"));
                return;
            }

            AsmRegisterOperand value = OpReg(op.Operands[i]);
            EmitAggregateInsert(nodes, dest, dest, value, shape);
        }
    }

    private static void LowerBitfieldExtract(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (!TryParseBitfieldOpcode(op.Opcode, "bitfield.extract.", out int bitOffset, out int bitWidth))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"invalid {op.Opcode}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);

        if (bitWidth == 1)
        {
            nodes.Add(Emit("TESTB", src, new AsmImmediateOperand(bitOffset), flagEffect: AsmFlagEffect.WC));
            nodes.Add(Emit("WRC", dest));
            return;
        }

        if (bitWidth == 4 && bitOffset % 4 == 0)
        {
            nodes.Add(new AsmInstructionNode("GETNIB", [dest, src, new AsmImmediateOperand(bitOffset / 4)]));
            return;
        }

        if (bitWidth == 8 && bitOffset % 8 == 0)
        {
            nodes.Add(new AsmInstructionNode("GETBYTE", [dest, src, new AsmImmediateOperand(bitOffset / 8)]));
            if (op.ResultType is not null && TypeFacts.IsSignedInteger(op.ResultType))
                nodes.Add(Emit("SIGNX", dest, new AsmImmediateOperand(7)));
            return;
        }

        if (bitWidth == 16 && bitOffset % 16 == 0)
        {
            nodes.Add(new AsmInstructionNode("GETWORD", [dest, src, new AsmImmediateOperand(bitOffset / 16)]));
            if (op.ResultType is not null && TypeFacts.IsSignedInteger(op.ResultType))
                nodes.Add(Emit("SIGNX", dest, new AsmImmediateOperand(15)));
            return;
        }

        nodes.Add(Emit("MOV", dest, src));
        if (bitOffset != 0)
            nodes.Add(Emit("SHR", dest, new AsmImmediateOperand(bitOffset)));

        if (bitWidth < 32 && op.ResultType is not null)
        {
            nodes.Add(TypeFacts.IsSignedInteger(op.ResultType)
                ? Emit("SIGNX", dest, new AsmImmediateOperand(bitWidth - 1))
                : Emit("ZEROX", dest, new AsmImmediateOperand(bitWidth - 1)));
        }
    }

    private static void LowerBitfieldInsert(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (!TryParseBitfieldOpcode(op.Opcode, "bitfield.insert.", out int bitOffset, out int bitWidth))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"invalid {op.Opcode}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand source = OpReg(op.Operands[0]);
        AsmRegisterOperand value = OpReg(op.Operands[1]);

        nodes.Add(Emit("MOV", dest, source));

        if (bitWidth == 32 && bitOffset == 0)
        {
            nodes.Add(Emit("MOV", dest, value));
            return;
        }

        if (bitWidth == 1)
        {
            nodes.Add(Emit("TESTB", value, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WC));
            nodes.Add(Emit("BITC", dest, new AsmImmediateOperand(bitOffset)));
            return;
        }

        if (bitWidth == 4 && bitOffset % 4 == 0)
        {
            nodes.Add(new AsmInstructionNode("SETNIB", [dest, value, new AsmImmediateOperand(bitOffset / 4)]));
            return;
        }

        if (bitWidth == 8 && bitOffset % 8 == 0)
        {
            nodes.Add(new AsmInstructionNode("SETBYTE", [dest, value, new AsmImmediateOperand(bitOffset / 8)]));
            return;
        }

        if (bitWidth == 16 && bitOffset % 16 == 0)
        {
            nodes.Add(new AsmInstructionNode("SETWORD", [dest, value, new AsmImmediateOperand(bitOffset / 16)]));
            return;
        }

        ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
        nodes.Add(new AsmCommentNode($"unhandled aligned fallback for {op.Opcode}"));
    }

    private static bool TryParseAggregateMemberOpcode(string opcode, string prefix, out string memberName, out int byteOffset)
    {
        memberName = string.Empty;
        byteOffset = 0;

        Debug.Assert(opcode.StartsWith(prefix, StringComparison.Ordinal), $"Opcode '{opcode}' must start with '{prefix}'.");

        string remainder = opcode[prefix.Length..];
        int separator = remainder.LastIndexOf('.');
        if (separator <= 0 || separator >= remainder.Length - 1)
            return false;

        memberName = remainder[..separator];
        return int.TryParse(remainder[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out byteOffset);
    }

    private static bool TryGetAggregateValueShape(
        string memberName,
        int byteOffset,
        TypeSymbol? memberType,
        out AggregateAccessShape shape)
    {
        shape = default;

        if (memberType is null
            || !TypeFacts.TryGetSizeBytes(memberType, out int sizeBytes)
            || sizeBytes <= 0
            || byteOffset < 0
            || byteOffset + sizeBytes > 4)
        {
            return false;
        }

        AggregateAccessKind kind = sizeBytes switch
        {
            1 => AggregateAccessKind.Byte,
            2 when byteOffset % 2 == 0 => AggregateAccessKind.Word,
            4 when byteOffset == 0 => AggregateAccessKind.Long,
            _ => AggregateAccessKind.Invalid,
        };

        if (kind == AggregateAccessKind.Invalid)
            return false;

        shape = new AggregateAccessShape(kind, byteOffset);
        return true;
    }

    private static bool TryGetAggregateMemberShape(
        TypeSymbol? aggregateType,
        string memberName,
        int byteOffset,
        out AggregateAccessShape shape)
    {
        shape = default;

        if (aggregateType is not StructTypeSymbol structType
            || !TryGetSingleWordAggregateSize(structType, out _)
            || !structType.Members.TryGetValue(memberName, out AggregateMemberSymbol? member)
            || member.ByteOffset != byteOffset)
        {
            return false;
        }

        return TryGetAggregateValueShape(member.Name, member.ByteOffset, member.Type, out shape);
    }

    private static void EmitAggregateExtract(
        List<AsmNode> nodes,
        AsmRegisterOperand dest,
        AsmRegisterOperand receiver,
        AggregateAccessShape shape,
        TypeSymbol? resultType)
    {
        if (shape.Kind == AggregateAccessKind.Long)
        {
            nodes.Add(Emit("MOV", dest, receiver));
            return;
        }

        if (shape.Kind == AggregateAccessKind.Byte)
            nodes.Add(new AsmInstructionNode("GETBYTE", [dest, receiver, new AsmImmediateOperand(shape.ByteOffset)]));
        else
        {
            Debug.Assert(shape.Kind == AggregateAccessKind.Word, $"Unexpected aggregate access kind '{shape.Kind}'.");
            nodes.Add(new AsmInstructionNode("GETWORD", [dest, receiver, new AsmImmediateOperand(shape.ByteOffset / 2)]));
        }

        if (resultType is not null && TypeFacts.TryGetIntegerWidth(resultType, out int width) && width < 32)
        {
            nodes.Add(TypeFacts.IsSignedInteger(resultType)
                ? Emit("SIGNX", dest, new AsmImmediateOperand(width - 1))
                : Emit("ZEROX", dest, new AsmImmediateOperand(width - 1)));
        }
    }

    private static void EmitAggregateInsert(
        List<AsmNode> nodes,
        AsmRegisterOperand dest,
        AsmRegisterOperand receiver,
        AsmRegisterOperand value,
        AggregateAccessShape shape)
    {
        nodes.Add(Emit("MOV", dest, receiver));

        if (shape.Kind == AggregateAccessKind.Long)
        {
            nodes.Add(Emit("MOV", dest, value));
            return;
        }

        if (shape.Kind == AggregateAccessKind.Byte)
            nodes.Add(new AsmInstructionNode("SETBYTE", [dest, value, new AsmImmediateOperand(shape.ByteOffset)]));
        else
        {
            Debug.Assert(shape.Kind == AggregateAccessKind.Word, $"Unexpected aggregate access kind '{shape.Kind}'.");
            nodes.Add(new AsmInstructionNode("SETWORD", [dest, value, new AsmImmediateOperand(shape.ByteOffset / 2)]));
        }
    }

    private static bool TryGetSingleWordAggregateSize(TypeSymbol type, out int sizeBytes)
    {
        Debug.Assert(TypeFacts.TryGetSizeBytes(type, out sizeBytes), $"Type '{type.Name}' must have a known size.");
        return sizeBytes > 0 && sizeBytes <= 4;
    }

    private enum AggregateAccessKind
    {
        Invalid,
        Byte,
        Word,
        Long,
    }

    private readonly record struct AggregateAccessShape(AggregateAccessKind Kind, int ByteOffset);

    private static void LowerBinary(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string operatorName = op.Opcode["binary.".Length..];
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand left = OpReg(op.Operands[0]);
        AsmRegisterOperand right = OpReg(op.Operands[1]);
        bool isFlagOnly = op.Destination is { } d && ctx.FlagOnlyRegisters.Contains(d.Id);

        if (!Enum.TryParse<BoundBinaryOperatorKind>(operatorName, out BoundBinaryOperatorKind kind))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unknown binary op: {operatorName}"));
            return;
        }

        switch (kind)
        {
            case BoundBinaryOperatorKind.Add:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("ADD", dest, right));
                break;

            case BoundBinaryOperatorKind.Subtract:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SUB", dest, right));
                break;

            case BoundBinaryOperatorKind.Multiply:
                nodes.Add(Emit("QMUL", left, right));
                nodes.Add(Emit("GETQX", dest));
                break;

            case BoundBinaryOperatorKind.Divide:
                nodes.Add(Emit("QDIV", left, right));
                nodes.Add(Emit("GETQX", dest));
                break;

            case BoundBinaryOperatorKind.Modulo:
                nodes.Add(Emit("QDIV", left, right));
                nodes.Add(Emit("GETQY", dest));
                break;

            case BoundBinaryOperatorKind.BitwiseAnd:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("AND", dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseOr:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("OR", dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseXor:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("XOR", dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftLeft:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHL", dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftRight:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHR", dest, right));
                break;

            case BoundBinaryOperatorKind.ArithmeticShiftLeft:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SHL", dest, right));
                break;

            case BoundBinaryOperatorKind.ArithmeticShiftRight:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("SAR", dest, right));
                break;

            case BoundBinaryOperatorKind.RotateLeft:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("ROL", dest, right));
                break;

            case BoundBinaryOperatorKind.RotateRight:
                nodes.Add(Emit("MOV", dest, left));
                nodes.Add(Emit("ROR", dest, right));
                break;

            case BoundBinaryOperatorKind.Equals:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITZ", dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.NotEquals:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WZ));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITNZ", dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.Less:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITC", dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.LessOrEqual:
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITNC", dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.Greater:
                nodes.Add(Emit("CMP", right, left, flagEffect: AsmFlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITC", dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.GreaterOrEqual:
                nodes.Add(Emit("CMP", left, right, flagEffect: AsmFlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit("BITNC", dest, new AsmImmediateOperand(0)));
                break;
        }
    }

    private static void LowerUnary(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string operatorName = op.Opcode["unary.".Length..];
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);

        if (!Enum.TryParse<BoundUnaryOperatorKind>(operatorName, out BoundUnaryOperatorKind kind))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            nodes.Add(new AsmCommentNode($"unknown unary op: {operatorName}"));
            return;
        }

        switch (kind)
        {
            case BoundUnaryOperatorKind.Negation:
                nodes.Add(Emit("NEG", dest, src));
                break;

            case BoundUnaryOperatorKind.LogicalNot:
                if (IsSingleBitType(op.ResultType))
                {
                    nodes.Add(Emit("MOV", dest, src));
                    nodes.Add(Emit("BITNOT", dest, new AsmImmediateOperand(0)));
                }
                else
                {
                    nodes.Add(Emit("CMP", src, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
                    nodes.Add(Emit("WRZ", dest));
                }

                break;

            case BoundUnaryOperatorKind.BitwiseNot:
                nodes.Add(Emit("MOV", dest, src));
                nodes.Add(Emit("NOT", dest));
                break;

            case BoundUnaryOperatorKind.UnaryPlus:
                nodes.Add(Emit("MOV", dest, src));
                break;

            case BoundUnaryOperatorKind.PostIncrement:
                nodes.Add(Emit("MOV", dest, src));
                nodes.Add(Emit("ADD", src, new AsmImmediateOperand(1)));
                break;

            case BoundUnaryOperatorKind.PostDecrement:
                nodes.Add(Emit("MOV", dest, src));
                nodes.Add(Emit("SUB", src, new AsmImmediateOperand(1)));
                break;
        }
    }

    private static void LowerSelect(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand cond = OpReg(op.Operands[0]);
        AsmRegisterOperand whenTrue = OpReg(op.Operands[1]);
        AsmRegisterOperand whenFalse = OpReg(op.Operands[2]);

        nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));
        nodes.Add(Emit("MOV", dest, whenFalse));
        nodes.Add(Emit("MOV", dest, whenTrue, predicate: "IF_NZ"));
    }

    private static void LowerCall(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string target = ((LirSymbolOperand)op.Operands[0]).Symbol;
        CallingConventionTier calleeTier = ctx.CalleeTiers.GetValueOrDefault(target, CallingConventionTier.General);

        // Collect argument registers
        List<AsmRegisterOperand> args = [];
        for (int i = 1; i < op.Operands.Count; i++)
            args.Add(OpReg(op.Operands[i]));

        AsmRegisterOperand? destReg = op.Destination is { } dest ? new AsmRegisterOperand(dest.Id) : null;
        AsmSymbolOperand targetOp = new(target);

        switch (calleeTier)
        {
            case CallingConventionTier.Leaf:
                // CALLPA: param in PA, result in PA
                if (args.Count > 0)
                    nodes.Add(Emit("MOV", new AsmSymbolOperand("PA"), args[0]));
                nodes.Add(Emit("CALLPA", new AsmSymbolOperand("PA"), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit("MOV", destReg, new AsmSymbolOperand("PA")));
                break;

            case CallingConventionTier.SecondOrder:
                // CALLPB: param in PB, result in PB
                if (args.Count > 0)
                    nodes.Add(Emit("MOV", new AsmSymbolOperand("PB"), args[0]));
                nodes.Add(Emit("CALLPB", new AsmSymbolOperand("PB"), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit("MOV", destReg, new AsmSymbolOperand("PB")));
                break;

            case CallingConventionTier.General:
            case CallingConventionTier.EntryPoint:
                // CALL: params in global registers, result in assigned register
                for (int i = 0; i < args.Count; i++)
                    nodes.Add(new AsmCommentNode($"arg{i} = {args[i].Format()}"));
                nodes.Add(Emit("CALL", targetOp));
                if (destReg is not null)
                    nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
                break;

            case CallingConventionTier.Recursive:
                // CALLB: push locals to hub stack via PTRB, then CALLB
                for (int i = 0; i < args.Count; i++)
                    nodes.Add(new AsmCommentNode($"arg{i} = {args[i].Format()}"));
                nodes.Add(Emit("CALL", targetOp));
                if (destReg is not null)
                    nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
                break;

            default:
                Debug.Fail($"Unexpected callee tier: {calleeTier}");
                break;
        }
    }

    private static void LowerCallExtractFlag(List<AsmNode> nodes, LirOpInstruction op, bool isC)
    {
        Debug.Assert(op.Destination is not null, "call.extract pseudo-op must have a destination register");
        LirVirtualRegister dest = op.Destination.Value;

        AsmRegisterOperand destReg = new(dest.Id);
        // Materialize the C or Z flag into a register: set dest to 0, then conditionally set bit 0
        nodes.Add(Emit("BITL", destReg, new AsmImmediateOperand(0)));
        if (isC)
        {
            nodes.Add(Emit("BITC", destReg, new AsmImmediateOperand(0)));
        }
        else
        {
            nodes.Add(Emit("BITZ", destReg, new AsmImmediateOperand(0)));
        }
    }

    private static void LowerIntrinsic(List<AsmNode> nodes, LirOpInstruction op)
    {
        string name = ((LirSymbolOperand)op.Operands[0]).Symbol;
        if (name.StartsWith('@'))
            name = name[1..];

        string opcode = name.ToUpperInvariant();
        List<AsmOperand> operands = [];
        for (int i = 1; i < op.Operands.Count; i++)
            operands.Add(LowerOperand(op.Operands[i]));

        if (op.Destination is { } dest
            && ShouldEmitIntrinsicDestination(opcode, operands.Count))
        {
            operands.Insert(0, new AsmRegisterOperand(dest.Id));
        }

        nodes.Add(new AsmInstructionNode(opcode, operands));
    }

    private static bool ShouldEmitIntrinsicDestination(string opcode, int explicitOperandCount)
    {
        if (P2InstructionMetadata.TryGetInstructionForm(opcode, explicitOperandCount + 1, out P2InstructionFormInfo formWithDestination))
            return FormWritesOperand(formWithDestination);

        return !P2InstructionMetadata.TryGetInstructionForm(opcode, explicitOperandCount, out _);
    }

    private static bool FormWritesOperand(P2InstructionFormInfo form)
    {
        return OperandWrites(form.Operand0)
            || OperandWrites(form.Operand1)
            || OperandWrites(form.Operand2);
    }

    private static bool OperandWrites(P2InstructionOperandInfo operand)
    {
        return operand.Access is P2OperandAccess.Write or P2OperandAccess.ReadWrite;
    }

    private static void LowerStore(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string target = op.Opcode["store.".Length..];

        if (op.Operands.Count == 1)
        {
            AsmOperand valueOp = LowerOperand(op.Operands[0]);
            nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand(target), valueOp]));
        }
        else if (op.Operands.Count >= 2)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
            AsmOperand targetOp = LowerOperand(op.Operands[0]);
            AsmOperand valueOp = LowerOperand(op.Operands[^1]);
            nodes.Add(new AsmCommentNode($"store.{target}"));
            nodes.Add(new AsmInstructionNode("MOV", [targetOp, valueOp]));
        }
    }

    private static void LowerStorePlace(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);
        AsmOperand valueOp = LowerOperand(op.Operands[1]);

        TypeSymbol? placeType = (place.Place.Symbol as VariableSymbol)?.Type;

        switch (place.Place.StorageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(new AsmInstructionNode("WRLUT", [valueOp, place]));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(placeType), [valueOp, place]));
                break;
            default:
                nodes.Add(new AsmInstructionNode("MOV", [place, valueOp]));
                break;
        }
    }

    private static void LowerUpdatePlace(List<AsmNode> nodes, LirOpInstruction op)
    {
        string operatorName = op.Opcode["update.place.".Length..];
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);
        AsmOperand value = LowerOperand(op.Operands[1]);

        string opcode = Enum.TryParse<BoundBinaryOperatorKind>(operatorName, out BoundBinaryOperatorKind kind)
            ? kind switch
            {
                BoundBinaryOperatorKind.Add => "ADD",
                BoundBinaryOperatorKind.Subtract => "SUB",
                BoundBinaryOperatorKind.BitwiseAnd => "AND",
                BoundBinaryOperatorKind.BitwiseOr => "OR",
                BoundBinaryOperatorKind.BitwiseXor => "XOR",
                BoundBinaryOperatorKind.ShiftLeft => "SHL",
                BoundBinaryOperatorKind.ShiftRight => "SHR",
                BoundBinaryOperatorKind.ArithmeticShiftLeft => "SHL",
                BoundBinaryOperatorKind.ArithmeticShiftRight => "SAR",
                _ => string.Empty,
            }
            : string.Empty;

        if (string.IsNullOrEmpty(opcode))
        {
            if (Enum.TryParse<BoundBinaryOperatorKind>(operatorName, out kind)
                && kind == BoundBinaryOperatorKind.Modulo)
            {
                nodes.Add(new AsmInstructionNode("QDIV", [place, value]));
                nodes.Add(new AsmInstructionNode("GETQY", [place]));
                return;
            }

            nodes.Add(new AsmCommentNode($"unhandled update place: {operatorName}"));
            nodes.Add(new AsmInstructionNode("MOV", [place, value]));
            return;
        }

        nodes.Add(new AsmInstructionNode(opcode, [place, value]));
    }

    private static string SelectHubReadOpcode(TypeSymbol? type)
    {
        if (type?.IsBool == true)
            return "RDBYTE";

        if (type is not null && TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            if (width <= 8)
                return "RDBYTE";
            if (width <= 16)
                return "RDWORD";
        }

        return "RDLONG";
    }

    private static string SelectHubWriteOpcode(TypeSymbol? type)
    {
        if (type?.IsBool == true)
            return "WRBYTE";

        if (type is not null && TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            if (width <= 8)
                return "WRBYTE";
            if (width <= 16)
                return "WRWORD";
        }

        return "WRLONG";
    }

    private static void LowerPseudo(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        string pseudoOp = op.Opcode["pseudo.".Length..];
        ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);

        switch (pseudoOp)
        {
            case "rep.setup":
                if (op.Operands.Count >= 1)
                {
                    AsmOperand iters = LowerOperand(op.Operands[0]);
                    nodes.Add(new AsmCommentNode("REP setup: body length TBD"));
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), iters]));
                }
                break;

            case "rep.iter":
                break;

            case "repfor.setup":
                if (op.Operands.Count >= 2)
                {
                    AsmOperand end = LowerOperand(op.Operands[1]);
                    nodes.Add(new AsmCommentNode("REP-FOR setup: body length TBD"));
                    nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), end]));
                }
                break;

            case "repfor.iter":
                break;

            case "noirq.begin":
                nodes.Add(new AsmCommentNode("noirq: body length TBD"));
                nodes.Add(new AsmInstructionNode("REP", [new AsmImmediateOperand(0), new AsmImmediateOperand(1)]));
                break;

            case "noirq.end":
                break;

            default:
                nodes.Add(new AsmCommentNode($"pseudo.{pseudoOp}"));
                break;
        }
    }

    private static void LowerYield(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        ReportUnsupportedOpcode(ctx, op.Span, op.Opcode);
        if (op.Opcode.StartsWith("yieldto:", StringComparison.Ordinal))
        {
            string target = op.Opcode["yieldto:".Length..];
            nodes.Add(new AsmCommentNode($"TODO: CALLD (yieldto {target})"));
        }
        else
        {
            nodes.Add(new AsmCommentNode("TODO: CALLD (yield)"));
        }
    }

    private static void LowerTerminator(List<AsmNode> nodes, LoweringContext ctx, LirTerminator terminator)
    {
        string functionName = ctx.Function.Name;

        switch (terminator)
        {
            case LirGotoTerminator goto_:
                EmitPhiMoves(nodes, goto_.Arguments, ctx, goto_.TargetLabel);
                nodes.Add(Emit("JMP", new AsmSymbolOperand($"{functionName}_{goto_.TargetLabel}")));
                break;

            case LirBranchTerminator branch:
                LowerBranch(nodes, ctx, branch);
                break;

            case LirReturnTerminator ret:
                LowerReturn(nodes, ctx, ret);
                break;

            case LirUnreachableTerminator:
                nodes.Add(new AsmCommentNode("unreachable"));
                EmitHaltLoop(nodes);
                break;
        }
    }

    private static void LowerReturn(List<AsmNode> nodes, LoweringContext ctx, LirReturnTerminator ret)
    {
        // Move first return value (register-placed) to appropriate location based on CC tier
        if (ret.Values.Count > 0)
        {
            AsmOperand resultOp = LowerOperand(ret.Values[0]);

            switch (ctx.Tier)
            {
                case CallingConventionTier.Leaf:
                    // Result in PA
                    nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand("PA"), resultOp]));
                    break;

                case CallingConventionTier.SecondOrder:
                    // Result in PB
                    nodes.Add(new AsmInstructionNode("MOV", [new AsmSymbolOperand("PB"), resultOp]));
                    break;

                default:
                    // General/Recursive: result stays in its register (caller knows which)
                    nodes.Add(new AsmImplicitUseNode([resultOp]));
                    nodes.Add(new AsmCommentNode($"return value: {resultOp.Format()}"));
                    break;
            }
        }

        // Set flags for additional return values (C and Z)
        IReadOnlyList<ReturnSlot> returnSlots = ctx.Function.ReturnSlots;
        for (int i = 1; i < ret.Values.Count && i < returnSlots.Count; i++)
        {
            AsmOperand flagValue = LowerOperand(ret.Values[i]);
            ReturnPlacement placement = returnSlots[i].Placement;
            if (placement == ReturnPlacement.FlagC)
                nodes.Add(new AsmInstructionNode("TESTB", [flagValue, new AsmImmediateOperand(0)], flagEffect: AsmFlagEffect.WC));
            else if (placement == ReturnPlacement.FlagZ)
                nodes.Add(new AsmInstructionNode("TESTB", [flagValue, new AsmImmediateOperand(0)], flagEffect: AsmFlagEffect.WZ));
        }

        switch (ctx.Tier)
        {
            case CallingConventionTier.EntryPoint:
                // Entry point "returns" by halting: endless loop with interrupts shielded
                EmitHaltLoop(nodes);
                break;

            case CallingConventionTier.Recursive:
                nodes.Add(Emit("RET"));
                break;

            case CallingConventionTier.Interrupt:
                FunctionKind kind = ctx.Function.Kind;
                string retInsn = kind switch
                {
                    FunctionKind.Int1 => "RETI1",
                    FunctionKind.Int2 => "RETI2",
                    FunctionKind.Int3 => "RETI3",
                    _ => "RET",
                };
                nodes.Add(Emit(retInsn));
                break;

            default:
                nodes.Add(Emit("RET"));
                break;
        }
    }

    /// <summary>
    /// Emit an endless halt loop: REP #1, #0 followed by NOP.
    /// REP #1, #0 repeats the next 1 instruction forever (count=0 means infinite).
    /// This keeps the COG alive without executing real work.
    /// </summary>
    private static void EmitHaltLoop(List<AsmNode> nodes)
    {
        nodes.Add(new AsmCommentNode("halt: endless loop"));
        nodes.Add(new AsmInstructionNode("REP",
            [new AsmImmediateOperand(1), new AsmImmediateOperand(0)]));
        nodes.Add(Emit("NOP"));
    }

    private static void LowerBranch(List<AsmNode> nodes, LoweringContext ctx, LirBranchTerminator branch)
    {
        string functionName = ctx.Function.Name;
        string trueLabel = $"{functionName}_{branch.TrueLabel}";
        string falseLabel = $"{functionName}_{branch.FalseLabel}";

        // Flag-aware branch: the condition already lives in a hardware flag (C or Z).
        // Use predicated jumps directly — no register test needed.
        if (branch.ConditionFlag is not null)
        {
            // The MirFlag encodes polarity: C/Z mean "true when flag set",
            // NC/NZ mean "true when flag clear".
            string truePredicate = branch.ConditionFlag.Value switch
            {
                MirFlag.C => "IF_C",
                MirFlag.NC => "IF_NC",
                MirFlag.Z => "IF_Z",
                MirFlag.NZ => "IF_NZ",
                _ => throw new UnreachableException(),
            };
            string falsePredicate = branch.ConditionFlag.Value switch
            {
                MirFlag.C => "IF_NC",
                MirFlag.NC => "IF_C",
                MirFlag.Z => "IF_NZ",
                MirFlag.NZ => "IF_Z",
                _ => throw new UnreachableException(),
            };

            if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
            {
                nodes.Add(Emit("JMP", new AsmSymbolOperand(falseLabel), predicate: falsePredicate));
                nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
            }
            else
            {
                EmitPhiMovesConditioned(nodes, branch.FalseArguments, ctx, branch.FalseLabel, falsePredicate);
                nodes.Add(Emit("JMP", new AsmSymbolOperand(falseLabel), predicate: falsePredicate));
                EmitPhiMoves(nodes, branch.TrueArguments, ctx, branch.TrueLabel);
                nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
            }

            return;
        }

        // Register-based branch: test the condition register.
        AsmRegisterOperand cond = OpReg(branch.Condition);

        if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
        {
            nodes.Add(Emit("TJZ", cond, new AsmSymbolOperand(falseLabel)));
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
        else
        {
            nodes.Add(Emit("CMP", cond, new AsmImmediateOperand(0), flagEffect: AsmFlagEffect.WZ));

            // False path (Z=1, condition was zero)
            EmitPhiMovesConditioned(nodes, branch.FalseArguments, ctx, branch.FalseLabel, "IF_Z");
            nodes.Add(Emit("JMP", new AsmSymbolOperand(falseLabel), predicate: "IF_Z"));

            // True path (fall-through when NZ)
            EmitPhiMoves(nodes, branch.TrueArguments, ctx, branch.TrueLabel);
            nodes.Add(Emit("JMP", new AsmSymbolOperand(trueLabel)));
        }
    }

    /// <summary>
    /// Emit MOV instructions for SSA φ-arguments (block parameter passing).
    /// Maps arguments to the target block's parameter registers.
    /// </summary>
    private static void EmitPhiMoves(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        LoweringContext ctx,
        string targetLabel,
        string? predicate = null)
    {
        if (arguments.Count == 0)
            return;

        IReadOnlyList<LirBlockParameter>? targetParams = null;
        ctx.BlockParams.TryGetValue(targetLabel, out targetParams);

        for (int i = 0; i < arguments.Count; i++)
        {
            AsmOperand src = LowerOperand(arguments[i]);

            if (targetParams is not null && i < targetParams.Count)
            {
                AsmRegisterOperand paramReg = new(targetParams[i].Register.Id);
                nodes.Add(new AsmInstructionNode("MOV", [paramReg, src], predicate));
            }
            else
            {
                // Fallback: emit as comment if we can't resolve target param
                ReportUnsupportedLowering(ctx, new TextSpan(0, 0), "phi-move");
                string prefix = predicate is not null ? $"{predicate} " : "";
                nodes.Add(new AsmCommentNode($"{prefix}phi[{i}] = {src.Format()} -> {ctx.Function.Name}_{targetLabel}"));
            }
        }
    }

    private static void EmitPhiMovesConditioned(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        LoweringContext ctx,
        string targetLabel,
        string predicate)
    {
        EmitPhiMoves(nodes, arguments, ctx, targetLabel, predicate);
    }

    private static void ReportUnsupportedOpcode(LoweringContext ctx, TextSpan span, string opcode)
    {
        ReportUnsupportedLowering(ctx, span, NormalizeUnsupportedLoweringName(opcode));
    }

    private static void ReportUnsupportedLowering(LoweringContext ctx, TextSpan span, string lowering)
    {
        if (ctx.Diagnostics is null)
            return;

        string key = $"{span.Start}:{span.Length}:{lowering}";
        if (!ctx.ReportedUnsupportedLowerings.Add(key))
            return;

        ctx.Diagnostics.ReportUnsupportedLowering(span, lowering);
    }

    private static string NormalizeUnsupportedLoweringName(string opcode)
    {
        if (opcode.StartsWith("load.member", StringComparison.Ordinal))
            return "load.member";
        if (opcode.StartsWith("store.member", StringComparison.Ordinal))
            return "store.member";
        if (opcode.StartsWith("structlit", StringComparison.Ordinal))
            return "structlit";
        if (opcode.StartsWith("yieldto:", StringComparison.Ordinal))
            return "yieldto";
        return opcode;
    }

    // --- Helpers ---

    private static AsmRegisterOperand DestReg(LirOpInstruction op)
    {
        if (op.Destination is not { } dest)
            throw new InvalidOperationException($"Instruction '{op.Opcode}' expected a destination register");
        return new AsmRegisterOperand(dest.Id);
    }

    private static AsmRegisterOperand OpReg(LirOperand operand)
    {
        if (operand is LirRegisterOperand reg)
            return new AsmRegisterOperand(reg.Register.Id);
        throw new InvalidOperationException($"Expected register operand, got {operand.GetType().Name}");
    }

    private static AsmOperand LowerOperand(LirOperand operand)
    {
        return operand switch
        {
            LirRegisterOperand reg => new AsmRegisterOperand(reg.Register.Id),
            LirImmediateOperand imm => new AsmImmediateOperand(GetImmediateValue(imm)),
            LirSymbolOperand sym => new AsmSymbolOperand(sym.Symbol),
            LirPlaceOperand place => new AsmPlaceOperand(place.Place),
            _ => throw new InvalidOperationException($"Unknown operand type: {operand.GetType().Name}"),
        };
    }

    private static bool IsSingleBitType(TypeSymbol? type)
        => type is not null && (type.IsBool || ReferenceEquals(type, BuiltinTypes.Bit));

    private static long GetImmediateValue(LirImmediateOperand imm)
    {
        return imm.Value switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            int i => i,
            uint u => u,
            long l => l,
            ulong u => (long)u,
            byte b => b,
            sbyte s => s,
            short s => s,
            ushort u => u,
            _ => Convert.ToInt64(imm.Value, CultureInfo.InvariantCulture),
        };
    }

    private static bool TryParseBitfieldOpcode(string opcode, string prefix, out int bitOffset, out int bitWidth)
    {
        bitOffset = 0;
        bitWidth = 0;
        if (!opcode.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string[] parts = opcode[prefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out bitOffset)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out bitWidth);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmOperand op1,
        AsmOperand op2,
        string? predicate = null,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1, op2], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmOperand op1,
        string? predicate = null,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        string opcode,
        AsmFlagEffect flagEffect = AsmFlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [], null, flagEffect);
    }
}
