using System;
using System.Collections.Generic;
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
        VirtualAsmRegister.ResetDebugIds();

        // Run call graph analysis to determine CC tiers and dead functions
        CallGraphResult cgResult = CallGraphAnalyzer.Analyze(module);
        (
            IReadOnlyList<StoragePlace> storagePlaces,
            Dictionary<FunctionSymbol, RecursiveCallingConventionInfo> recursiveCallingConvention,
            Dictionary<FunctionSymbol, CoroutineCallingConventionInfo> coroutineCallingConvention,
            StoragePlace? topLevelYieldStatePlace) =
            BuildCallingConventionStorage(module, cgResult);

        // Build a map of function name → block label → block parameter registers
        // so φ-moves can emit actual MOV instructions to the right target registers.
        Dictionary<FunctionSymbol, Dictionary<LirBlockRef, IReadOnlyList<LirBlockParameter>>> blockParamMap = [];
        foreach (LirFunction function in module.Functions)
        {
            Dictionary<LirBlockRef, IReadOnlyList<LirBlockParameter>> funcBlocks = [];
            foreach (LirBlock block in function.Blocks)
                funcBlocks[block.Ref] = block.Parameters;
            blockParamMap[function.Symbol] = funcBlocks;
        }

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
        {
            // Eliminate dead (unreachable) functions from codegen
            if (cgResult.DeadFunctions.Contains(function.Symbol))
                continue;

            CallingConventionTier tier = cgResult.Tiers.GetValueOrDefault(function.Symbol, CallingConventionTier.General);
            bool containsYield = FunctionContainsYield(function);
            LoweringContext ctx = new(
                function,
                functionOrdinal: functions.Count,
                tier,
                cgResult.Tiers,
                blockParamMap[function.Symbol],
                recursiveCallingConvention,
                coroutineCallingConvention,
                topLevelYieldStatePlace,
                containsYield,
                diagnostics);
            functions.Add(LowerFunction(ctx));
        }

        return new AsmModule(storagePlaces, functions);
    }

    private sealed class LoweringContext
    {
        public LirFunction Function { get; }
        public int FunctionOrdinal { get; }
        public CallingConventionTier Tier { get; }
        public Dictionary<FunctionSymbol, CallingConventionTier> CalleeTiers { get; }
        public Dictionary<LirBlockRef, IReadOnlyList<LirBlockParameter>> BlockParams { get; }
        public Dictionary<FunctionSymbol, RecursiveCallingConventionInfo> RecursiveCallingConvention { get; }
        public Dictionary<FunctionSymbol, CoroutineCallingConventionInfo> CoroutineCallingConvention { get; }
        public StoragePlace? TopLevelYieldStatePlace { get; }
        public bool ContainsYield { get; }
        public DiagnosticBag? Diagnostics { get; }
        public HashSet<string> ReportedUnsupportedLowerings { get; } = new(StringComparer.Ordinal);
        public Dictionary<LirBlockRef, ControlFlowLabelSymbol> BlockLabels { get; } = [];
        public int NextInlineAsmBlockOrdinal { get; set; }
        public int NextRepLabelOrdinal { get; set; }

        /// <summary>
        /// Stack of REP end-label names for correlating setup/begin pseudo-ops with
        /// their corresponding iter/end pseudo-ops. Supports nesting.
        /// </summary>
        public Stack<ControlFlowLabelSymbol> RepEndLabelStack { get; } = new();

        /// <summary>
        /// Registers whose values are only consumed as hardware flags (by a flag-aware branch),
        /// not as register values. Comparisons writing to these can skip materialization (WRZ/WRC).
        /// Recomputed per block.
        /// </summary>
        public HashSet<LirVirtualRegister> FlagOnlyRegisters { get; } = [];

        public LoweringContext(
            LirFunction function,
            int functionOrdinal,
            CallingConventionTier tier,
            Dictionary<FunctionSymbol, CallingConventionTier> calleeTiers,
            Dictionary<LirBlockRef, IReadOnlyList<LirBlockParameter>> blockParams,
            Dictionary<FunctionSymbol, RecursiveCallingConventionInfo> recursiveCallingConvention,
            Dictionary<FunctionSymbol, CoroutineCallingConventionInfo> coroutineCallingConvention,
            StoragePlace? topLevelYieldStatePlace,
            bool containsYield,
            DiagnosticBag? diagnostics)
        {
            Function = function;
            FunctionOrdinal = functionOrdinal;
            Tier = tier;
            CalleeTiers = calleeTiers;
            BlockParams = blockParams;
            RecursiveCallingConvention = recursiveCallingConvention;
            CoroutineCallingConvention = coroutineCallingConvention;
            TopLevelYieldStatePlace = topLevelYieldStatePlace;
            ContainsYield = containsYield;
            Diagnostics = diagnostics;
        }

        public ControlFlowLabelSymbol GetBlockLabel(LirBlockRef blockRef)
        {
            if (BlockLabels.TryGetValue(blockRef, out ControlFlowLabelSymbol? label))
                return label;

            label = new ControlFlowLabelSymbol($"{Function.Name}_{blockRef}");
            BlockLabels.Add(blockRef, label);
            return label;
        }

        public VirtualAsmRegister GetRegister(LirVirtualRegister register)
        {
            return VirtualAsmRegister.FromLir(register);
        }
    }

    private sealed class RecursiveCallingConventionInfo
    {
        public RecursiveCallingConventionInfo(
            IReadOnlyList<StoragePlace> parameterPlaces,
            StoragePlace? registerReturnPlace)
        {
            ParameterPlaces = parameterPlaces;
            RegisterReturnPlace = registerReturnPlace;
        }

        public IReadOnlyList<StoragePlace> ParameterPlaces { get; }
        public StoragePlace? RegisterReturnPlace { get; }
    }

    private sealed class CoroutineCallingConventionInfo
    {
        public CoroutineCallingConventionInfo(
            StoragePlace statePlace,
            IReadOnlyList<StoragePlace> parameterPlaces)
        {
            StatePlace = statePlace;
            ParameterPlaces = parameterPlaces;
        }

        public StoragePlace StatePlace { get; }
        public IReadOnlyList<StoragePlace> ParameterPlaces { get; }
    }

    private static AsmFunction LowerFunction(LoweringContext ctx)
    {
        List<AsmNode> nodes = [];
        nodes.Add(new AsmDirectiveNode($"function {ctx.Function.Name}"));

        foreach (LirBlock block in ctx.Function.Blocks)
        {
            nodes.Add(new AsmLabelNode(ctx.GetBlockLabel(block.Ref)));

            if (ctx.Tier == CallingConventionTier.Recursive
                && ReferenceEquals(block, ctx.Function.Blocks[0]))
            {
                EmitRecursiveFunctionEntryLoads(nodes, block, ctx);
            }
            else if (ctx.Tier == CallingConventionTier.Coroutine
                     && ReferenceEquals(block, ctx.Function.Blocks[0]))
            {
                EmitCoroutineFunctionEntryLoads(nodes, block, ctx);
            }

            ComputeFlagOnlyRegisters(ctx, block);

            foreach (LirInstruction instruction in block.Instructions)
                LowerInstruction(nodes, instruction, ctx);

            LowerTerminator(nodes, ctx, block.Terminator);
        }

        return new AsmFunction(ctx.Function, ctx.Tier, nodes);
    }

    private static (
        IReadOnlyList<StoragePlace> StoragePlaces,
        Dictionary<FunctionSymbol, RecursiveCallingConventionInfo> RecursiveCallingConvention,
        Dictionary<FunctionSymbol, CoroutineCallingConventionInfo> CoroutineCallingConvention,
        StoragePlace? TopLevelYieldStatePlace)
        BuildCallingConventionStorage(LirModule module, CallGraphResult cgResult)
    {
        List<StoragePlace> storagePlaces = new(module.StoragePlaces.Count);
        storagePlaces.AddRange(module.StoragePlaces);

        Dictionary<FunctionSymbol, RecursiveCallingConventionInfo> recursiveCallingConvention = [];
        Dictionary<FunctionSymbol, CoroutineCallingConventionInfo> coroutineCallingConvention = [];
        foreach (LirFunction function in module.Functions)
        {
            if (cgResult.DeadFunctions.Contains(function.Symbol))
                continue;

            CallingConventionTier functionTier = cgResult.Tiers.GetValueOrDefault(function.Symbol, CallingConventionTier.General);
            if (functionTier == CallingConventionTier.Recursive)
            {
                Assert.Invariant(function.Blocks.Count > 0, "Recursive functions must have an entry block.");
                IReadOnlyList<LirBlockParameter> entryParameters = function.Blocks[0].Parameters;
                List<StoragePlace> parameterPlaces = new(entryParameters.Count);
                for (int i = 0; i < entryParameters.Count; i++)
                {
                    VariableSymbol parameterSymbol = new(
                        $"rec_{function.Name}_arg{i}",
                        entryParameters[i].Type,
                        isConst: false,
                        VariableStorageClass.Reg,
                        VariableScopeKind.GlobalStorage,
                        isExtern: false,
                        fixedAddress: null,
                        alignment: null);
                    StoragePlace parameterPlace = new(
                        parameterSymbol,
                        StoragePlaceKind.AllocatableGlobalRegister,
                        fixedAddress: null,
                        staticInitializer: null);
                    parameterPlaces.Add(parameterPlace);
                    storagePlaces.Add(parameterPlace);
                }

                ReturnSlot? registerReturnSlot = function.ReturnSlots
                    .Where(static slot => slot.Placement == ReturnPlacement.Register)
                    .Cast<ReturnSlot?>()
                    .FirstOrDefault();

                StoragePlace? registerReturnPlace = null;
                if (registerReturnSlot is { } slot)
                {
                    if (parameterPlaces.Count > 0)
                    {
                        registerReturnPlace = parameterPlaces[0];
                    }
                    else
                    {
                        VariableSymbol returnSymbol = new(
                            $"rec_{function.Name}_ret0",
                            slot.Type,
                            isConst: false,
                            VariableStorageClass.Reg,
                            VariableScopeKind.GlobalStorage,
                            isExtern: false,
                            fixedAddress: null,
                            alignment: null);
                        registerReturnPlace = new StoragePlace(
                            returnSymbol,
                            StoragePlaceKind.AllocatableGlobalRegister,
                            fixedAddress: null,
                            staticInitializer: null);
                        storagePlaces.Add(registerReturnPlace);
                    }
                }

                recursiveCallingConvention[function.Symbol] = new RecursiveCallingConventionInfo(parameterPlaces, registerReturnPlace);
            }
            else if (functionTier == CallingConventionTier.Coroutine)
            {
                Assert.Invariant(function.Blocks.Count > 0, "Coroutine functions must have an entry block.");
                IReadOnlyList<LirBlockParameter> entryParameters = function.Blocks[0].Parameters;
                List<StoragePlace> parameterPlaces = new(entryParameters.Count);
                for (int i = 0; i < entryParameters.Count; i++)
                {
                    VariableSymbol parameterSymbol = new(
                        $"coro_{function.Name}_arg{i}",
                        entryParameters[i].Type,
                        isConst: false,
                        VariableStorageClass.Reg,
                        VariableScopeKind.GlobalStorage,
                        isExtern: false,
                        fixedAddress: null,
                        alignment: null);
                    StoragePlace parameterPlace = new(
                        parameterSymbol,
                        StoragePlaceKind.AllocatableGlobalRegister,
                        fixedAddress: null,
                        staticInitializer: null);
                    parameterPlaces.Add(parameterPlace);
                    storagePlaces.Add(parameterPlace);
                }

                VariableSymbol stateSymbol = new(
                    $"coro_{function.Name}_state",
                    BuiltinTypes.U32,
                    isConst: false,
                    VariableStorageClass.Reg,
                    VariableScopeKind.GlobalStorage,
                    isExtern: false,
                    fixedAddress: null,
                    alignment: null);
                string entryLabel = $"{function.Name}_{function.Blocks[0].Label}";
                StoragePlace statePlace = new(
                    stateSymbol,
                    StoragePlaceKind.AllocatableGlobalRegister,
                    fixedAddress: null,
                    staticInitializer: entryLabel);
                storagePlaces.Add(statePlace);

                coroutineCallingConvention[function.Symbol] = new CoroutineCallingConventionInfo(statePlace, parameterPlaces);
            }
        }

        StoragePlace? topLevelYieldStatePlace = null;
        if (HasTopLevelYieldto(module))
        {
            VariableSymbol topYieldStateSymbol = new(
                "top_yield_state",
                BuiltinTypes.U32,
                isConst: false,
                VariableStorageClass.Reg,
                VariableScopeKind.GlobalStorage,
                isExtern: false,
                fixedAddress: null,
                alignment: null);
            topLevelYieldStatePlace = new StoragePlace(
                topYieldStateSymbol,
                StoragePlaceKind.AllocatableGlobalRegister,
                fixedAddress: null,
                staticInitializer: null);
            storagePlaces.Add(topLevelYieldStatePlace);
        }

        return (storagePlaces, recursiveCallingConvention, coroutineCallingConvention, topLevelYieldStatePlace);
    }

    private static bool FunctionContainsYield(LirFunction function)
    {
        foreach (LirBlock block in function.Blocks)
        {
            foreach (LirInstruction instruction in block.Instructions)
            {
                if (instruction is LirOpInstruction { Operation: LirYieldOperation })
                    return true;
            }
        }

        return false;
    }

    private static bool HasTopLevelYieldto(LirModule module)
    {
        foreach (LirFunction function in module.Functions)
        {
            if (!function.IsEntryPoint)
                continue;

            foreach (LirBlock block in function.Blocks)
            {
                foreach (LirInstruction instruction in block.Instructions)
                {
                    if (instruction is LirOpInstruction { Operation: LirYieldToOperation })
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void EmitRecursiveFunctionEntryLoads(List<AsmNode> nodes, LirBlock block, LoweringContext ctx)
    {
        var ok = ctx.RecursiveCallingConvention.TryGetValue(ctx.Function.Symbol, out RecursiveCallingConventionInfo? recursiveInfo);
        Assert.Invariant(ok, "Recursive functions must have calling-convention metadata.");
        Assert.Invariant(recursiveInfo != null);
        Assert.Invariant(recursiveInfo.ParameterPlaces.Count == block.Parameters.Count, "Recursive entry ABI must match the function parameter list.");

        for (int i = 0; i < block.Parameters.Count; i++)
        {
            nodes.Add(Emit(
                P2Mnemonic.MOV,
                new AsmRegisterOperand(ctx.GetRegister(block.Parameters[i].Register)),
                new AsmPlaceOperand(recursiveInfo.ParameterPlaces[i])));
        }
    }

    private static void EmitCoroutineFunctionEntryLoads(List<AsmNode> nodes, LirBlock block, LoweringContext ctx)
    {
        var ok = ctx.CoroutineCallingConvention.TryGetValue(ctx.Function.Symbol, out CoroutineCallingConventionInfo? coroutineInfo);
        Assert.Invariant(ok, "Coroutine functions must have calling-convention metadata.");
        Assert.Invariant(coroutineInfo != null);
        Assert.Invariant(coroutineInfo.ParameterPlaces.Count == block.Parameters.Count, "Coroutine entry ABI must match the function parameter list.");

        for (int i = 0; i < block.Parameters.Count; i++)
        {
            nodes.Add(Emit(
                P2Mnemonic.MOV,
                new AsmRegisterOperand(ctx.GetRegister(block.Parameters[i].Register)),
                new AsmPlaceOperand(coroutineInfo.ParameterPlaces[i])));
        }
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

        Assert.Invariant(branch.Condition is LirRegisterOperand, "Flag-aware branch condition must be a register operand");
        if (branch.Condition is not LirRegisterOperand condReg)
            return;

        LirVirtualRegister condRegister = condReg.Register;

        // Check that no instruction in the block uses this register as an operand
        // (other than the instruction that defines it).
        foreach (LirInstruction instruction in block.Instructions)
        {
            foreach (LirOperand operand in instruction.Operands)
            {
                if (operand is LirRegisterOperand reg && ReferenceEquals(reg.Register, condRegister))
                    return; // Used as an operand somewhere — need materialization.
            }
        }

        // Also check terminator arguments (phi moves).
        foreach (LirOperand operand in branch.TrueArguments)
        {
            if (operand is LirRegisterOperand reg && ReferenceEquals(reg.Register, condRegister))
                return;
        }

        foreach (LirOperand operand in branch.FalseArguments)
        {
            if (operand is LirRegisterOperand reg && ReferenceEquals(reg.Register, condRegister))
                return;
        }

        ctx.FlagOnlyRegisters.Add(condRegister);
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
            Assert.Unreachable($"Unexpected LIR instruction type '{instruction.GetType().Name}'.");
            return;
        }

        switch (op.Operation)
        {
            case LirConstOperation:
                LowerConst(nodes, op);
                break;
            case LirMovOperation:
                LowerMov(nodes, op);
                break;
            case LirLoadAddressOperation:
                LowerLoadAddress(nodes, op);
                break;
            case LirLoadPlaceOperation:
                LowerLoadPlace(nodes, op);
                break;
            case LirCallOperation:
                LowerCall(nodes, op, ctx);
                break;
            case LirCallExtractFlagOperation extractFlag:
                LowerCallExtractFlag(nodes, op, extractFlag.Flag);
                break;
            case LirIntrinsicOperation:
                LowerIntrinsic(nodes, op);
                break;
            case LirConvertOperation:
                LowerConvert(nodes, op);
                break;
            case LirStructLiteralOperation structLiteral:
                LowerStructLiteral(nodes, op, structLiteral, ctx);
                break;
            case LirBinaryOperation binary:
                LowerBinary(nodes, op, binary, ctx);
                break;
            case LirUnaryOperation unary:
                LowerUnary(nodes, op, unary);
                break;
            case LirBitfieldExtractOperation bitfieldExtract:
                LowerBitfieldExtract(nodes, op, bitfieldExtract);
                break;
            case LirBitfieldInsertOperation bitfieldInsert:
                LowerBitfieldInsert(nodes, op, bitfieldInsert, ctx);
                break;
            case LirLoadMemberOperation loadMember:
                LowerLoadMember(nodes, op, loadMember, ctx);
                break;
            case LirLoadDerefOperation loadDeref:
                LowerLoadDeref(nodes, op, loadDeref, ctx);
                break;
            case LirLoadIndexOperation loadIndex:
                LowerLoadIndex(nodes, op, loadIndex, ctx);
                break;
            case LirStorePlaceOperation:
                LowerStorePlace(nodes, op);
                break;
            case LirUpdatePlaceOperation updatePlace:
                LowerUpdatePlace(nodes, op, updatePlace, ctx);
                break;
            case LirStoreDerefOperation storeDeref:
                LowerStoreDeref(nodes, op, storeDeref, ctx);
                break;
            case LirStoreIndexOperation storeIndex:
                LowerStoreIndex(nodes, op, storeIndex, ctx);
                break;
            case LirInsertMemberOperation insertMember:
                LowerInsertMember(nodes, op, insertMember, ctx);
                break;
            case LirRepSetupOperation:
                LowerRepSetup(nodes, op, ctx);
                break;
            case LirRepIterOperation:
                LowerRepIter(nodes, ctx);
                break;
            case LirRepForSetupOperation:
                LowerRepForSetup(nodes, op, ctx);
                break;
            case LirRepForIterOperation:
                LowerRepForIter(nodes, ctx);
                break;
            case LirNoIrqBeginOperation:
                LowerNoIrqBegin(nodes, ctx);
                break;
            case LirNoIrqEndOperation:
                LowerNoIrqEnd(nodes, ctx);
                break;
            case LirYieldOperation:
                LowerYield(nodes, op, ctx);
                break;
            case LirYieldToOperation yieldTo:
                LowerYieldTo(nodes, op, yieldTo, ctx);
                break;
            case LirRangeOperation:
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                break;
        }
    }

    private static void LowerInlineAsm(List<AsmNode> nodes, LirInlineAsmInstruction inlineAsm, LoweringContext ctx)
    {
        Dictionary<string, AsmOperand> bindings = new(StringComparer.Ordinal);
        foreach (LirInlineAsmBinding binding in inlineAsm.Bindings)
            bindings[binding.Name] = LowerOperand(binding.Operand);

        IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels = CreateInlineAsmLocalLabels(ctx, inlineAsm.ParsedLines);

        bool isVolatile = inlineAsm.Volatility == AsmVolatility.Volatile;

        if (TryLowerTypedInlineAsm(nodes, inlineAsm, bindings, localLabels, isVolatile))
            return;

        // After label restriction (E0306), all valid inline asm should be typed-lowerable.
        Assert.Unreachable();
    }

    private static bool TryLowerTypedInlineAsm(
        List<AsmNode> nodes,
        LirInlineAsmInstruction inlineAsm,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels,
        bool isVolatile)
    {
        Queue<InlineAsmLine> parsedLines = new(inlineAsm.ParsedLines);
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

            if (!parsedLines.TryDequeue(out InlineAsmLine? line))
                return false;

            if (line is InlineAsmLabelLine labelLine)
            {
                lowered.Add(new AsmLabelNode(RewriteInlineAsmLocalLabel(labelLine.Label, localLabels)));
                if (sourceLine.CommentText is not null)
                    lowered.Add(new AsmCommentNode(sourceLine.CommentText));
                continue;
            }

            if (!TryLowerParsedInlineAsmLine(line, bindings, localLabels, out AsmInstructionNode? instruction))
                return false;

            if (isVolatile)
                instruction = WithNonElidable(instruction!);

            lowered.Add(instruction!);
            if (sourceLine.CommentText is not null)
                lowered.Add(new AsmCommentNode(sourceLine.CommentText));
        }

        if (parsedLines.Count != 0)
            return false;

        if (isVolatile)
        {
            nodes.Add(new AsmVolatileRegionBeginNode());
            nodes.Add(new AsmCommentNode("inline asm volatile begin"));
            nodes.AddRange(lowered);
            nodes.Add(new AsmCommentNode("inline asm volatile end"));
            nodes.Add(new AsmVolatileRegionEndNode());
        }
        else
        {
            nodes.Add(new AsmCommentNode("inline asm typed begin"));
            nodes.AddRange(lowered);
            nodes.Add(new AsmCommentNode("inline asm typed end"));
        }

        return true;
    }

    private static AsmInstructionNode WithNonElidable(AsmInstructionNode instruction)
    {
        if (instruction.IsNonElidable)
            return instruction;

        return new AsmInstructionNode(
            instruction.Mnemonic,
            instruction.Operands,
            instruction.Condition,
            instruction.FlagEffect,
            isNonElidable: true);
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
        InlineAsmLine line,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels,
        out AsmInstructionNode? instruction)
    {
        instruction = null;
        if (line is not InlineAsmInstructionLine instructionLine)
            return false;

        List<AsmOperand> operands = new(instructionLine.Operands.Count);
        foreach (InlineAsmOperand operandText in instructionLine.Operands)
        {
            if (!TryLowerInlineAsmOperand(operandText, bindings, localLabels, out AsmOperand? operand))
                return false;
            operands.Add(operand!);
        }

        instruction = new AsmInstructionNode(
            instructionLine.Mnemonic,
            operands,
            instructionLine.Condition,
            instructionLine.FlagEffect ?? P2FlagEffect.None);
        return true;
    }

    private static bool TryLowerInlineAsmOperand(
        InlineAsmOperand operandNode,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels,
        out AsmOperand? operand)
    {
        operand = null;
        switch (operandNode)
        {
            case InlineAsmBindingRefOperand binding:
                return bindings.TryGetValue(binding.BindingName, out operand);

            case InlineAsmImmediateOperand immediate:
                operand = new AsmImmediateOperand(immediate.Value);
                return true;

            case InlineAsmCurrentAddressOperand current:
                operand = new AsmSymbolOperand(
                    AsmCurrentAddressSymbol.Instance,
                    current.AddressingMode == InlineAsmAddressingMode.Immediate
                        ? AsmSymbolAddressingMode.Immediate
                        : AsmSymbolAddressingMode.Register);
                return true;

            case InlineAsmLabelOperand label:
                operand = new AsmSymbolOperand(
                    RewriteInlineAsmLocalLabel(label.Label, localLabels),
                    label.AddressingMode == InlineAsmAddressingMode.Immediate
                        ? AsmSymbolAddressingMode.Immediate
                        : AsmSymbolAddressingMode.Register);
                return true;

            case InlineAsmSpecialRegisterOperand specialRegister:
                operand = new AsmSymbolOperand(specialRegister.Register);
                return true;

            case InlineAsmSymbolOperand symbol:
                operand = new AsmSymbolOperand(
                    new AsmNamedSymbol(symbol.Name, SymbolType.ControlFlowLabel),
                    symbol.AddressingMode == InlineAsmAddressingMode.Immediate
                        ? AsmSymbolAddressingMode.Immediate
                        : AsmSymbolAddressingMode.Register);
                return true;

            default:
                return false;
        }
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


    private static IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> CreateInlineAsmLocalLabels(
        LoweringContext ctx,
        IReadOnlyList<InlineAsmLine> parsedLines)
    {
        Dictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels = [];
        List<ControlFlowLabelSymbol> labels = parsedLines
            .OfType<InlineAsmLabelLine>()
            .Select(static line => line.Label)
            .Distinct()
            .ToList();
        if (labels.Count == 0)
            return localLabels;

        int blockOrdinal = ctx.NextInlineAsmBlockOrdinal++;
        foreach (ControlFlowLabelSymbol label in labels)
        {
            string rewrittenName = $"__asm_{ctx.FunctionOrdinal}_{blockOrdinal}_{EncodeInlineAsmLabelComponent(label.Name)}";
            localLabels[label] = new ControlFlowLabelSymbol(rewrittenName);
        }

        return localLabels;
    }

    private static ControlFlowLabelSymbol RewriteInlineAsmLocalLabel(
        ControlFlowLabelSymbol label,
        IReadOnlyDictionary<ControlFlowLabelSymbol, ControlFlowLabelSymbol> localLabels)
        => localLabels.TryGetValue(label, out ControlFlowLabelSymbol? rewritten) ? rewritten : label;

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

    private static void LowerConst(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        object? rawValue = ((LirImmediateOperand)op.Operands[0]).Value;
        object? normalizedValue = rawValue;
        if (op.ResultType is not null && TypeFacts.TryNormalizeValue(rawValue, op.ResultType, out object? converted))
            normalizedValue = converted;
        long value = GetImmediateValue(new LirImmediateOperand(normalizedValue, op.ResultType ?? BuiltinTypes.Unknown));

        // Bool/bit constants: use BITH (set bit 0) or BITL (clear bit 0).
        if (IsSingleBitType(op.ResultType))
        {
            Assert.Invariant(value is 0 or 1, "Single-bit constants must normalize to 0 or 1.");
            nodes.Add(Emit(value == 1 ? P2Mnemonic.BITH : P2Mnemonic.BITL, dest, new AsmImmediateOperand(0)));
            return;
        }

        nodes.Add(Emit(P2Mnemonic.MOV, dest, new AsmImmediateOperand(value)));
    }

    private static void LowerMov(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
    }

    private static void LowerLoadAddress(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);
        nodes.Add(Emit(P2Mnemonic.MOV, dest, place));
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
                nodes.Add(Emit(isArrayBase ? P2Mnemonic.MOV : P2Mnemonic.RDLUT, dest, place));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(Emit(
                    isArrayBase ? P2Mnemonic.MOV : SelectHubReadOpcode(RequireTypedResult(op, "load.place")),
                    dest,
                    place));
                break;
            default:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, place));
                break;
        }
    }

    private static void LowerLoadDeref(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirLoadDerefOperation operation,
        LoweringContext ctx)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand pointer = OpReg(op.Operands[0]);
        VariableStorageClass storageClass = operation.StorageClass;

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit(P2Mnemonic.RDLUT, dest, pointer));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(Emit(SelectHubReadOpcode(RequireTypedResult(op, "load.deref")), dest, pointer));
                break;
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                break;
        }
    }

    private static void LowerLoadMember(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirLoadMemberOperation operation,
        LoweringContext ctx)
    {
        AggregateMemberSymbol member = operation.Member;
        if (!TryGetAggregateValueShape(member.ByteOffset, op.ResultType, out AggregateAccessShape shape))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand receiver = OpReg(op.Operands[0]);
        EmitAggregateExtract(nodes, dest, receiver, shape, op.ResultType);
    }

    private static void LowerLoadIndex(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirLoadIndexOperation operation,
        LoweringContext ctx)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmOperand baseOp = LowerOperand(op.Operands[0]);
        AsmRegisterOperand index = OpReg(op.Operands[1]);
        VariableStorageClass storageClass = operation.StorageClass;

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit(P2Mnemonic.ADD, index, baseOp));
                nodes.Add(Emit(P2Mnemonic.RDLUT, dest, index));
                break;
            case VariableStorageClass.Hub:
                {
                    int elemSize = GetHubElementSize(op.ResultType);
                    if (elemSize > 1)
                        nodes.Add(Emit(P2Mnemonic.SHL, index, new AsmImmediateOperand(ShiftForSize(elemSize))));
                    nodes.Add(Emit(P2Mnemonic.ADD, index, baseOp));
                    nodes.Add(Emit(SelectHubReadOpcode(RequireTypedResult(op, "load.index")), dest, index));
                    break;
                }
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                break;
        }
    }

    private static void LowerStoreDeref(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirStoreDerefOperation operation,
        LoweringContext ctx)
    {
        AsmRegisterOperand pointer = OpReg(op.Operands[0]);
        AsmOperand value = LowerOperand(op.Operands[^1]);
        VariableStorageClass storageClass = operation.StorageClass;

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(new AsmInstructionNode(P2Mnemonic.WRLUT, [value, pointer]));
                break;
            case VariableStorageClass.Hub:
                nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(RequireTypedResult(op, "store.deref")), [value, pointer]));
                break;
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                break;
        }
    }

    private static void LowerStoreIndex(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirStoreIndexOperation operation,
        LoweringContext ctx)
    {
        AsmOperand baseOp = LowerOperand(op.Operands[0]);
        AsmRegisterOperand index = OpReg(op.Operands[1]);
        AsmOperand value = LowerOperand(op.Operands[^1]);
        VariableStorageClass storageClass = operation.StorageClass;

        switch (storageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(Emit(P2Mnemonic.ADD, index, baseOp));
                nodes.Add(new AsmInstructionNode(P2Mnemonic.WRLUT, [value, index]));
                break;
            case VariableStorageClass.Hub:
                {
                    int elemSize = GetHubElementSize(op.ResultType);
                    if (elemSize > 1)
                        nodes.Add(Emit(P2Mnemonic.SHL, index, new AsmImmediateOperand(ShiftForSize(elemSize))));
                    nodes.Add(Emit(P2Mnemonic.ADD, index, baseOp));
                    nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(RequireTypedResult(op, "store.index")), [value, index]));
                    break;
                }
            default:
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                break;
        }
    }

    private static void LowerInsertMember(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirInsertMemberOperation operation,
        LoweringContext ctx)
    {
        AggregateMemberSymbol member = operation.Member;
        if (!TryGetAggregateMemberShape(op.ResultType, member.Name, member.ByteOffset, out AggregateAccessShape shape))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
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
        nodes.Add(Emit(P2Mnemonic.MOV, dest, src));

        if (op.ResultType is null || !TypeFacts.TryGetIntegerWidth(op.ResultType, out int width) || width >= 32)
            return;

        nodes.Add(TypeFacts.IsSignedInteger(op.ResultType)
            ? Emit(P2Mnemonic.SIGNX, dest, new AsmImmediateOperand(width - 1))
            : Emit(P2Mnemonic.ZEROX, dest, new AsmImmediateOperand(width - 1)));
    }

    private static void LowerStructLiteral(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirStructLiteralOperation operation,
        LoweringContext ctx)
    {
        if (op.ResultType is not StructTypeSymbol structType)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
            return;
        }

        if (!TryGetSingleWordAggregateSize(structType, out _))
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
            return;
        }

        if (operation.Members.Count != op.Operands.Count)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            nodes.Add(new AsmCommentNode($"invalid {op.DisplayName}"));
            return;
        }

        AsmRegisterOperand dest = DestReg(op);
        nodes.Add(Emit(P2Mnemonic.MOV, dest, new AsmImmediateOperand(0)));

        for (int i = 0; i < op.Operands.Count; i++)
        {
            AggregateMemberSymbol member = operation.Members[i];
            if (!structType.Members.TryGetValue(member.Name, out AggregateMemberSymbol? resolvedMember)
                || !TryGetAggregateMemberShape(structType, resolvedMember.Name, resolvedMember.ByteOffset, out AggregateAccessShape shape))
            {
                ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
                nodes.Add(new AsmCommentNode($"unhandled: {op.DisplayName}"));
                return;
            }

            AsmRegisterOperand value = OpReg(op.Operands[i]);
            EmitAggregateInsert(nodes, dest, dest, value, shape);
        }
    }

    private static void LowerBitfieldExtract(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirBitfieldExtractOperation operation)
    {
        int bitOffset = operation.Member.BitOffset;
        int bitWidth = operation.Member.BitWidth;

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);

        if (bitWidth == 1)
        {
            nodes.Add(Emit(P2Mnemonic.TESTB, src, new AsmImmediateOperand(bitOffset), flagEffect: P2FlagEffect.WC));
            nodes.Add(Emit(P2Mnemonic.WRC, dest));
            return;
        }

        if (bitWidth == 4 && bitOffset % 4 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.GETNIB, [dest, src, new AsmImmediateOperand(bitOffset / 4)]));
            return;
        }

        if (bitWidth == 8 && bitOffset % 8 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.GETBYTE, [dest, src, new AsmImmediateOperand(bitOffset / 8)]));
            if (op.ResultType is not null && TypeFacts.IsSignedInteger(op.ResultType))
                nodes.Add(Emit(P2Mnemonic.SIGNX, dest, new AsmImmediateOperand(7)));
            return;
        }

        if (bitWidth == 16 && bitOffset % 16 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.GETWORD, [dest, src, new AsmImmediateOperand(bitOffset / 16)]));
            if (op.ResultType is not null && TypeFacts.IsSignedInteger(op.ResultType))
                nodes.Add(Emit(P2Mnemonic.SIGNX, dest, new AsmImmediateOperand(15)));
            return;
        }

        nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
        if (bitOffset != 0)
            nodes.Add(Emit(P2Mnemonic.SHR, dest, new AsmImmediateOperand(bitOffset)));

        if (bitWidth < 32 && op.ResultType is not null)
        {
            nodes.Add(TypeFacts.IsSignedInteger(op.ResultType)
                ? Emit(P2Mnemonic.SIGNX, dest, new AsmImmediateOperand(bitWidth - 1))
                : Emit(P2Mnemonic.ZEROX, dest, new AsmImmediateOperand(bitWidth - 1)));
        }
    }

    private static void LowerBitfieldInsert(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirBitfieldInsertOperation operation,
        LoweringContext ctx)
    {
        int bitOffset = operation.Member.BitOffset;
        int bitWidth = operation.Member.BitWidth;

        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand source = OpReg(op.Operands[0]);
        AsmRegisterOperand value = OpReg(op.Operands[1]);

        nodes.Add(Emit(P2Mnemonic.MOV, dest, source));

        if (bitWidth == 32 && bitOffset == 0)
        {
            nodes.Add(Emit(P2Mnemonic.MOV, dest, value));
            return;
        }

        if (bitWidth == 1)
        {
            nodes.Add(Emit(P2Mnemonic.TESTB, value, new AsmImmediateOperand(0), flagEffect: P2FlagEffect.WC));
            nodes.Add(Emit(P2Mnemonic.BITC, dest, new AsmImmediateOperand(bitOffset)));
            return;
        }

        if (bitWidth == 4 && bitOffset % 4 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.SETNIB, [dest, value, new AsmImmediateOperand(bitOffset / 4)]));
            return;
        }

        if (bitWidth == 8 && bitOffset % 8 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.SETBYTE, [dest, value, new AsmImmediateOperand(bitOffset / 8)]));
            return;
        }

        if (bitWidth == 16 && bitOffset % 16 == 0)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.SETWORD, [dest, value, new AsmImmediateOperand(bitOffset / 16)]));
            return;
        }

        ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
        nodes.Add(new AsmCommentNode($"unhandled aligned fallback for {op.DisplayName}"));
    }

    private static bool TryGetAggregateValueShape(
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

        if (aggregateType is not AggregateTypeSymbol resolvedAggregateType)
            return false;

        if (!TryGetSingleWordAggregateSize(resolvedAggregateType, out _))
            return false;

        if (!resolvedAggregateType.Members.TryGetValue(memberName, out AggregateMemberSymbol? member))
            return false;

        if (member.ByteOffset != byteOffset)
        {
            return false;
        }

        return TryGetAggregateValueShape(member.ByteOffset, member.Type, out shape);
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
            nodes.Add(Emit(P2Mnemonic.MOV, dest, receiver));
            return;
        }

        if (shape.Kind == AggregateAccessKind.Byte)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.GETBYTE, [dest, receiver, new AsmImmediateOperand(shape.ByteOffset)]));
        }
        else
        {
            Assert.Invariant(shape.Kind == AggregateAccessKind.Word, $"Unexpected aggregate access kind '{shape.Kind}'.");
            nodes.Add(new AsmInstructionNode(P2Mnemonic.GETWORD, [dest, receiver, new AsmImmediateOperand(shape.ByteOffset / 2)]));
        }

        if (resultType is not null && TypeFacts.TryGetIntegerWidth(resultType, out int width) && width < 32)
        {
            nodes.Add(TypeFacts.IsSignedInteger(resultType)
                ? Emit(P2Mnemonic.SIGNX, dest, new AsmImmediateOperand(width - 1))
                : Emit(P2Mnemonic.ZEROX, dest, new AsmImmediateOperand(width - 1)));
        }
    }

    private static void EmitAggregateInsert(
        List<AsmNode> nodes,
        AsmRegisterOperand dest,
        AsmRegisterOperand receiver,
        AsmRegisterOperand value,
        AggregateAccessShape shape)
    {
        nodes.Add(Emit(P2Mnemonic.MOV, dest, receiver));

        if (shape.Kind == AggregateAccessKind.Long)
        {
            nodes.Add(Emit(P2Mnemonic.MOV, dest, value));
            return;
        }

        if (shape.Kind == AggregateAccessKind.Byte)
        {
            nodes.Add(new AsmInstructionNode(P2Mnemonic.SETBYTE, [dest, value, new AsmImmediateOperand(shape.ByteOffset)]));
        }
        else
        {
            Assert.Invariant(shape.Kind == AggregateAccessKind.Word, $"Unexpected aggregate access kind '{shape.Kind}'.");
            nodes.Add(new AsmInstructionNode(P2Mnemonic.SETWORD, [dest, value, new AsmImmediateOperand(shape.ByteOffset / 2)]));
        }
    }

    private static bool TryGetSingleWordAggregateSize(TypeSymbol type, out int sizeBytes)
    {
        var ok = TypeFacts.TryGetSizeBytes(type, out sizeBytes);
        Assert.Invariant(ok, $"Type '{type.Name}' must have a known size.");
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

    private static void LowerBinary(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirBinaryOperation operation,
        LoweringContext ctx)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand left = OpReg(op.Operands[0]);
        AsmRegisterOperand right = OpReg(op.Operands[1]);
        bool isFlagOnly = op.Destination is { } d && ctx.FlagOnlyRegisters.Contains(d);
        BoundBinaryOperatorKind kind = operation.OperatorKind;

        switch (kind)
        {
            case BoundBinaryOperatorKind.Add:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.ADD, dest, right));
                break;

            case BoundBinaryOperatorKind.Subtract:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.SUB, dest, right));
                break;

            case BoundBinaryOperatorKind.Multiply:
                nodes.Add(Emit(P2Mnemonic.QMUL, left, right));
                nodes.Add(Emit(P2Mnemonic.GETQX, dest));
                break;

            case BoundBinaryOperatorKind.Divide:
                nodes.Add(Emit(P2Mnemonic.QDIV, left, right));
                nodes.Add(Emit(P2Mnemonic.GETQX, dest));
                break;

            case BoundBinaryOperatorKind.Modulo:
                nodes.Add(Emit(P2Mnemonic.QDIV, left, right));
                nodes.Add(Emit(P2Mnemonic.GETQY, dest));
                break;

            case BoundBinaryOperatorKind.BitwiseAnd:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.AND, dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseOr:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.OR, dest, right));
                break;

            case BoundBinaryOperatorKind.BitwiseXor:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.XOR, dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftLeft:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.SHL, dest, right));
                break;

            case BoundBinaryOperatorKind.ShiftRight:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.SHR, dest, right));
                break;

            case BoundBinaryOperatorKind.ArithmeticShiftLeft:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.SHL, dest, right));
                break;

            case BoundBinaryOperatorKind.ArithmeticShiftRight:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.SAR, dest, right));
                break;

            case BoundBinaryOperatorKind.RotateLeft:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.ROL, dest, right));
                break;

            case BoundBinaryOperatorKind.RotateRight:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, left));
                nodes.Add(Emit(P2Mnemonic.ROR, dest, right));
                break;

            case BoundBinaryOperatorKind.Equals:
                nodes.Add(Emit(P2Mnemonic.CMP, left, right, flagEffect: P2FlagEffect.WZ));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITZ, dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.NotEquals:
                nodes.Add(Emit(P2Mnemonic.CMP, left, right, flagEffect: P2FlagEffect.WZ));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITNZ, dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.Less:
                nodes.Add(Emit(P2Mnemonic.CMP, left, right, flagEffect: P2FlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITC, dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.LessOrEqual:
                nodes.Add(Emit(P2Mnemonic.CMP, right, left, flagEffect: P2FlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITNC, dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.Greater:
                nodes.Add(Emit(P2Mnemonic.CMP, right, left, flagEffect: P2FlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITC, dest, new AsmImmediateOperand(0)));
                break;

            case BoundBinaryOperatorKind.GreaterOrEqual:
                nodes.Add(Emit(P2Mnemonic.CMP, left, right, flagEffect: P2FlagEffect.WC));
                if (!isFlagOnly)
                    nodes.Add(Emit(P2Mnemonic.BITNC, dest, new AsmImmediateOperand(0)));
                break;

            default:
                Assert.Unreachable($"Unexpected binary operator kind: {kind}");
                break;
        }
    }

    private static void LowerUnary(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirUnaryOperation operation)
    {
        AsmRegisterOperand dest = DestReg(op);
        AsmRegisterOperand src = OpReg(op.Operands[0]);
        BoundUnaryOperatorKind kind = operation.OperatorKind;

        switch (kind)
        {
            case BoundUnaryOperatorKind.Negation:
                nodes.Add(Emit(P2Mnemonic.NEG, dest, src));
                break;

            case BoundUnaryOperatorKind.LogicalNot:
                if (IsSingleBitType(op.ResultType))
                {
                    nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
                    nodes.Add(Emit(P2Mnemonic.BITNOT, dest, new AsmImmediateOperand(0)));
                }
                else
                {
                    nodes.Add(Emit(P2Mnemonic.CMP, src, new AsmImmediateOperand(0), flagEffect: P2FlagEffect.WZ));
                    nodes.Add(Emit(P2Mnemonic.WRZ, dest));
                }

                break;

            case BoundUnaryOperatorKind.BitwiseNot:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
                nodes.Add(Emit(P2Mnemonic.NOT, dest));
                break;

            case BoundUnaryOperatorKind.UnaryPlus:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
                break;

            case BoundUnaryOperatorKind.PostIncrement:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
                nodes.Add(Emit(P2Mnemonic.ADD, src, new AsmImmediateOperand(1)));
                break;

            case BoundUnaryOperatorKind.PostDecrement:
                nodes.Add(Emit(P2Mnemonic.MOV, dest, src));
                nodes.Add(Emit(P2Mnemonic.SUB, src, new AsmImmediateOperand(1)));
                break;

            default:
                Assert.Unreachable($"Unexpected unary operator kind: {kind}");
                break;
        }
    }

    private static void LowerCall(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        LirCallOperation operation = (LirCallOperation)op.Operation;
        FunctionSymbol target = operation.TargetFunction;
        CallingConventionTier calleeTier = ctx.CalleeTiers.GetValueOrDefault(target, CallingConventionTier.General);

        // Collect argument registers
        List<AsmRegisterOperand> args = [];
        for (int i = 0; i < op.Operands.Count; i++)
            args.Add(OpReg(op.Operands[i]));

        AsmRegisterOperand? destReg = op.Destination is { } dest ? new AsmRegisterOperand(ctx.GetRegister(dest)) : null;
        AsmSymbolOperand targetOp = new(new AsmFunctionReferenceSymbol(target), AsmSymbolAddressingMode.Immediate);

        switch (calleeTier)
        {
            case CallingConventionTier.Leaf:
                // CALLPA: param in PA, result in PA
                if (args.Count > 0)
                    nodes.Add(Emit(P2Mnemonic.MOV, new AsmSymbolOperand(P2SpecialRegister.PA), args[0]));
                nodes.Add(Emit(P2Mnemonic.CALLPA, new AsmSymbolOperand(P2SpecialRegister.PA), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit(P2Mnemonic.MOV, destReg, new AsmSymbolOperand(P2SpecialRegister.PA)));
                break;

            case CallingConventionTier.SecondOrder:
                // CALLPB: param in PB, result in PB
                if (args.Count > 0)
                    nodes.Add(Emit(P2Mnemonic.MOV, new AsmSymbolOperand(P2SpecialRegister.PB), args[0]));
                nodes.Add(Emit(P2Mnemonic.CALLPB, new AsmSymbolOperand(P2SpecialRegister.PB), targetOp));
                if (destReg is not null)
                    nodes.Add(Emit(P2Mnemonic.MOV, destReg, new AsmSymbolOperand(P2SpecialRegister.PB)));
                break;

            case CallingConventionTier.General:
            case CallingConventionTier.EntryPoint:
                // CALL: params in global registers, result in assigned register
                for (int i = 0; i < args.Count; i++)
                    nodes.Add(new AsmCommentNode($"arg{i} = {args[i].Format()}"));
                nodes.Add(Emit(P2Mnemonic.CALL, targetOp));
                if (destReg is not null)
                    nodes.Add(new AsmCommentNode($"result -> {destReg.Format()}"));
                break;

            case CallingConventionTier.Recursive:
                var ok = ctx.RecursiveCallingConvention.TryGetValue(target, out RecursiveCallingConventionInfo? recursiveInfo);
                Assert.Invariant(ok, "Recursive callees must have calling-convention metadata.");
                Assert.Invariant(recursiveInfo!.ParameterPlaces.Count == args.Count, "Recursive call arguments must match the callee parameter ABI.");

                for (int i = 0; i < args.Count; i++)
                    nodes.Add(Emit(P2Mnemonic.MOV, new AsmPlaceOperand(recursiveInfo.ParameterPlaces[i]), args[i]));

                nodes.Add(Emit(P2Mnemonic.CALLB, targetOp));

                if (destReg is not null)
                {
                    Assert.Invariant(recursiveInfo.RegisterReturnPlace is not null, "Recursive register-return calls must have a return storage place.");
                    nodes.Add(Emit(P2Mnemonic.MOV, destReg, new AsmPlaceOperand(recursiveInfo.RegisterReturnPlace)));
                }
                break;

            default:
                Assert.Unreachable($"Unexpected callee tier: {calleeTier}");
                return;
        }
    }

    private static void LowerCallExtractFlag(List<AsmNode> nodes, LirOpInstruction op, MirFlag flag)
    {
        Assert.Invariant(op.Destination is not null, "call.extract pseudo-op must have a destination register");
        LirVirtualRegister dest = op.Destination.Value;

        AsmRegisterOperand destReg = new(dest);
        P2Mnemonic opcode = flag switch
        {
            MirFlag.C => P2Mnemonic.BITC,
            MirFlag.NC => P2Mnemonic.BITNC,
            MirFlag.Z => P2Mnemonic.BITZ,
            MirFlag.NZ => P2Mnemonic.BITNZ,
            _ => Assert.UnreachableValue<P2Mnemonic>(),
        };
        nodes.Add(Emit(opcode, destReg, new AsmImmediateOperand(0)));
    }

    private static void LowerIntrinsic(List<AsmNode> nodes, LirOpInstruction op)
    {
        LirIntrinsicOperation intrinsic = (LirIntrinsicOperation)op.Operation;
        P2Mnemonic mnemonic = intrinsic.Mnemonic;
        List<AsmOperand> operands = [];
        for (int i = 0; i < op.Operands.Count; i++)
            operands.Add(LowerOperand(op.Operands[i]));

        if (op.Destination is { } dest
            && ShouldEmitIntrinsicDestination(mnemonic, operands.Count))
        {
            operands.Insert(0, new AsmRegisterOperand(dest));
        }

        nodes.Add(new AsmInstructionNode(mnemonic, operands));
    }

    private static bool ShouldEmitIntrinsicDestination(P2Mnemonic mnemonic, int explicitOperandCount)
    {
        if (P2InstructionMetadata.TryGetInstructionForm(mnemonic, explicitOperandCount + 1, out P2InstructionFormInfo formWithDestination))
            return FormWritesOperand(formWithDestination);

        return !P2InstructionMetadata.TryGetInstructionForm(mnemonic, explicitOperandCount, out _);
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

    private static void LowerStorePlace(List<AsmNode> nodes, LirOpInstruction op)
    {
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);
        AsmOperand valueOp = LowerOperand(op.Operands[1]);

        TypeSymbol? placeType = (place.Place.Symbol as VariableSymbol)?.Type;

        switch (place.Place.StorageClass)
        {
            case VariableStorageClass.Lut:
                nodes.Add(new AsmInstructionNode(P2Mnemonic.WRLUT, [valueOp, place]));
                break;
            case VariableStorageClass.Hub:
                Assert.Invariant(placeType is not null, "Hub place stores require a concrete place type.");
                nodes.Add(new AsmInstructionNode(SelectHubWriteOpcode(placeType), [valueOp, place]));
                break;
            case VariableStorageClass.Automatic:
            case VariableStorageClass.Reg:
                nodes.Add(new AsmInstructionNode(P2Mnemonic.MOV, [place, valueOp]));
                break;
            default:
                Assert.Unreachable($"Unexpected storage class: {place.Place.StorageClass}");
                break;
        }
    }

    private static void LowerUpdatePlace(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirUpdatePlaceOperation operation,
        LoweringContext ctx)
    {
        AsmPlaceOperand place = (AsmPlaceOperand)LowerOperand(op.Operands[0]);
        AsmOperand value = LowerOperand(op.Operands[1]);

        P2Mnemonic? opcode = operation.OperatorKind switch
            {
                BoundBinaryOperatorKind.Add => P2Mnemonic.ADD,
                BoundBinaryOperatorKind.Subtract => P2Mnemonic.SUB,
                BoundBinaryOperatorKind.BitwiseAnd => P2Mnemonic.AND,
                BoundBinaryOperatorKind.BitwiseOr => P2Mnemonic.OR,
                BoundBinaryOperatorKind.BitwiseXor => P2Mnemonic.XOR,
                BoundBinaryOperatorKind.ShiftLeft => P2Mnemonic.SHL,
                BoundBinaryOperatorKind.ShiftRight => P2Mnemonic.SHR,
                BoundBinaryOperatorKind.ArithmeticShiftLeft => P2Mnemonic.SHL,
                BoundBinaryOperatorKind.ArithmeticShiftRight => P2Mnemonic.SAR,
                _ => null,
            };

        if (opcode is null)
        {
            if (operation.OperatorKind == BoundBinaryOperatorKind.Modulo)
            {
                nodes.Add(new AsmInstructionNode(P2Mnemonic.QDIV, [place, value]));
                nodes.Add(new AsmInstructionNode(P2Mnemonic.GETQY, [place]));
                return;
            }

            ReportUnsupportedLowering(ctx, op.Span, "update.place");
            nodes.Add(new AsmCommentNode($"unhandled update place: {operation.OperatorKind}"));
            return;
        }

        nodes.Add(new AsmInstructionNode(opcode.Value, [place, value]));
    }

    private static P2Mnemonic SelectHubReadOpcode(TypeSymbol type)
    {
        if (type.IsBool)
            return P2Mnemonic.RDBYTE;

        if (TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            Assert.Invariant(width <= 32, $"Hub read width must be <= 32 bits, got {width}.");
            if (width <= 8)
                return P2Mnemonic.RDBYTE;
            if (width <= 16)
                return P2Mnemonic.RDWORD;

            return P2Mnemonic.RDLONG;
        }

        Assert.Invariant(TypeFacts.TryGetSizeBytes(type, out int sizeBytes), $"Hub read type must have a concrete in-memory size, got '{type}'.");
        Assert.Invariant(sizeBytes <= 4, $"Hub read size must be <= 4 bytes, got {sizeBytes} for '{type}'.");
        if (sizeBytes <= 1)
            return P2Mnemonic.RDBYTE;
        if (sizeBytes <= 2)
            return P2Mnemonic.RDWORD;

        return P2Mnemonic.RDLONG;
    }

    private static P2Mnemonic SelectHubWriteOpcode(TypeSymbol type)
    {
        if (type.IsBool)
            return P2Mnemonic.WRBYTE;

        if (TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            Assert.Invariant(width <= 32, $"Hub write width must be <= 32 bits, got {width}.");
            if (width <= 8)
                return P2Mnemonic.WRBYTE;
            if (width <= 16)
                return P2Mnemonic.WRWORD;

            return P2Mnemonic.WRLONG;
        }

        Assert.Invariant(TypeFacts.TryGetSizeBytes(type, out int sizeBytes), $"Hub write type must have a concrete in-memory size, got '{type}'.");
        Assert.Invariant(sizeBytes <= 4, $"Hub write size must be <= 4 bytes, got {sizeBytes} for '{type}'.");
        if (sizeBytes <= 1)
            return P2Mnemonic.WRBYTE;
        if (sizeBytes <= 2)
            return P2Mnemonic.WRWORD;

        return P2Mnemonic.WRLONG;
    }

    private static TypeSymbol RequireTypedResult(LirOpInstruction op, string lowering)
    {
        Assert.Invariant(op.ResultType is not null, $"Operation '{lowering}' must have a result type in ASM lowering.");
        return op.ResultType!;
    }

    private static void LowerRepSetup(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (op.Operands.Count < 1)
            return;

        ControlFlowLabelSymbol endLabel = PushRepEndLabel(ctx);
        AsmOperand iterations = LowerOperand(op.Operands[0]);
        nodes.Add(new AsmInstructionNode(P2Mnemonic.REP, [new AsmLabelRefOperand(endLabel), iterations]));
    }

    private static void LowerRepIter(List<AsmNode> nodes, LoweringContext ctx)
    {
        nodes.Add(new AsmLabelNode(PopRepEndLabel(ctx)));
    }

    private static void LowerRepForSetup(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (op.Operands.Count < 2)
            return;

        ControlFlowLabelSymbol endLabel = PushRepEndLabel(ctx);
        AsmOperand end = LowerOperand(op.Operands[1]);
        nodes.Add(new AsmInstructionNode(P2Mnemonic.REP, [new AsmLabelRefOperand(endLabel), end]));
    }

    private static void LowerRepForIter(List<AsmNode> nodes, LoweringContext ctx)
    {
        nodes.Add(new AsmLabelNode(PopRepEndLabel(ctx)));
    }

    private static void LowerNoIrqBegin(List<AsmNode> nodes, LoweringContext ctx)
    {
        ControlFlowLabelSymbol endLabel = PushRepEndLabel(ctx);
        nodes.Add(new AsmInstructionNode(P2Mnemonic.REP, [new AsmLabelRefOperand(endLabel), new AsmImmediateOperand(1)]));
    }

    private static void LowerNoIrqEnd(List<AsmNode> nodes, LoweringContext ctx)
    {
        nodes.Add(new AsmLabelNode(PopRepEndLabel(ctx)));
    }

    private static ControlFlowLabelSymbol PushRepEndLabel(LoweringContext ctx)
    {
        int ordinal = ctx.NextRepLabelOrdinal++;
        ControlFlowLabelSymbol label = new($"_rep_end_{ctx.FunctionOrdinal}_{ordinal}");
        ctx.RepEndLabelStack.Push(label);
        return label;
    }

    private static ControlFlowLabelSymbol PopRepEndLabel(LoweringContext ctx)
    {
        Assert.Invariant(ctx.RepEndLabelStack.Count > 0);
        return ctx.RepEndLabelStack.Pop();
    }

    private static void LowerYield(List<AsmNode> nodes, LirOpInstruction op, LoweringContext ctx)
    {
        if (ctx.Tier != CallingConventionTier.Interrupt)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            return;
        }

        P2Mnemonic resumeOpcode = ctx.Function.Kind switch
        {
            FunctionKind.Int1 => P2Mnemonic.RESI1,
            FunctionKind.Int2 => P2Mnemonic.RESI2,
            FunctionKind.Int3 => P2Mnemonic.RESI3,
            _ => Assert.UnreachableValue<P2Mnemonic>(),
        };
        nodes.Add(Emit(resumeOpcode));
    }

    private static void LowerYieldTo(
        List<AsmNode> nodes,
        LirOpInstruction op,
        LirYieldToOperation operation,
        LoweringContext ctx)
    {
        var hasTargetInfo = ctx.CoroutineCallingConvention.TryGetValue(operation.TargetFunction, out CoroutineCallingConventionInfo? targetInfo);
        if (!hasTargetInfo || targetInfo is null)
        {
            ReportUnsupportedOpcode(ctx, op.Span, op.DisplayName);
            return;
        }

        Assert.Invariant(targetInfo.ParameterPlaces.Count == op.Operands.Count, "Coroutine yieldto argument count must match target parameter ABI.");
        for (int i = 0; i < op.Operands.Count; i++)
            nodes.Add(Emit(P2Mnemonic.MOV, new AsmPlaceOperand(targetInfo.ParameterPlaces[i]), OpReg(op.Operands[i])));

        AsmOperand yieldStateDestination = ctx.Tier == CallingConventionTier.Coroutine
            && ctx.CoroutineCallingConvention.TryGetValue(ctx.Function.Symbol, out CoroutineCallingConventionInfo? sourceInfo)
            && sourceInfo is not null
            ? new AsmPlaceOperand(sourceInfo.StatePlace)
            : ctx.TopLevelYieldStatePlace is not null
                ? new AsmPlaceOperand(ctx.TopLevelYieldStatePlace)
                : Assert.UnreachableValue<AsmOperand>();

        nodes.Add(Emit(P2Mnemonic.CALLD, yieldStateDestination, new AsmPlaceOperand(targetInfo.StatePlace)));
    }

    private static void LowerTerminator(List<AsmNode> nodes, LoweringContext ctx, LirTerminator terminator)
    {
        switch (terminator)
        {
            case LirGotoTerminator goto_:
                EmitPhiMoves(nodes, goto_.Arguments, ctx, goto_.Target);
                nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(ctx.GetBlockLabel(goto_.Target), AsmSymbolAddressingMode.Immediate)));
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
                    nodes.Add(new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(P2SpecialRegister.PA), resultOp]));
                    break;

                case CallingConventionTier.SecondOrder:
                    // Result in PB
                    nodes.Add(new AsmInstructionNode(P2Mnemonic.MOV, [new AsmSymbolOperand(P2SpecialRegister.PB), resultOp]));
                    break;

                case CallingConventionTier.Recursive:
                    var ok = ctx.RecursiveCallingConvention.TryGetValue(ctx.Function.Symbol, out RecursiveCallingConventionInfo? recursiveInfo);
                    Assert.Invariant(ok, "Recursive functions must have calling-convention metadata.");
                    Assert.Invariant(recursiveInfo!.RegisterReturnPlace is not null, "Recursive register-return functions must have a return storage place.");
                    nodes.Add(new AsmInstructionNode(P2Mnemonic.MOV, [new AsmPlaceOperand(recursiveInfo.RegisterReturnPlace), resultOp]));
                    break;

                default:
                    // General: result stays in its register (caller knows which)
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
                nodes.Add(new AsmInstructionNode(P2Mnemonic.TESTB, [flagValue, new AsmImmediateOperand(0)], flagEffect: P2FlagEffect.WC));
            else if (placement == ReturnPlacement.FlagZ)
                nodes.Add(new AsmInstructionNode(P2Mnemonic.TESTB, [flagValue, new AsmImmediateOperand(0)], flagEffect: P2FlagEffect.WZ));
        }

        switch (ctx.Tier)
        {
            case CallingConventionTier.EntryPoint:
                // Entry point "returns" by halting: endless loop with interrupts shielded
                EmitHaltLoop(nodes);
                break;

            case CallingConventionTier.Recursive:
                nodes.Add(Emit(P2Mnemonic.RETB));
                break;

            case CallingConventionTier.Interrupt:
                FunctionKind kind = ctx.Function.Kind;
                if (ctx.ContainsYield)
                {
                    P2SpecialRegister interruptJumpRegister = kind switch
                    {
                        FunctionKind.Int1 => P2SpecialRegister.IJMP1,
                        FunctionKind.Int2 => P2SpecialRegister.IJMP2,
                        FunctionKind.Int3 => P2SpecialRegister.IJMP3,
                        _ => Assert.UnreachableValue<P2SpecialRegister>(),
                    };
                    nodes.Add(Emit(
                    P2Mnemonic.MOV,
                        new AsmSymbolOperand(interruptJumpRegister),
                        new AsmSymbolOperand(new AsmFunctionReferenceSymbol(ctx.Function.Symbol), AsmSymbolAddressingMode.Immediate)));
                }

                P2Mnemonic retInsn = kind switch
                {
                    FunctionKind.Int1 => P2Mnemonic.RETI1,
                    FunctionKind.Int2 => P2Mnemonic.RETI2,
                    FunctionKind.Int3 => P2Mnemonic.RETI3,
                    _ => P2Mnemonic.RET,
                };
                nodes.Add(Emit(retInsn));
                break;

            default:
                nodes.Add(Emit(P2Mnemonic.RET));
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
        nodes.Add(new AsmInstructionNode(P2Mnemonic.REP,
            [new AsmImmediateOperand(1), new AsmImmediateOperand(0)],
            isNonElidable: true));
        nodes.Add(new AsmInstructionNode(P2Mnemonic.NOP, [], isNonElidable: true));
    }

    private static void LowerBranch(List<AsmNode> nodes, LoweringContext ctx, LirBranchTerminator branch)
    {
        ControlFlowLabelSymbol trueLabel = ctx.GetBlockLabel(branch.TrueTarget);
        ControlFlowLabelSymbol falseLabel = ctx.GetBlockLabel(branch.FalseTarget);

        // Flag-aware branch: the condition already lives in a hardware flag (C or Z).
        // Use predicated jumps directly — no register test needed.
        if (branch.ConditionFlag is not null)
        {
            // The MirFlag encodes polarity: C/Z mean "true when flag set",
            // NC/NZ mean "true when flag clear".
            P2ConditionCode truePredicate = branch.ConditionFlag.Value switch
            {
                MirFlag.C => P2ConditionCode.IF_C,
                MirFlag.NC => P2ConditionCode.IF_NC,
                MirFlag.Z => P2ConditionCode.IF_Z,
                MirFlag.NZ => P2ConditionCode.IF_NZ,
                _ => Assert.UnreachableValue<P2ConditionCode>(),
            };
            P2ConditionCode falsePredicate = branch.ConditionFlag.Value switch
            {
                MirFlag.C => P2ConditionCode.IF_NC,
                MirFlag.NC => P2ConditionCode.IF_C,
                MirFlag.Z => P2ConditionCode.IF_NZ,
                MirFlag.NZ => P2ConditionCode.IF_Z,
                _ => Assert.UnreachableValue<P2ConditionCode>(),
            };

            if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
            {
                nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(falseLabel, AsmSymbolAddressingMode.Immediate), predicate: falsePredicate));
                nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(trueLabel, AsmSymbolAddressingMode.Immediate)));
            }
            else
            {
                EmitPhiMovesConditioned(nodes, branch.FalseArguments, ctx, branch.FalseTarget, falsePredicate);
                nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(falseLabel, AsmSymbolAddressingMode.Immediate), predicate: falsePredicate));
                EmitPhiMoves(nodes, branch.TrueArguments, ctx, branch.TrueTarget);
                nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(trueLabel, AsmSymbolAddressingMode.Immediate)));
            }

            return;
        }

        // Register-based branch: test the condition register.
        AsmRegisterOperand cond = OpReg(branch.Condition);

        if (branch.TrueArguments.Count == 0 && branch.FalseArguments.Count == 0)
        {
            nodes.Add(Emit(P2Mnemonic.TJZ, cond, new AsmSymbolOperand(falseLabel, AsmSymbolAddressingMode.Immediate)));
            nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(trueLabel, AsmSymbolAddressingMode.Immediate)));
        }
        else
        {
            nodes.Add(Emit(P2Mnemonic.CMP, cond, new AsmImmediateOperand(0), flagEffect: P2FlagEffect.WZ));

            // False path (Z=1, condition was zero)
            EmitPhiMovesConditioned(nodes, branch.FalseArguments, ctx, branch.FalseTarget, P2ConditionCode.IF_Z);
            nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(falseLabel, AsmSymbolAddressingMode.Immediate), predicate: P2ConditionCode.IF_Z));

            // True path (fall-through when NZ)
            EmitPhiMoves(nodes, branch.TrueArguments, ctx, branch.TrueTarget);
            nodes.Add(Emit(P2Mnemonic.JMP, new AsmSymbolOperand(trueLabel, AsmSymbolAddressingMode.Immediate)));
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
        LirBlockRef targetBlock,
        P2ConditionCode? predicate = null)
    {
        if (arguments.Count == 0)
            return;

        IReadOnlyList<LirBlockParameter>? targetParams = null;
        ctx.BlockParams.TryGetValue(targetBlock, out targetParams);

        for (int i = 0; i < arguments.Count; i++)
        {
            AsmOperand src = LowerOperand(arguments[i]);

            if (targetParams is not null && i < targetParams.Count)
            {
                AsmRegisterOperand paramReg = new(targetParams[i].Register);
                nodes.Add(new AsmInstructionNode(P2Mnemonic.MOV, [paramReg, src], predicate));
            }
            else
            {
                // Fallback: emit as comment if we can't resolve target param
                ReportUnsupportedLowering(ctx, new TextSpan(0, 0), "phi-move");
                string prefix = predicate is not null ? $"{P2InstructionMetadata.GetConditionPrefixText(predicate.Value)} " : "";
                nodes.Add(new AsmCommentNode($"{prefix}phi[{i}] = {src.Format()} -> {ctx.GetBlockLabel(targetBlock).Name}"));
            }
        }
    }

    private static void EmitPhiMovesConditioned(
        List<AsmNode> nodes,
        IReadOnlyList<LirOperand> arguments,
        LoweringContext ctx,
        LirBlockRef targetBlock,
        P2ConditionCode predicate)
    {
        EmitPhiMoves(nodes, arguments, ctx, targetBlock, predicate);
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
            throw new InvalidOperationException($"Instruction '{op.DisplayName}' expected a destination register");
        return new AsmRegisterOperand(dest);
    }

    private static AsmRegisterOperand OpReg(LirOperand operand)
    {
        if (operand is LirRegisterOperand reg)
            return new AsmRegisterOperand(reg.Register);
        throw new InvalidOperationException($"Expected register operand, got {operand.GetType().Name}");
    }

    private static AsmOperand LowerOperand(LirOperand operand)
    {
        return operand switch
        {
            LirRegisterOperand reg => new AsmRegisterOperand(reg.Register),
            LirImmediateOperand imm => new AsmImmediateOperand(GetImmediateValue(imm)),
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

    private static AsmInstructionNode Emit(
        P2Mnemonic opcode,
        AsmOperand op1,
        AsmOperand op2,
        P2ConditionCode? predicate = null,
        P2FlagEffect flagEffect = P2FlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1, op2], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        P2Mnemonic opcode,
        AsmOperand op1,
        P2ConditionCode? predicate = null,
        P2FlagEffect flagEffect = P2FlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [op1], predicate, flagEffect);
    }

    private static AsmInstructionNode Emit(
        P2Mnemonic opcode,
        P2FlagEffect flagEffect = P2FlagEffect.None)
    {
        return new AsmInstructionNode(opcode, [], null, flagEffect);
    }
}
