using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;

namespace Blade.IR;

// ── Interfaces ──────────────────────────────────────────────────────────────

public interface IMirOptimization
{
    MirModule? Run(MirModule input);
}

public interface ILirOptimization
{
    LirModule? Run(LirModule input);
}

public interface IAsmOptimization
{
    AsmModule? Run(AsmModule input);
}

// ── ASM helper base class ───────────────────────────────────────────────────

public abstract class PerFunctionAsmOptimization : IAsmOptimization
{
    public AsmModule? Run(AsmModule input)
    {
        Requires.NotNull(input);

        bool anyChanged = false;
        List<AsmFunction> functions = new(input.Functions.Count);
        foreach (AsmFunction function in input.Functions)
        {
            AsmFunction? result = RunOnFunction(function);
            functions.Add(result ?? function);
            anyChanged |= result is not null;
        }

        return anyChanged ? new AsmModule(input.StoragePlaces, functions) : null;
    }

    protected abstract AsmFunction? RunOnFunction(AsmFunction input);
}

// ── ASM optimization state flags ────────────────────────────────────────────

[Flags]
public enum AsmOptimizationState
{
    None = 0,
    PreRegAlloc = 1,
    PostRegAlloc = 2,
}

// ── Wrapper classes ─────────────────────────────────────────────────────────

public abstract class Optimization(string name, int priority)
{
    public string Name { get; } = name;
    public int Priority { get; } = priority;
}

public sealed class MirOptimization(string name, int priority, bool runAfterIterations, IMirOptimization implementation)
    : Optimization(name, priority)
{
    public bool RunAfterIterations { get; } = runAfterIterations;
    public MirModule? Run(MirModule input) => implementation.Run(input);
}

public sealed class LirOptimization(string name, int priority, ILirOptimization implementation)
    : Optimization(name, priority)
{
    public LirModule? Run(LirModule input) => implementation.Run(input);
}

public sealed class AsmOptimization(string name, int priority, AsmOptimizationState state, IAsmOptimization implementation)
    : Optimization(name, priority)
{
    public AsmOptimizationState State { get; } = state;
    public AsmModule? Run(AsmModule input) => implementation.Run(input);
}

// ── Attributes ──────────────────────────────────────────────────────────────

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MirOptimizationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Priority { get; init; } = 500;
    public bool RunAfterIterations { get; init; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LirOptimizationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Priority { get; init; } = 500;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AsmOptimizationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Priority { get; init; } = 500;
    public AsmOptimizationState State { get; init; } = AsmOptimizationState.PreRegAlloc;
}

// ── Registry ────────────────────────────────────────────────────────────────

public static class OptimizationRegistry
{
    private static readonly Lazy<Registry<MirOptimization>> MirRegistry =
        new(static () => Discover<IMirOptimization, MirOptimizationAttribute, MirOptimization>(
            static attr => attr.Name, static attr => attr.Priority,
            static (name, priority, attr, impl) => new MirOptimization(name, priority, attr.RunAfterIterations, impl)));

    private static readonly Lazy<Registry<LirOptimization>> LirRegistry =
        new(static () => Discover<ILirOptimization, LirOptimizationAttribute, LirOptimization>(
            static attr => attr.Name, static attr => attr.Priority,
            static (name, priority, _, impl) => new LirOptimization(name, priority, impl)));

    private static readonly Lazy<Registry<AsmOptimization>> AsmRegistry =
        new(static () => Discover<IAsmOptimization, AsmOptimizationAttribute, AsmOptimization>(
            static attr => attr.Name, static attr => attr.Priority,
            static (name, priority, attr, impl) => new AsmOptimization(name, priority, attr.State, impl)));

    // ── Public API ──────────────────────────────────────────────────────

    public static MirOptimization? GetMirOptimization(string name)
        => MirRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static LirOptimization? GetLirOptimization(string name)
        => LirRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static AsmOptimization? GetAsmOptimization(string name)
        => AsmRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static IReadOnlyList<MirOptimization> AllMirOptimizations
        => MirRegistry.Value.OrderedInstances;

    public static IReadOnlyList<LirOptimization> AllLirOptimizations
        => LirRegistry.Value.OrderedInstances;

    public static IReadOnlyList<AsmOptimization> AllAsmOptimizations
        => AsmRegistry.Value.OrderedInstances;

    public static MirOptimization SingleCallsiteInlineMirOptimization
        => GetRequiredMirOptimization("single-callsite-inline");

    // ── Discovery ───────────────────────────────────────────────────────

    private static MirOptimization GetRequiredMirOptimization(string name)
    {
        MirOptimization? optimization = GetMirOptimization(name);
        Assert.Invariant(optimization is not null, $"MIR optimization '{name}' must exist.");
        return optimization;
    }

    private sealed class Registry<TWrapper>
    {
        public required IReadOnlyDictionary<string, TWrapper> InstancesByName { get; init; }
        public required IReadOnlyList<TWrapper> OrderedInstances { get; init; }
    }

    private static Registry<TWrapper> Discover<TInterface, TAttribute, TWrapper>(
        Func<TAttribute, string> getName,
        Func<TAttribute, int> getPriority,
        Func<string, int, TAttribute, TInterface, TWrapper> createWrapper)
        where TInterface : notnull
        where TAttribute : Attribute
    {
        Assembly assembly = typeof(OptimizationRegistry).Assembly;
        Dictionary<string, TWrapper> instancesByName = new(StringComparer.Ordinal);
        List<(TWrapper Wrapper, string Name, int Priority)> ordering = [];

        foreach (Type type in assembly.GetTypes())
        {
            TAttribute? attr = type.GetCustomAttribute<TAttribute>();
            if (attr is null)
                continue;

            if (!typeof(TInterface).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    $"Type {type.FullName} has [{typeof(TAttribute).Name}] but does not implement {typeof(TInterface).Name}.");
            }

            string name = getName(attr);
            int priority = getPriority(attr);
            TInterface instance = (TInterface)Activator.CreateInstance(type)!;
            TWrapper wrapper = createWrapper(name, priority, attr, instance);

            if (!instancesByName.TryAdd(name, wrapper))
            {
                throw new InvalidOperationException(
                    $"Duplicate optimization name '{name}' on type {type.FullName}.");
            }

            ordering.Add((wrapper, name, priority));
        }

        // Higher priority first, then alphabetical by name for same priority.
        ordering.Sort(static (a, b) =>
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });

        return new Registry<TWrapper>
        {
            InstancesByName = instancesByName,
            OrderedInstances = ordering.Select(static entry => entry.Wrapper).ToArray(),
        };
    }
}
