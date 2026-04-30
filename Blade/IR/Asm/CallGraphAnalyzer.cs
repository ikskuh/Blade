using System.Collections.Generic;
using System.Linq;
using Blade;
using Blade.IR.Lir;
using Blade.Semantics;

namespace Blade.IR.Asm;

// TODO: Add C#-style attributes that can be attached to different AST nodes:
//   - [Used] attribute: marks a function/variable as used, preventing elimination
//     even if never called directly (e.g., for functions called via function pointers
//     or referenced externally).
//   - [LinkName("_start")] attribute: sets the name of the asm label exported for
//     the function, allowing interop with external assembly/linker conventions.
// These attributes should be parsed in the syntax layer, stored in the bound tree,
// and propagated through MIR/LIR so the call graph analyzer and codegen can use them.

/// <summary>
/// Resolved calling convention tier for a function, determined by call graph analysis.
/// </summary>
public enum CallingConventionTier
{
    /// <summary>Leaf function — calls nothing. Uses CALLPA/RET.</summary>
    Leaf,

    /// <summary>Calls only CALLPA-tier leaves. Uses CALLPB/RET.</summary>
    SecondOrder,

    /// <summary>General-purpose. Uses CALL/RET.</summary>
    General,

    /// <summary>Recursive function (explicit `rec fn`). Uses CALLB/RETB with PTRB hub stack.</summary>
    Recursive,

    /// <summary>Coroutine (explicit `coro fn`). Uses CALLD/CALLD.</summary>
    Coroutine,

    /// <summary>Interrupt handler (int1/int2/int3). Uses RETI1/RETI2/RETI3.</summary>
    Interrupt,

    /// <summary>Entry point (top-level code). No calling convention — just runs.
    /// Exit transfers to the runtime halt hook.</summary>
    EntryPoint,
}

/// <summary>
/// Result of call graph analysis: CC tiers and dead function set.
/// </summary>
public sealed class CallGraphResult(
    Dictionary<FunctionSymbol, CallingConventionTier> tiers,
    HashSet<FunctionSymbol> deadFunctions)
{

    /// <summary>CC tier for each function symbol.</summary>
    public Dictionary<FunctionSymbol, CallingConventionTier> Tiers { get; } = tiers;

    /// <summary>Functions that are never called and not entry points — can be eliminated.</summary>
    public HashSet<FunctionSymbol> DeadFunctions { get; } = deadFunctions;
}

/// <summary>
/// Analyzes the static call graph of a LIR module and assigns calling convention tiers.
/// Also identifies dead (unreachable) functions for elimination.
/// </summary>
public static class CallGraphAnalyzer
{
    /// <summary>
    /// Analyze the call graph, assign CC tiers, and identify dead functions.
    /// </summary>
    public static CallGraphResult Analyze(LirModule module)
    {
        Requires.NotNull(module);

        // Build maps
        Dictionary<FunctionSymbol, LirFunction> functionMap = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            functionMap[function.Symbol] = function;

        // Build call graph: function symbol -> set of called function symbols
        Dictionary<FunctionSymbol, HashSet<FunctionSymbol>> callGraph = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            callGraph[function.Symbol] = CollectCallees(function);

        // Compute reachability from entry points and interrupt handlers
        HashSet<FunctionSymbol> reachable = ComputeReachable(module, callGraph);

        // Identify dead functions
        HashSet<FunctionSymbol> deadFunctions = [];
        foreach (LirFunction function in module.Functions)
        {
            if (!reachable.Contains(function.Symbol))
                deadFunctions.Add(function.Symbol);
        }

        // Assign tiers (only for reachable functions, but we tier everything
        // so callers can look up any name)
        Dictionary<FunctionSymbol, CallingConventionTier> tiers = new(module.Functions.Count);

        foreach (LirFunction function in module.Functions)
        {
            CallingConventionTier tier = ResolveTier(function, callGraph, functionMap, tiers);
            tiers[function.Symbol] = tier;
        }

        return new CallGraphResult(tiers, deadFunctions);
    }

    /// <summary>
    /// Compute the set of reachable functions from all entry points and interrupt handlers.
    /// </summary>
    private static HashSet<FunctionSymbol> ComputeReachable(
        LirModule module,
        Dictionary<FunctionSymbol, HashSet<FunctionSymbol>> callGraph)
    {
        HashSet<FunctionSymbol> reachable = [];
        Queue<FunctionSymbol> worklist = new();

        if (reachable.Add(module.Image.EntryFunction))
            worklist.Enqueue(module.Image.EntryFunction);

        foreach (LirFunction function in module.Functions)
        {
            if (function.Kind is FunctionKind.Int1 or FunctionKind.Int2 or FunctionKind.Int3
                && reachable.Add(function.Symbol))
            {
                worklist.Enqueue(function.Symbol);
            }
        }

        // Flood-fill transitively called functions
        while (worklist.Count > 0)
        {
            FunctionSymbol current = worklist.Dequeue();
            if (callGraph.TryGetValue(current, out HashSet<FunctionSymbol>? callees))
            {
                foreach (FunctionSymbol callee in callees)
                {
                    if (reachable.Add(callee))
                        worklist.Enqueue(callee);
                }
            }
        }

        return reachable;
    }

    private static HashSet<FunctionSymbol> CollectCallees(LirFunction function)
    {
        HashSet<FunctionSymbol> callees = [];
        foreach (LirBlock block in function.Blocks)
        {
            foreach (LirInstruction instruction in block.Instructions)
            {
                if (instruction is LirOpInstruction { Operation: LirCallOperation call })
                {
                    callees.Add(call.TargetFunction);
                }
                else if (instruction is LirOpInstruction { Operation: LirYieldToOperation yieldTo })
                {
                    callees.Add(yieldTo.TargetFunction);
                }
            }
        }
        return callees;
    }

    private static CallingConventionTier ResolveTier(
        LirFunction function,
        Dictionary<FunctionSymbol, HashSet<FunctionSymbol>> callGraph,
        Dictionary<FunctionSymbol, LirFunction> functionMap,
        Dictionary<FunctionSymbol, CallingConventionTier> resolved)
    {
        if (function.IsEntryPoint)
            return CallingConventionTier.EntryPoint;

        // Explicit kinds override auto-tiering
        switch (function.Kind)
        {
            case FunctionKind.Rec:
                return CallingConventionTier.Recursive;
            case FunctionKind.Coro:
                return CallingConventionTier.Coroutine;
            case FunctionKind.Int1:
            case FunctionKind.Int2:
            case FunctionKind.Int3:
                return CallingConventionTier.Interrupt;
            case FunctionKind.Leaf:
                return CallingConventionTier.Leaf;
        }

        // Auto-tier Default functions based on call graph
        HashSet<FunctionSymbol> callees = callGraph.GetValueOrDefault(function.Symbol) ?? [];

        if (callees.Count == 0)
            return CallingConventionTier.Leaf;

        // Check if all callees are leaves
        bool allCalleesAreLeaves = callees.All(callee =>
        {
            if (resolved.TryGetValue(callee, out CallingConventionTier calleeTier))
                return calleeTier == CallingConventionTier.Leaf;

            if (functionMap.TryGetValue(callee, out LirFunction? calleeFunc))
            {
                HashSet<FunctionSymbol> calleeCallees = callGraph.GetValueOrDefault(callee) ?? [];
                return calleeCallees.Count == 0
                       || calleeFunc.Kind == FunctionKind.Leaf;
            }

            return false;
        });

        if (allCalleesAreLeaves)
            return CallingConventionTier.SecondOrder;

        return CallingConventionTier.General;
    }
}
