using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Blade;
using Blade.IR;
using Blade.Semantics;

namespace Blade.IR.Asm;

/// <summary>
/// Whole-program register allocator that uses liveness analysis and bottom-up
/// call graph coloring to minimize COG register usage.
///
/// The algorithm:
/// 1. Intra-function liveness analysis → interference graph per function
/// 2. Intra-function graph coloring → minimum colors per function
/// 3. Inter-function bottom-up packing using the call graph:
///    - Leaves share the same register pool
///    - Local temps not live across calls reuse callee slots
///    - Values live across calls get slots disjoint from callee slots
///    - Interrupt handlers get fully disjoint register sets
/// 4. Rewrite all virtual register operands to symbol references
/// </summary>
public static class RegisterAllocator
{
    public static AsmModule Allocate(AsmModule module)
    {
        Requires.NotNull(module);

        if (module.Functions.Count == 0)
            return module;

        // Step 1: Run liveness analysis per function
        Dictionary<string, FunctionLiveness> livenessMap = [];
        foreach (AsmFunction function in module.Functions)
            livenessMap[function.Name] = LivenessAnalyzer.Analyze(function);

        // Step 2: Intra-function graph coloring with MOV coalescing
        Dictionary<string, Dictionary<int, int>> coloringMap = [];
        Dictionary<string, int> functionColorCounts = [];
        foreach (AsmFunction function in module.Functions)
        {
            FunctionLiveness liveness = livenessMap[function.Name];
            (Dictionary<int, int> coloring, int colorCount) = ColorFunction(function, liveness);
            coloringMap[function.Name] = coloring;
            functionColorCounts[function.Name] = colorCount;
        }

        // Step 3: Reconstruct call graph from ASMIR and do bottom-up packing
        Dictionary<string, HashSet<string>> callGraph = ReconstructCallGraph(module);
        Dictionary<string, Dictionary<int, int>> globalSlotMap = PackRegisters(
            module, callGraph, coloringMap, functionColorCounts, livenessMap);

        // Step 4: Rewrite operands and emit register file
        return RewriteModule(module, globalSlotMap);
    }

    // ── Intra-function coloring ─────────────────────────────────────

    /// <summary>
    /// Colors the interference graph for a function, with MOV coalescing.
    /// Returns (virtualRegId -> color, totalColors).
    /// </summary>
    private static (Dictionary<int, int> Coloring, int ColorCount) ColorFunction(
        AsmFunction function,
        FunctionLiveness liveness)
    {
        IReadOnlyDictionary<int, HashSet<int>> interference = liveness.InterferenceGraph;

        // Collect all register IDs
        HashSet<int> allRegs = [];
        foreach (AsmNode node in function.Nodes)
        {
            switch (node)
            {
                case AsmInstructionNode instruction:
                    foreach (AsmOperand operand in instruction.Operands)
                    {
                        if (operand is AsmRegisterOperand reg)
                            allRegs.Add(reg.RegisterId);
                    }
                    break;
                case AsmInlineTextNode inlineText:
                    foreach (AsmOperand operand in inlineText.Bindings.Values)
                    {
                        if (operand is AsmRegisterOperand reg)
                            allRegs.Add(reg.RegisterId);
                    }
                    break;
                case AsmImplicitUseNode implicitUse:
                    foreach (AsmOperand operand in implicitUse.Operands)
                    {
                        if (operand is AsmRegisterOperand reg)
                            allRegs.Add(reg.RegisterId);
                    }
                    break;
            }
        }

        if (allRegs.Count == 0)
            return ([], 0);

        // MOV coalescing: merge non-interfering registers connected by plain MOVs
        Dictionary<int, int> coalesceMap = BuildCoalesceMap(function, interference, allRegs);

        // Build coalesced interference graph
        Dictionary<int, HashSet<int>> coalescedInterference = [];
        foreach ((int reg, HashSet<int> neighbors) in interference)
        {
            int canonical = Canonical(reg, coalesceMap);
            if (!coalescedInterference.TryGetValue(canonical, out HashSet<int>? set))
            {
                set = [];
                coalescedInterference[canonical] = set;
            }
            foreach (int neighbor in neighbors)
            {
                int canonicalNeighbor = Canonical(neighbor, coalesceMap);
                if (canonicalNeighbor != canonical)
                    set.Add(canonicalNeighbor);
            }
        }

        // Ensure all registers appear in the graph
        foreach (int reg in allRegs)
        {
            int canonical = Canonical(reg, coalesceMap);
            if (!coalescedInterference.ContainsKey(canonical))
                coalescedInterference[canonical] = [];
        }

        // Greedy coloring ordered by interference degree (descending)
        List<int> order = coalescedInterference.Keys
            .OrderByDescending(r => coalescedInterference[r].Count)
            .ToList();

        Dictionary<int, int> canonicalColoring = [];
        int maxColor = 0;

        foreach (int reg in order)
        {
            HashSet<int> usedColors = [];
            if (coalescedInterference.TryGetValue(reg, out HashSet<int>? neighbors))
            {
                foreach (int neighbor in neighbors)
                {
                    if (canonicalColoring.TryGetValue(neighbor, out int neighborColor))
                        usedColors.Add(neighborColor);
                }
            }

            int color = 0;
            while (usedColors.Contains(color))
                color++;

            canonicalColoring[reg] = color;
            if (color >= maxColor)
                maxColor = color + 1;
        }

        // Map all original register IDs to their colors (through coalesce map)
        Dictionary<int, int> coloring = [];
        foreach (int reg in allRegs)
        {
            int canonical = Canonical(reg, coalesceMap);
            coloring[reg] = canonicalColoring[canonical];
        }

        return (coloring, maxColor);
    }

    /// <summary>
    /// Finds pairs of registers connected by plain MOV that can be coalesced
    /// (assigned the same color) because they don't interfere.
    /// </summary>
    private static Dictionary<int, int> BuildCoalesceMap(
        AsmFunction function,
        IReadOnlyDictionary<int, HashSet<int>> interference,
        HashSet<int> allRegs)
    {
        // Union-find style: coalesceMap[reg] -> canonical representative
        Dictionary<int, int> parent = [];
        foreach (int reg in allRegs)
            parent[reg] = reg;

        foreach (AsmNode node in function.Nodes)
        {
            if (node is not AsmInstructionNode instruction)
                continue;

            if (instruction.Opcode != "MOV"
                || instruction.Predicate is not null
                || instruction.FlagEffect != AsmFlagEffect.None
                || instruction.Operands.Count != 2)
            {
                continue;
            }

            if (instruction.Operands[0] is not AsmRegisterOperand dest
                || instruction.Operands[1] is not AsmRegisterOperand src)
            {
                continue;
            }

            int destCanonical = Find(parent, dest.RegisterId);
            int srcCanonical = Find(parent, src.RegisterId);

            if (destCanonical == srcCanonical)
                continue;

            // Check if they interfere
            bool interferes = interference.TryGetValue(dest.RegisterId, out HashSet<int>? neighbors)
                              && neighbors.Contains(src.RegisterId);

            if (!interferes)
            {
                // Coalesce: merge smaller into larger canonical
                parent[destCanonical] = srcCanonical;
            }
        }

        return parent;
    }

    private static int Find(Dictionary<int, int> parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // path compression
            x = parent[x];
        }
        return x;
    }

    private static int Canonical(int reg, Dictionary<int, int> coalesceMap)
        => Find(coalesceMap, reg);

    // ── Call graph reconstruction from ASMIR ─────────────────────────

    private static Dictionary<string, HashSet<string>> ReconstructCallGraph(AsmModule module)
    {
        HashSet<string> functionNames = [];
        foreach (AsmFunction function in module.Functions)
            functionNames.Add(function.Name);

        Dictionary<string, HashSet<string>> callGraph = [];
        foreach (AsmFunction function in module.Functions)
        {
            HashSet<string> callees = [];
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction && P2InstructionMetadata.IsCall(instruction.Opcode, instruction.Operands.Count))
                {
                    foreach (AsmOperand operand in instruction.Operands)
                    {
                        if (operand is AsmSymbolOperand symbol && functionNames.Contains(symbol.Name))
                            callees.Add(symbol.Name);
                    }
                }
            }
            callGraph[function.Name] = callees;
        }

        return callGraph;
    }

    /// <summary>
    /// Computes the transitive callee set for a function.
    /// </summary>
    private static HashSet<string> GetTransitiveCallees(
        string functionName,
        Dictionary<string, HashSet<string>> callGraph,
        Dictionary<string, HashSet<string>> cache)
    {
        if (cache.TryGetValue(functionName, out HashSet<string>? cached))
            return cached;

        HashSet<string> result = [];
        Queue<string> worklist = new();
        foreach (string callee in callGraph.GetValueOrDefault(functionName) ?? [])
            worklist.Enqueue(callee);

        while (worklist.Count > 0)
        {
            string current = worklist.Dequeue();
            if (!result.Add(current))
                continue;
            foreach (string callee in callGraph.GetValueOrDefault(current) ?? [])
                worklist.Enqueue(callee);
        }

        cache[functionName] = result;
        return result;
    }

    // ── Inter-function register packing ─────────────────────────────

    private static Dictionary<string, Dictionary<int, int>> PackRegisters(
        AsmModule module,
        Dictionary<string, HashSet<string>> callGraph,
        Dictionary<string, Dictionary<int, int>> coloringMap,
        Dictionary<string, int> functionColorCounts,
        Dictionary<string, FunctionLiveness> livenessMap)
    {
        Dictionary<string, HashSet<string>> transitiveCache = [];

        // Compute topological order (reverse = leaves first)
        List<string> order = TopologicalSort(
            module.Functions.Select(f => f.Name).ToList(), callGraph);

        // Track per-function: which global slots are used
        Dictionary<string, HashSet<int>> functionGlobalSlots = [];

        // Pre-assign global AllocatableGlobalRegister storage places
        int nextGlobalSlot = 0;
        HashSet<int> globalVarSlots = [];
        foreach (StoragePlace place in module.StoragePlaces.Where(p => p.Kind == StoragePlaceKind.AllocatableGlobalRegister))
        {
            // Global vars get their own dedicated slots
            globalVarSlots.Add(nextGlobalSlot);
            nextGlobalSlot++;
        }

        // Map: functionName -> (intra-function color -> global slot)
        Dictionary<string, Dictionary<int, int>> functionColorToSlot = [];

        // Process functions bottom-up (leaves first)
        foreach (string funcName in order)
        {
            int colorCount = functionColorCounts.GetValueOrDefault(funcName, 0);
            if (colorCount == 0)
            {
                functionColorToSlot[funcName] = [];
                functionGlobalSlots[funcName] = [];
                continue;
            }

            FunctionLiveness liveness = livenessMap[funcName];
            Dictionary<int, int> coloring = coloringMap[funcName];
            AsmFunction function = module.Functions.First(f => f.Name == funcName);

            // Determine which colors are live across calls
            HashSet<int> colorsLiveAcrossCall = [];
            foreach (int reg in liveness.LiveAcrossCallRegisters)
            {
                if (coloring.TryGetValue(reg, out int color))
                    colorsLiveAcrossCall.Add(color);
            }

            // Compute the set of global slots used by all transitive callees
            HashSet<string> transitiveCallees = GetTransitiveCallees(funcName, callGraph, transitiveCache);
            HashSet<int> calleeSlots = [];
            foreach (string callee in transitiveCallees)
            {
                if (functionGlobalSlots.TryGetValue(callee, out HashSet<int>? slots))
                {
                    foreach (int slot in slots)
                        calleeSlots.Add(slot);
                }
            }

            // Check if this is an interrupt handler (needs fully disjoint slots)
            bool isInterrupt = function.CcTier == CallingConventionTier.Interrupt;

            // Assign global slots to each color
            Dictionary<int, int> colorToSlot = [];
            HashSet<int> usedByThisFunction = [];

            // All slots that cannot be used by ANY color in this function
            HashSet<int> alwaysForbidden = new(globalVarSlots);
            if (isInterrupt)
            {
                // Interrupt handlers must be disjoint from everything
                foreach ((string _, HashSet<int> otherSlots) in functionGlobalSlots)
                {
                    foreach (int s in otherSlots)
                        alwaysForbidden.Add(s);
                }
            }

            for (int color = 0; color < colorCount; color++)
            {
                HashSet<int> forbidden = new(alwaysForbidden);

                // Can't conflict with other colors already assigned in this function
                foreach (int usedSlot in usedByThisFunction)
                    forbidden.Add(usedSlot);

                // Colors live across calls must avoid callee slots
                if (colorsLiveAcrossCall.Contains(color))
                {
                    foreach (int calleeSlot in calleeSlots)
                        forbidden.Add(calleeSlot);
                }

                // Find lowest available slot
                int assignedSlot = 0;
                while (forbidden.Contains(assignedSlot))
                    assignedSlot++;

                colorToSlot[color] = assignedSlot;
                usedByThisFunction.Add(assignedSlot);

                if (assignedSlot >= nextGlobalSlot)
                    nextGlobalSlot = assignedSlot + 1;
            }

            functionColorToSlot[funcName] = colorToSlot;
            functionGlobalSlots[funcName] = usedByThisFunction;
        }

        // Build the final mapping: (function, virtualRegId) -> global slot
        Dictionary<string, Dictionary<int, int>> result = [];
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, int> coloring = coloringMap[function.Name];
            Dictionary<int, int> colorToSlot = functionColorToSlot[function.Name];
            Dictionary<int, int> regToSlot = [];

            foreach ((int regId, int color) in coloring)
            {
                if (colorToSlot.TryGetValue(color, out int slot))
                    regToSlot[regId] = slot;
            }

            result[function.Name] = regToSlot;
        }

        return result;
    }

    /// <summary>
    /// Topological sort of functions, returning leaves first (reverse topological order).
    /// </summary>
    private static List<string> TopologicalSort(
        List<string> functions,
        Dictionary<string, HashSet<string>> callGraph)
    {
        HashSet<string> visited = [];
        HashSet<string> inStack = [];
        List<string> result = [];

        foreach (string func in functions)
            Visit(func, callGraph, visited, inStack, result);

        // result is in reverse-post-order (callers last) — leaves are first
        return result;
    }

    private static void Visit(
        string func,
        Dictionary<string, HashSet<string>> callGraph,
        HashSet<string> visited,
        HashSet<string> inStack,
        List<string> result)
    {
        if (!visited.Add(func))
            return;

        inStack.Add(func);

        foreach (string callee in callGraph.GetValueOrDefault(func) ?? [])
        {
            if (inStack.Contains(callee))
                continue; // Back edge (cycle) — skip to avoid infinite recursion
            Visit(callee, callGraph, visited, inStack, result);
        }

        inStack.Remove(func);
        result.Add(func);
    }

    // ── Module rewriting ────────────────────────────────────────────

    private static AsmModule RewriteModule(
        AsmModule module,
        Dictionary<string, Dictionary<int, int>> globalSlotMap)
    {
        // Collect all used global slot numbers
        HashSet<int> allUsedSlots = [];
        foreach (Dictionary<int, int> regToSlot in globalSlotMap.Values)
        {
            foreach (int slot in regToSlot.Values)
                allUsedSlots.Add(slot);
        }

        // Rewrite functions
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<int, int> regToSlot = globalSlotMap[function.Name];
            List<AsmNode> rewrittenNodes = new(function.Nodes.Count);

            foreach (AsmNode node in function.Nodes)
            {
                switch (node)
                {
                    case AsmInstructionNode instruction:
                    {
                        List<AsmOperand> operands = new(instruction.Operands.Count);
                        foreach (AsmOperand operand in instruction.Operands)
                            operands.Add(RewriteOperand(operand, regToSlot));
                        rewrittenNodes.Add(new AsmInstructionNode(
                            instruction.Opcode, operands, instruction.Predicate, instruction.FlagEffect));
                        break;
                    }

                    case AsmInlineTextNode inlineText:
                        rewrittenNodes.Add(new AsmInlineTextNode(
                            RewriteInlineAsmText(inlineText.Text, inlineText.Bindings, inlineText.LocalLabels, regToSlot)));
                        break;

                    case AsmImplicitUseNode implicitUse:
                    {
                        List<AsmOperand> operands = new(implicitUse.Operands.Count);
                        foreach (AsmOperand operand in implicitUse.Operands)
                            operands.Add(RewriteOperand(operand, regToSlot));
                        rewrittenNodes.Add(new AsmImplicitUseNode(operands));
                        break;
                    }

                    default:
                        rewrittenNodes.Add(node);
                        break;
                }
            }

            functions.Add(new AsmFunction(function.Name, function.IsEntryPoint, function.CcTier, rewrittenNodes));
        }

        // Emit register file data section in the entry point function
        if (functions.Count == 0)
            return new AsmModule(module.StoragePlaces, functions);

        int targetIdx = functions.FindIndex(f => f.IsEntryPoint);
        if (targetIdx < 0)
            targetIdx = functions.Count - 1;

        AsmFunction target = functions[targetIdx];
        List<AsmNode> extendedNodes = new(target.Nodes);
        extendedNodes.Add(new AsmCommentNode("--- register file ---"));

        // Emit global variable storage places
        HashSet<string> emitted = [];
        foreach (StoragePlace place in module.StoragePlaces.Where(p => p.Kind == StoragePlaceKind.AllocatableGlobalRegister))
        {
            if (!emitted.Add(place.EmittedName))
                continue;
            extendedNodes.Add(new AsmLabelNode(place.EmittedName));
            extendedNodes.Add(new AsmDirectiveNode($"LONG {FormatStaticInitializer(place.StaticInitializer)}"));
        }

        // Emit shared register slots
        foreach (int slot in allUsedSlots.Order())
        {
            string label = SlotLabel(slot);
            if (!emitted.Add(label))
                continue;
            extendedNodes.Add(new AsmLabelNode(label));
            extendedNodes.Add(new AsmDirectiveNode("LONG 0"));
        }

        // Emit LUT variable storage places
        IReadOnlyList<StoragePlace> lutPlaces = module.StoragePlaces
            .Where(p => p.Kind == StoragePlaceKind.AllocatableLutEntry)
            .ToList();
        if (lutPlaces.Count > 0)
        {
            extendedNodes.Add(new AsmCommentNode("--- lut file ---"));
            foreach (StoragePlace place in lutPlaces)
            {
                extendedNodes.Add(new AsmLabelNode(place.EmittedName));
                int count = GetPlaceEntryCount(place);
                string initValue = FormatStaticInitializer(place.StaticInitializer);
                string directive = count > 1 ? $"LONG {initValue}[{count}]" : $"LONG {initValue}";
                extendedNodes.Add(new AsmDirectiveNode(directive));
            }
        }

        // Emit Hub variable storage places
        IReadOnlyList<StoragePlace> hubPlaces = module.StoragePlaces
            .Where(p => p.Kind == StoragePlaceKind.AllocatableHubEntry)
            .ToList();
        if (hubPlaces.Count > 0)
        {
            extendedNodes.Add(new AsmCommentNode("--- hub file ---"));
            foreach (StoragePlace place in hubPlaces)
            {
                string elemDirective = SelectHubDirective(place);
                int count = GetPlaceEntryCount(place);
                string initValue = FormatStaticInitializer(place.StaticInitializer);
                string directive = count > 1 ? $"{elemDirective} {initValue}[{count}]" : $"{elemDirective} {initValue}";
                extendedNodes.Add(new AsmLabelNode(place.EmittedName));
                extendedNodes.Add(new AsmDirectiveNode(directive));
            }
        }

        functions[targetIdx] = new AsmFunction(target.Name, target.IsEntryPoint, target.CcTier, extendedNodes);
        return new AsmModule(module.StoragePlaces, functions);
    }

    private static AsmOperand RewriteOperand(AsmOperand operand, IReadOnlyDictionary<int, int> regToSlot)
    {
        if (operand is AsmRegisterOperand reg && regToSlot.TryGetValue(reg.RegisterId, out int slot))
            return new AsmSymbolOperand(SlotLabel(slot));
        return operand;
    }

    private static string RewriteInlineAsmText(
        string text,
        IReadOnlyDictionary<string, AsmOperand> bindings,
        IReadOnlyDictionary<string, string> localLabels,
        IReadOnlyDictionary<int, int> regToSlot)
    {
        int commentIndex = text.AsSpan().IndexOf('\'');
        string codeText = commentIndex >= 0 ? text[..commentIndex] : text;
        string commentText = commentIndex >= 0 ? text[commentIndex..] : string.Empty;

        string rewritten = Regex.Replace(
            codeText,
            @"\{([A-Za-z0-9_$.]+)\}",
            match =>
            {
                string name = match.Groups[1].Value;
                if (!bindings.TryGetValue(name, out AsmOperand? operand))
                    return name;

                AsmOperand rewritten = RewriteOperand(operand, regToSlot);
                return rewritten switch
                {
                    AsmSymbolOperand symbol => symbol.Name,
                    AsmPlaceOperand place => place.Place.EmittedName,
                    _ => rewritten.Format(),
                };
            });

        rewritten = RewriteInlineAsmLocalLabels(rewritten, localLabels);
        return rewritten + commentText;
    }

    private static string RewriteInlineAsmLocalLabels(
        string text,
        IReadOnlyDictionary<string, string> localLabels)
    {
        if (localLabels.Count == 0 || string.IsNullOrEmpty(text))
            return text;

        Match labelDefinition = Regex.Match(
            text,
            @"^(?<leading>\s*)(?<label>[A-Za-z0-9_$]+)\s*:\s*$",
            RegexOptions.CultureInvariant);
        if (labelDefinition.Success)
        {
            string originalLabel = labelDefinition.Groups["label"].Value;
            if (localLabels.TryGetValue(originalLabel, out string? rewrittenLabel))
                return labelDefinition.Groups["leading"].Value + rewrittenLabel;
        }

        string rewritten = text;
        foreach ((string originalLabel, string rewrittenLabel) in localLabels.OrderByDescending(static pair => pair.Key.Length))
        {
            string pattern = $"(?<![A-Za-z0-9_$]){Regex.Escape(originalLabel)}(?![A-Za-z0-9_$])";
            rewritten = Regex.Replace(rewritten, pattern, rewrittenLabel, RegexOptions.CultureInvariant);
        }

        return rewritten;
    }

    private static string SlotLabel(int slot) => $"_r{slot}";

    private static string SelectHubDirective(StoragePlace place)
    {
        TypeSymbol type = GetPlaceElementType(place);
        if (TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            if (width <= 8) return "BYTE";
            if (width <= 16) return "WORD";
        }

        return "LONG";
    }

    private static int GetPlaceEntryCount(StoragePlace place)
    {
        if (place.Symbol is VariableSymbol { Type: ArrayTypeSymbol arrayType } && arrayType.Length.HasValue)
            return arrayType.Length.Value;

        return 1;
    }

    private static TypeSymbol GetPlaceElementType(StoragePlace place)
    {
        if (place.Symbol is VariableSymbol variable)
        {
            if (variable.Type is ArrayTypeSymbol arrayType)
                return arrayType.ElementType;
            return variable.Type;
        }

        return BuiltinTypes.U32;
    }

    private static string FormatStaticInitializer(object? value)
    {
        return value switch
        {
            null => "0",
            bool boolean => boolean ? "1" : "0",
            _ => value.ToString() ?? "0",
        };
    }
}
