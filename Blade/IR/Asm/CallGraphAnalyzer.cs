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
    /// Exit is an endless halt loop (REP #1, #0 + NOP).</summary>
    EntryPoint,
}

/// <summary>
/// Result of call graph analysis: CC tiers and dead function set.
/// </summary>
public sealed class CallGraphResult
{
    public CallGraphResult(
        Dictionary<string, CallingConventionTier> tiers,
        HashSet<string> deadFunctions)
    {
        Tiers = tiers;
        DeadFunctions = deadFunctions;
    }

    /// <summary>CC tier for each function by name.</summary>
    public Dictionary<string, CallingConventionTier> Tiers { get; }

    /// <summary>Functions that are never called and not entry points — can be eliminated.</summary>
    public HashSet<string> DeadFunctions { get; }
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
        Dictionary<string, LirFunction> functionMap = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            functionMap[function.Name] = function;

        // Build call graph: function name → set of called function names
        Dictionary<string, HashSet<string>> callGraph = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            callGraph[function.Name] = CollectCallees(function);

        // Compute reachability from entry points and interrupt handlers
        HashSet<string> reachable = ComputeReachable(module, callGraph);

        // Identify dead functions
        HashSet<string> deadFunctions = [];
        foreach (LirFunction function in module.Functions)
        {
            if (!reachable.Contains(function.Name))
                deadFunctions.Add(function.Name);
        }

        // Assign tiers (only for reachable functions, but we tier everything
        // so callers can look up any name)
        Dictionary<string, CallingConventionTier> tiers = new(module.Functions.Count);

        foreach (LirFunction function in module.Functions)
        {
            CallingConventionTier tier = ResolveTier(function, callGraph, functionMap, tiers);
            tiers[function.Name] = tier;
        }

        return new CallGraphResult(tiers, deadFunctions);
    }

    /// <summary>
    /// Compute the set of reachable functions from all entry points and interrupt handlers.
    /// </summary>
    private static HashSet<string> ComputeReachable(
        LirModule module,
        Dictionary<string, HashSet<string>> callGraph)
    {
        HashSet<string> reachable = [];
        Queue<string> worklist = new();

        // Seed: entry points and interrupt handlers are always reachable
        foreach (LirFunction function in module.Functions)
        {
            if (function.IsEntryPoint
                || function.Kind is FunctionKind.Int1 or FunctionKind.Int2 or FunctionKind.Int3)
            {
                if (reachable.Add(function.Name))
                    worklist.Enqueue(function.Name);
            }
        }

        // Flood-fill transitively called functions
        while (worklist.Count > 0)
        {
            string current = worklist.Dequeue();
            if (callGraph.TryGetValue(current, out HashSet<string>? callees))
            {
                foreach (string callee in callees)
                {
                    if (reachable.Add(callee))
                        worklist.Enqueue(callee);
                }
            }
        }

        return reachable;
    }

    private static HashSet<string> CollectCallees(LirFunction function)
    {
        HashSet<string> callees = [];
        foreach (LirBlock block in function.Blocks)
        {
            foreach (LirInstruction instruction in block.Instructions)
            {
                if (instruction is LirOpInstruction op && op.Opcode == "call"
                    && op.Operands.Count > 0 && op.Operands[0] is LirSymbolOperand sym)
                {
                    callees.Add(sym.Symbol);
                }
                else if (instruction is LirOpInstruction yieldtoOp
                         && yieldtoOp.Opcode.StartsWith("yieldto:", System.StringComparison.Ordinal))
                {
                    string target = yieldtoOp.Opcode["yieldto:".Length..];
                    if (!string.IsNullOrEmpty(target))
                        callees.Add(target);
                }
            }
        }
        return callees;
    }

    private static CallingConventionTier ResolveTier(
        LirFunction function,
        Dictionary<string, HashSet<string>> callGraph,
        Dictionary<string, LirFunction> functionMap,
        Dictionary<string, CallingConventionTier> resolved)
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
        HashSet<string> callees = callGraph.GetValueOrDefault(function.Name) ?? [];

        if (callees.Count == 0)
            return CallingConventionTier.Leaf;

        // Check if all callees are leaves
        bool allCalleesAreLeaves = callees.All(callee =>
        {
            if (resolved.TryGetValue(callee, out CallingConventionTier calleeTier))
                return calleeTier == CallingConventionTier.Leaf;

            if (functionMap.TryGetValue(callee, out LirFunction? calleeFunc))
            {
                HashSet<string> calleeCallees = callGraph.GetValueOrDefault(callee) ?? [];
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
