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
    private enum AllocatedLocationKind
    {
        SpillSlot,
        PhysicalRegister,
        StoragePlace,
    }

    private readonly record struct AllocatedLocation(
        AllocatedLocationKind Kind,
        int SpillSlot,
        P2Register? PhysicalRegister,
        StoragePlace? StoragePlace)
    {
        public static AllocatedLocation ForSlot(int slot) => new(AllocatedLocationKind.SpillSlot, slot, null, null);
        public static AllocatedLocation ForPhysicalRegister(P2Register register) => new(AllocatedLocationKind.PhysicalRegister, 0, register, null);
        public static AllocatedLocation ForStoragePlace(StoragePlace place) => new(AllocatedLocationKind.StoragePlace, 0, null, place);
    }

    public static AsmModule Allocate(AsmModule module)
    {
        Requires.NotNull(module);

        if (module.Functions.Count == 0)
            return module;

        module = InsertRecursiveCallSpills(module);

        // Step 1: Run liveness analysis per function
        Dictionary<AsmFunction, FunctionLiveness> livenessMap = [];
        foreach (AsmFunction function in module.Functions)
            livenessMap[function] = LivenessAnalyzer.Analyze(function);

        // Step 2: Intra-function graph coloring with MOV coalescing
        Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, int>> coloringMap = [];
        Dictionary<AsmFunction, int> functionColorCounts = [];
        Dictionary<AsmFunction, Dictionary<int, AsmRegisterConstraint>> functionColorConstraints = [];
        foreach (AsmFunction function in module.Functions)
        {
            FunctionLiveness liveness = livenessMap[function];
            (Dictionary<VirtualAsmRegister, int> coloring, int colorCount, Dictionary<int, AsmRegisterConstraint> colorConstraints) = ColorFunction(function, liveness);
            coloringMap[function] = coloring;
            functionColorCounts[function] = colorCount;
            functionColorConstraints[function] = colorConstraints;
        }

        // Step 3: Reconstruct call graph from ASMIR and do bottom-up packing
        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph = ReconstructCallGraph(module);
        (
            Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, AllocatedLocation>> registerLocationMap,
            Dictionary<StoragePlace, AllocatedLocation> placeLocationMap) = PackRegisters(
                module,
                callGraph,
                coloringMap,
                functionColorCounts,
                functionColorConstraints,
                livenessMap);

        // Step 4: Rewrite operands and emit register file
        return RewriteModule(module, registerLocationMap, placeLocationMap);
    }

    private static AsmModule InsertRecursiveCallSpills(AsmModule module)
    {
        Dictionary<AsmFunction, FunctionLiveness> livenessMap = [];
        foreach (AsmFunction function in module.Functions)
            livenessMap[function] = LivenessAnalyzer.Analyze(function);

        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            FunctionLiveness liveness = livenessMap[function];
            List<AsmNode> rewrittenNodes = new(function.Nodes.Count);
            IReadOnlyDictionary<VirtualAsmRegister, int> registerOrder = ComputeRegisterOrder(function);

            for (int i = 0; i < function.Nodes.Count; i++)
            {
                if (function.Nodes[i] is not AsmInstructionNode instruction
                    || instruction.Mnemonic != P2Mnemonic.CALLB)
                {
                    rewrittenNodes.Add(function.Nodes[i]);
                    continue;
                }

                List<VirtualAsmRegister> liveRegisters = [];
                if (liveness.LiveRegistersByCallInstruction.TryGetValue(i, out HashSet<VirtualAsmRegister>? liveSet))
                    liveRegisters.AddRange(liveSet.OrderBy(register => registerOrder.GetValueOrDefault(register, int.MaxValue)));

                foreach (VirtualAsmRegister register in liveRegisters)
                    rewrittenNodes.Add(new AsmInstructionNode(P2Mnemonic.PUSHB, [new AsmRegisterOperand(register)]));

                rewrittenNodes.Add(instruction);

                for (int liveIndex = liveRegisters.Count - 1; liveIndex >= 0; liveIndex--)
                    rewrittenNodes.Add(new AsmInstructionNode(P2Mnemonic.POPB, [new AsmRegisterOperand(liveRegisters[liveIndex])]));
            }

            functions.Add(new AsmFunction(function, rewrittenNodes));
        }

        return new AsmModule(module.StoragePlaces, functions);
    }

    private static IReadOnlyDictionary<VirtualAsmRegister, int> ComputeRegisterOrder(AsmFunction function)
    {
        Dictionary<VirtualAsmRegister, int> order = [];
        int next = 0;

        void Track(AsmOperand operand)
        {
            if (operand is AsmRegisterOperand register && !order.ContainsKey(register.Register))
                order.Add(register.Register, next++);
        }

        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
            {
                foreach (AsmOperand operand in instruction.Operands)
                    Track(operand);
            }
        }

        return order;
    }

    // ── Intra-function coloring ─────────────────────────────────────

    /// <summary>
    /// Colors the interference graph for a function, with MOV coalescing.
    /// Returns (virtualRegId -> color, totalColors).
    /// </summary>
    private static (Dictionary<VirtualAsmRegister, int> Coloring, int ColorCount, Dictionary<int, AsmRegisterConstraint> ColorConstraints) ColorFunction(
        AsmFunction function,
        FunctionLiveness liveness)
    {
        IReadOnlyDictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> interference = liveness.InterferenceGraph;

        // Collect all register IDs
        HashSet<VirtualAsmRegister> allRegs = [];
        foreach (AsmNode node in function.Nodes)
        {
            if (node is AsmInstructionNode instruction)
            {
                foreach (AsmOperand operand in instruction.Operands)
                {
                    if (operand is AsmRegisterOperand reg)
                        allRegs.Add(reg.Register);
                }
            }
        }

        if (allRegs.Count == 0)
            return ([], 0, []);

        // MOV coalescing: merge non-interfering registers connected by plain MOVs
        Dictionary<VirtualAsmRegister, VirtualAsmRegister> coalesceMap = BuildCoalesceMap(function, interference, allRegs);

        // Build coalesced interference graph
        Dictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> coalescedInterference = [];
        foreach ((VirtualAsmRegister reg, HashSet<VirtualAsmRegister> neighbors) in interference)
        {
            VirtualAsmRegister canonical = Canonical(reg, coalesceMap);
            if (!coalescedInterference.TryGetValue(canonical, out HashSet<VirtualAsmRegister>? set))
            {
                set = [];
                coalescedInterference[canonical] = set;
            }
            foreach (VirtualAsmRegister neighbor in neighbors)
            {
                VirtualAsmRegister canonicalNeighbor = Canonical(neighbor, coalesceMap);
                if (canonicalNeighbor != canonical)
                    set.Add(canonicalNeighbor);
            }
        }

        // Ensure all registers appear in the graph
        foreach (VirtualAsmRegister reg in allRegs)
        {
            VirtualAsmRegister canonical = Canonical(reg, coalesceMap);
            if (!coalescedInterference.ContainsKey(canonical))
                coalescedInterference[canonical] = [];
        }

        // Greedy coloring ordered by interference degree (descending)
        List<VirtualAsmRegister> order = coalescedInterference.Keys
            .OrderByDescending(r => coalescedInterference[r].Count)
            .ToList();

        Dictionary<VirtualAsmRegister, int> canonicalColoring = [];
        Dictionary<int, AsmRegisterConstraint> colorConstraints = [];
        int maxColor = 0;

        foreach (VirtualAsmRegister reg in order)
        {
            HashSet<int> usedColors = [];
            if (coalescedInterference.TryGetValue(reg, out HashSet<VirtualAsmRegister>? neighbors))
            {
                foreach (VirtualAsmRegister neighbor in neighbors)
                {
                    if (canonicalColoring.TryGetValue(neighbor, out int neighborColor))
                        usedColors.Add(neighborColor);
                }
            }

            int color = 0;
            AsmRegisterConstraint? constraint = GetCanonicalConstraint(reg, function.RegisterConstraints, coalesceMap);
            while (usedColors.Contains(color)
                || (constraint is not null
                    && colorConstraints.TryGetValue(color, out AsmRegisterConstraint? existingConstraint)
                    && !RegisterConstraintsEquivalent(existingConstraint, constraint)))
            {
                color++;
            }

            canonicalColoring[reg] = color;
            if (constraint is not null)
                colorConstraints[color] = constraint;
            if (color >= maxColor)
                maxColor = color + 1;
        }

        // Map all original register IDs to their colors (through coalesce map)
        Dictionary<VirtualAsmRegister, int> coloring = [];
        foreach (VirtualAsmRegister reg in allRegs)
        {
            VirtualAsmRegister canonical = Canonical(reg, coalesceMap);
            coloring[reg] = canonicalColoring[canonical];
        }

        return (coloring, maxColor, colorConstraints);
    }

    /// <summary>
    /// Finds pairs of registers connected by plain MOV that can be coalesced
    /// (assigned the same color) because they don't interfere.
    /// </summary>
    private static Dictionary<VirtualAsmRegister, VirtualAsmRegister> BuildCoalesceMap(
        AsmFunction function,
        IReadOnlyDictionary<VirtualAsmRegister, HashSet<VirtualAsmRegister>> interference,
        HashSet<VirtualAsmRegister> allRegs)
    {
        // Union-find style: coalesceMap[reg] -> canonical representative
        Dictionary<VirtualAsmRegister, VirtualAsmRegister> parent = [];
        foreach (VirtualAsmRegister reg in allRegs)
            parent[reg] = reg;

        foreach (AsmNode node in function.Nodes)
        {
            if (node is not AsmInstructionNode instruction)
                continue;

            if (instruction.Mnemonic != P2Mnemonic.MOV
                || instruction.Condition is not null
                || instruction.FlagEffect != P2FlagEffect.None
                || instruction.IsPhiMove
                || instruction.Operands.Count != 2)
            {
                continue;
            }

            if (instruction.Operands[0] is not AsmRegisterOperand dest
                || instruction.Operands[1] is not AsmRegisterOperand src)
            {
                continue;
            }

            VirtualAsmRegister destCanonical = Find(parent, dest.Register);
            VirtualAsmRegister srcCanonical = Find(parent, src.Register);

            if (destCanonical == srcCanonical)
                continue;

            if (!CanCoalesce(function.RegisterConstraints, destCanonical, srcCanonical))
                continue;

            // Check if they interfere
            bool interferes = interference.TryGetValue(dest.Register, out HashSet<VirtualAsmRegister>? neighbors)
                              && neighbors.Contains(src.Register);

            if (!interferes)
            {
                // Coalesce: merge smaller into larger canonical
                parent[destCanonical] = srcCanonical;
            }
        }

        return parent;
    }

    private static VirtualAsmRegister Find(Dictionary<VirtualAsmRegister, VirtualAsmRegister> parent, VirtualAsmRegister register)
    {
        while (!ReferenceEquals(parent[register], register))
        {
            parent[register] = parent[parent[register]];
            register = parent[register];
        }
        return register;
    }

    private static VirtualAsmRegister Canonical(VirtualAsmRegister reg, Dictionary<VirtualAsmRegister, VirtualAsmRegister> coalesceMap)
        => Find(coalesceMap, reg);

    private static bool CanCoalesce(
        IReadOnlyDictionary<VirtualAsmRegister, AsmRegisterConstraint> constraints,
        VirtualAsmRegister left,
        VirtualAsmRegister right)
    {
        bool hasLeft = constraints.TryGetValue(left, out AsmRegisterConstraint? leftConstraint);
        bool hasRight = constraints.TryGetValue(right, out AsmRegisterConstraint? rightConstraint);
        if (!hasLeft || !hasRight)
            return true;

        return RegisterConstraintsEquivalent(leftConstraint!, rightConstraint!);
    }

    private static AsmRegisterConstraint? GetCanonicalConstraint(
        VirtualAsmRegister canonical,
        IReadOnlyDictionary<VirtualAsmRegister, AsmRegisterConstraint> constraints,
        Dictionary<VirtualAsmRegister, VirtualAsmRegister> coalesceMap)
    {
        foreach ((VirtualAsmRegister register, AsmRegisterConstraint constraint) in constraints)
        {
            if (coalesceMap.ContainsKey(register)
                && ReferenceEquals(Canonical(register, coalesceMap), canonical))
            {
                return constraint;
            }
        }

        return null;
    }

    private static bool RegisterConstraintsEquivalent(AsmRegisterConstraint left, AsmRegisterConstraint right)
    {
        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            AsmRegisterConstraintKind.FixedPhysicalRegister => left.FixedRegister == right.FixedRegister,
            AsmRegisterConstraintKind.TiedStoragePlace => ReferenceEquals(left.TiedPlace, right.TiedPlace),
            _ => false,
        };
    }

    // ── Call graph reconstruction from ASMIR ─────────────────────────

    private static Dictionary<AsmFunction, HashSet<AsmFunction>> ReconstructCallGraph(AsmModule module)
    {
        Dictionary<FunctionSymbol, AsmFunction> functionsBySymbol = [];
        foreach (AsmFunction function in module.Functions)
            functionsBySymbol[function.Symbol] = function;

        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph = [];
        foreach (AsmFunction function in module.Functions)
        {
            HashSet<AsmFunction> callees = [];
            foreach (AsmNode node in function.Nodes)
            {
                if (node is AsmInstructionNode instruction && P2InstructionMetadata.IsCall(instruction.Mnemonic, instruction.Operands.Count))
                {
                    foreach (AsmOperand operand in instruction.Operands)
                    {
                        if (operand is AsmSymbolOperand { Symbol: AsmFunctionReferenceSymbol functionReference }
                            && functionsBySymbol.TryGetValue(functionReference.Function, out AsmFunction? callee))
                        {
                            callees.Add(callee);
                        }
                    }
                }
            }
            callGraph[function] = callees;
        }

        return callGraph;
    }

    /// <summary>
    /// Computes the transitive callee set for a function.
    /// </summary>
    private static HashSet<AsmFunction> GetTransitiveCallees(
        AsmFunction function,
        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph,
        Dictionary<AsmFunction, HashSet<AsmFunction>> cache)
    {
        if (cache.TryGetValue(function, out HashSet<AsmFunction>? cached))
            return cached;

        HashSet<AsmFunction> result = [];
        Queue<AsmFunction> worklist = new();
        foreach (AsmFunction callee in callGraph.GetValueOrDefault(function) ?? [])
            worklist.Enqueue(callee);

        while (worklist.Count > 0)
        {
            AsmFunction current = worklist.Dequeue();
            if (!result.Add(current))
                continue;
            foreach (AsmFunction callee in callGraph.GetValueOrDefault(current) ?? [])
                worklist.Enqueue(callee);
        }

        cache[function] = result;
        return result;
    }

    // ── Inter-function register packing ─────────────────────────────

    private static (
        Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, AllocatedLocation>> RegisterLocations,
        Dictionary<StoragePlace, AllocatedLocation> PlaceLocations)
        PackRegisters(
        AsmModule module,
        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph,
        Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, int>> coloringMap,
        Dictionary<AsmFunction, int> functionColorCounts,
        Dictionary<AsmFunction, Dictionary<int, AsmRegisterConstraint>> functionColorConstraints,
        Dictionary<AsmFunction, FunctionLiveness> livenessMap)
    {
        Dictionary<AsmFunction, HashSet<AsmFunction>> transitiveCache = [];
        HashSet<StoragePlace> callClobberedPlaces = CollectCallClobberedPlaces(
            coloringMap,
            functionColorConstraints,
            livenessMap);

        // Compute topological order (reverse = leaves first)
        List<AsmFunction> order = TopologicalSort(module.Functions.ToList(), callGraph);

        // Track per-function: which spill slots are used
        Dictionary<AsmFunction, HashSet<int>> functionGlobalSlots = [];

        // Pre-assign dedicated register-backed storage places.
        int nextGlobalSlot = 0;
        HashSet<int> dedicatedRegisterSlots = [];
        Dictionary<StoragePlace, AllocatedLocation> placeLocations = [];
        foreach (StoragePlace place in module.StoragePlaces.Where(static p => p.IsDedicatedRegisterSlot))
        {
            placeLocations[place] = AllocatedLocation.ForSlot(nextGlobalSlot);
            dedicatedRegisterSlots.Add(nextGlobalSlot);
            nextGlobalSlot++;
        }

        // Map: functionName -> (intra-function color -> allocated location)
        Dictionary<AsmFunction, Dictionary<int, AllocatedLocation>> functionColorToLocation = [];

        // Process functions bottom-up (leaves first)
        foreach (AsmFunction function in order)
        {
            int colorCount = functionColorCounts.GetValueOrDefault(function, 0);
            FunctionLiveness liveness = livenessMap[function];
            Dictionary<VirtualAsmRegister, int> coloring = coloringMap[function];
            Dictionary<int, AsmRegisterConstraint> colorConstraints = functionColorConstraints.GetValueOrDefault(function) ?? [];

            // Determine which colors are live across calls
            HashSet<int> colorsLiveAcrossCall = [];
            foreach (VirtualAsmRegister reg in liveness.LiveAcrossCallRegisters)
            {
                if (coloring.TryGetValue(reg, out int color))
                    colorsLiveAcrossCall.Add(color);
            }

            // Compute the set of global slots used by all transitive callees
            HashSet<AsmFunction> transitiveCallees = GetTransitiveCallees(function, callGraph, transitiveCache);
            HashSet<int> calleeSlots = [];
            foreach (AsmFunction callee in transitiveCallees)
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
            Dictionary<int, AllocatedLocation> colorToLocation = [];
            HashSet<int> usedByThisFunction = [];

            // All slots that cannot be used by ANY color in this function
            HashSet<int> alwaysForbidden = new(dedicatedRegisterSlots);
            if (isInterrupt)
            {
                // Interrupt handlers must be disjoint from everything
                foreach ((AsmFunction _, HashSet<int> otherSlots) in functionGlobalSlots)
                {
                    foreach (int s in otherSlots)
                        alwaysForbidden.Add(s);
                }
            }

            IReadOnlyList<StoragePlace> sharedPlaces = [.. function.SharedRegisterPlaces
                .Where(static place => place.Kind == StoragePlaceKind.AllocatableInternalSharedRegister)
                .Distinct()];
            foreach (StoragePlace place in sharedPlaces)
            {
                if (placeLocations.ContainsKey(place))
                    continue;

                if (!callClobberedPlaces.Contains(place))
                {
                    P2Register? preferredRegister = place.PreferredRegisters.Count > 0
                        ? place.PreferredRegisters[0]
                        : null;
                    if (preferredRegister is { } physicalRegister)
                    {
                        placeLocations[place] = AllocatedLocation.ForPhysicalRegister(physicalRegister);
                        continue;
                    }
                }

                HashSet<int> forbidden = new(alwaysForbidden);
                foreach (int usedSlot in usedByThisFunction)
                    forbidden.Add(usedSlot);
                foreach (int calleeSlot in calleeSlots)
                    forbidden.Add(calleeSlot);

                int assignedSlot = 0;
                while (forbidden.Contains(assignedSlot))
                    assignedSlot++;

                placeLocations[place] = AllocatedLocation.ForSlot(assignedSlot);
                usedByThisFunction.Add(assignedSlot);
                if (assignedSlot >= nextGlobalSlot)
                    nextGlobalSlot = assignedSlot + 1;
            }

            for (int color = 0; color < colorCount; color++)
            {
                if (colorConstraints.TryGetValue(color, out AsmRegisterConstraint? constraint))
                {
                    AllocatedLocation constrainedLocation = constraint.Kind switch
                    {
                        AsmRegisterConstraintKind.FixedPhysicalRegister => AllocatedLocation.ForPhysicalRegister(constraint.FixedRegister!.Value),
                        AsmRegisterConstraintKind.TiedStoragePlace => GetLocationForPlace(constraint.TiedPlace!, placeLocations),
                        _ => throw new InvalidOperationException("Unknown register constraint kind."),
                    };

                    colorToLocation[color] = constrainedLocation;
                    if (constrainedLocation.Kind == AllocatedLocationKind.SpillSlot)
                    {
                        usedByThisFunction.Add(constrainedLocation.SpillSlot);
                        if (constrainedLocation.SpillSlot >= nextGlobalSlot)
                            nextGlobalSlot = constrainedLocation.SpillSlot + 1;
                    }

                    continue;
                }

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

                colorToLocation[color] = AllocatedLocation.ForSlot(assignedSlot);
                usedByThisFunction.Add(assignedSlot);

                if (assignedSlot >= nextGlobalSlot)
                    nextGlobalSlot = assignedSlot + 1;
            }

            functionColorToLocation[function] = colorToLocation;
            functionGlobalSlots[function] = usedByThisFunction;
        }

        // Build the final mapping: (function, virtualRegId) -> allocated location
        Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, AllocatedLocation>> result = [];
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<VirtualAsmRegister, int> coloring = coloringMap[function];
            Dictionary<int, AllocatedLocation> colorToLocation = functionColorToLocation.GetValueOrDefault(function) ?? [];
            Dictionary<VirtualAsmRegister, AllocatedLocation> regToLocation = [];

            foreach ((VirtualAsmRegister register, int color) in coloring)
            {
                if (colorToLocation.TryGetValue(color, out AllocatedLocation location))
                    regToLocation[register] = location;
            }

            result[function] = regToLocation;
        }

        return (result, placeLocations);
    }

    private static HashSet<StoragePlace> CollectCallClobberedPlaces(
        IReadOnlyDictionary<AsmFunction, Dictionary<VirtualAsmRegister, int>> coloringMap,
        IReadOnlyDictionary<AsmFunction, Dictionary<int, AsmRegisterConstraint>> functionColorConstraints,
        IReadOnlyDictionary<AsmFunction, FunctionLiveness> livenessMap)
    {
        HashSet<StoragePlace> places = [];

        foreach ((AsmFunction function, FunctionLiveness liveness) in livenessMap)
        {
            if (!functionColorConstraints.TryGetValue(function, out Dictionary<int, AsmRegisterConstraint>? colorConstraints))
                continue;

            if (!coloringMap.TryGetValue(function, out Dictionary<VirtualAsmRegister, int>? coloring))
                continue;

            foreach (VirtualAsmRegister liveRegister in liveness.LiveAcrossCallRegisters)
            {
                if (!coloring.TryGetValue(liveRegister, out int color))
                    continue;

                if (!colorConstraints.TryGetValue(color, out AsmRegisterConstraint? constraint))
                    continue;

                if (constraint.Kind == AsmRegisterConstraintKind.TiedStoragePlace)
                    places.Add(constraint.TiedPlace!);
            }
        }

        return places;
    }

    /// <summary>
    /// Topological sort of functions, returning leaves first (reverse topological order).
    /// </summary>
    private static List<AsmFunction> TopologicalSort(
        List<AsmFunction> functions,
        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph)
    {
        HashSet<AsmFunction> visited = [];
        HashSet<AsmFunction> inStack = [];
        List<AsmFunction> result = [];

        foreach (AsmFunction func in functions)
            Visit(func, callGraph, visited, inStack, result);

        // result is in reverse-post-order (callers last) — leaves are first
        return result;
    }

    private static void Visit(
        AsmFunction func,
        Dictionary<AsmFunction, HashSet<AsmFunction>> callGraph,
        HashSet<AsmFunction> visited,
        HashSet<AsmFunction> inStack,
        List<AsmFunction> result)
    {
        if (!visited.Add(func))
            return;

        inStack.Add(func);

        foreach (AsmFunction callee in callGraph.GetValueOrDefault(func) ?? [])
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
        Dictionary<AsmFunction, Dictionary<VirtualAsmRegister, AllocatedLocation>> registerLocationMap,
        Dictionary<StoragePlace, AllocatedLocation> placeLocationMap)
    {
        Dictionary<int, AsmSpillSlotSymbol> slotSymbols = [];

        AsmSpillSlotSymbol GetSlotSymbol(int slot)
        {
            if (!slotSymbols.TryGetValue(slot, out AsmSpillSlotSymbol? symbol))
            {
                symbol = new AsmSpillSlotSymbol(slot);
                slotSymbols.Add(slot, symbol);
            }

            return symbol;
        }

        // Rewrite functions
        List<AsmFunction> functions = new(module.Functions.Count);
        foreach (AsmFunction function in module.Functions)
        {
            Dictionary<VirtualAsmRegister, AllocatedLocation> regToLocation = registerLocationMap[function];
            List<AsmNode> rewrittenNodes = new(function.Nodes.Count);

            foreach (AsmNode node in function.Nodes)
            {
                switch (node)
                {
                    case AsmInstructionNode instruction:
                    {
                        List<AsmOperand> operands = new(instruction.Operands.Count);
                        foreach (AsmOperand operand in instruction.Operands)
                            operands.Add(RewriteOperand(operand, regToLocation, placeLocationMap, GetSlotSymbol));
                        rewrittenNodes.Add(new AsmInstructionNode(
                            instruction.Mnemonic,
                            operands,
                            instruction.Condition,
                            instruction.FlagEffect,
                            instruction.IsNonElidable));
                        break;
                    }

                    default:
                        rewrittenNodes.Add(node);
                        break;
                }
            }

            functions.Add(new AsmFunction(function, rewrittenNodes));
        }

        HashSet<int> allUsedSlots = CollectReferencedSpillSlots(functions);

        // Emit register file data section in the entry point function
        if (functions.Count == 0)
            return new AsmModule(module.StoragePlaces, functions);

        int targetIdx = functions.FindIndex(f => f.IsEntryPoint);
        if (targetIdx < 0)
            targetIdx = functions.Count - 1;

        AsmFunction target = functions[targetIdx];
        List<AsmNode> extendedNodes = new(target.Nodes);
        extendedNodes.Add(new AsmSectionNode(AsmStorageSection.Register));

        // Emit global variable storage places
        HashSet<IAsmSymbol> emitted = [];
        foreach (StoragePlace place in module.StoragePlaces.Where(static p => p.Kind == StoragePlaceKind.AllocatableGlobalRegister))
        {
            if (!emitted.Add(place))
                continue;
            extendedNodes.Add(new AsmLabelNode(place.EmittedName));
            extendedNodes.Add(new AsmDataNode(AsmDataDirective.Long, place.StaticInitializer));
        }

        // Emit shared register slots
        foreach (int slot in allUsedSlots.Order())
        {
            AsmSpillSlotSymbol label = GetSlotSymbol(slot);
            if (!emitted.Add(label))
                continue;
            extendedNodes.Add(new AsmLabelNode(label.Name));
            extendedNodes.Add(new AsmDataNode(AsmDataDirective.Long, 0));
        }

        // Emit LUT variable storage places
        IReadOnlyList<StoragePlace> lutPlaces = module.StoragePlaces
            .Where(p => p.Kind == StoragePlaceKind.AllocatableLutEntry)
            .ToList();
        if (lutPlaces.Count > 0)
        {
            extendedNodes.Add(new AsmSectionNode(AsmStorageSection.Lut));
            foreach (StoragePlace place in lutPlaces)
            {
                extendedNodes.Add(new AsmLabelNode(place.EmittedName));
                int count = GetPlaceEntryCount(place);
                extendedNodes.Add(new AsmDataNode(AsmDataDirective.Long, place.StaticInitializer, count));
            }
        }

        // Emit Hub variable storage places
        IReadOnlyList<StoragePlace> hubPlaces = module.StoragePlaces
            .Where(p => p.Kind == StoragePlaceKind.AllocatableHubEntry)
            .ToList();
        if (hubPlaces.Count > 0)
        {
            extendedNodes.Add(new AsmSectionNode(AsmStorageSection.Hub));
            foreach (StoragePlace place in hubPlaces)
            {
                AsmDataDirective directive = SelectDataDirective(GetPlaceElementType(place));
                int count = GetPlaceEntryCount(place);
                extendedNodes.Add(new AsmLabelNode(place.EmittedName));
                extendedNodes.Add(new AsmDataNode(directive, place.StaticInitializer, count));
            }
        }

        functions[targetIdx] = new AsmFunction(target, extendedNodes);
        return new AsmModule(module.StoragePlaces, functions);
    }

    private static HashSet<int> CollectReferencedSpillSlots(IReadOnlyList<AsmFunction> functions)
    {
        HashSet<int> slots = [];
        foreach (AsmFunction function in functions)
        {
            foreach (AsmNode node in function.Nodes)
            {
                if (node is not AsmInstructionNode instruction)
                    continue;

                foreach (AsmOperand operand in instruction.Operands)
                {
                    if (operand is AsmSymbolOperand { Symbol: AsmSpillSlotSymbol spillSlot })
                        slots.Add(spillSlot.Slot);
                }
            }
        }

        return slots;
    }

    private static AsmOperand RewriteOperand(
        AsmOperand operand,
        IReadOnlyDictionary<VirtualAsmRegister, AllocatedLocation> regToLocation,
        IReadOnlyDictionary<StoragePlace, AllocatedLocation> placeLocationMap,
        Func<int, AsmSpillSlotSymbol> getSlotSymbol)
    {
        if (operand is AsmRegisterOperand reg && regToLocation.TryGetValue(reg.Register, out AllocatedLocation registerLocation))
            return ToOperand(registerLocation, getSlotSymbol);

        if (operand is AsmPlaceOperand place
            && place.Place.IsInternalRegisterSlot
            && placeLocationMap.TryGetValue(place.Place, out AllocatedLocation placeLocation))
        {
            return ToOperand(placeLocation, getSlotSymbol);
        }

        return operand;
    }

    private static AllocatedLocation GetLocationForPlace(
        StoragePlace place,
        IDictionary<StoragePlace, AllocatedLocation> placeLocations)
    {
        if (placeLocations.TryGetValue(place, out AllocatedLocation location))
            return location;

        if (place.Kind == StoragePlaceKind.AllocatableGlobalRegister)
            return AllocatedLocation.ForStoragePlace(place);

        throw new InvalidOperationException($"Missing allocated location for internal register place '{place.Symbol.Name}'.");
    }

    private static AsmOperand ToOperand(AllocatedLocation location, Func<int, AsmSpillSlotSymbol> getSlotSymbol)
    {
        return location.Kind switch
        {
            AllocatedLocationKind.SpillSlot => new AsmSymbolOperand(getSlotSymbol(location.SpillSlot), AsmSymbolAddressingMode.Register),
            AllocatedLocationKind.PhysicalRegister => new AsmPhysicalRegisterOperand(location.PhysicalRegister!.Value),
            AllocatedLocationKind.StoragePlace => new AsmPlaceOperand(location.StoragePlace!),
            _ => throw new InvalidOperationException("Unknown allocated location kind."),
        };
    }

    private static AsmDataDirective SelectDataDirective(TypeSymbol type)
    {
        if (TypeFacts.TryGetIntegerWidth(type, out int width))
        {
            if (width <= 8) return AsmDataDirective.Byte;
            if (width <= 16) return AsmDataDirective.Word;
        }

        return AsmDataDirective.Long;
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

}
