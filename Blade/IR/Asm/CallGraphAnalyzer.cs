using System.Collections.Generic;
using System.Linq;
using Blade.IR.Lir;
using Blade.Semantics;

namespace Blade.IR.Asm;

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

    /// <summary>Entry point (top-level code). No calling convention — just runs.</summary>
    EntryPoint,
}

/// <summary>
/// Analyzes the static call graph of a LIR module and assigns calling convention tiers.
/// </summary>
public static class CallGraphAnalyzer
{
    /// <summary>
    /// Analyze the call graph and return a dictionary mapping function name to CC tier.
    /// </summary>
    public static Dictionary<string, CallingConventionTier> Analyze(LirModule module)
    {
        // Build a map of function name → function for lookups
        Dictionary<string, LirFunction> functionMap = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            functionMap[function.Name] = function;

        // Build call graph: function name → set of called function names
        Dictionary<string, HashSet<string>> callGraph = new(module.Functions.Count);
        foreach (LirFunction function in module.Functions)
            callGraph[function.Name] = CollectCallees(function);

        // Assign tiers
        Dictionary<string, CallingConventionTier> tiers = new(module.Functions.Count);

        foreach (LirFunction function in module.Functions)
        {
            CallingConventionTier tier = ResolveTier(function, callGraph, functionMap, tiers);
            tiers[function.Name] = tier;
        }

        return tiers;
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
        // Entry point is special
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

        // No callees → leaf
        if (callees.Count == 0)
            return CallingConventionTier.Leaf;

        // Check if all callees are leaves
        bool allCalleesAreLeaves = callees.All(callee =>
        {
            if (resolved.TryGetValue(callee, out CallingConventionTier calleeTier))
                return calleeTier == CallingConventionTier.Leaf;

            // If not yet resolved, check if the callee has no callees itself
            if (functionMap.TryGetValue(callee, out LirFunction? calleeFunc))
            {
                HashSet<string> calleeCallees = callGraph.GetValueOrDefault(callee) ?? [];
                return calleeCallees.Count == 0
                       || calleeFunc.Kind == FunctionKind.Leaf;
            }

            // External/unknown function — assume general
            return false;
        });

        if (allCalleesAreLeaves)
            return CallingConventionTier.SecondOrder;

        // Otherwise, general tier
        return CallingConventionTier.General;
    }
}
