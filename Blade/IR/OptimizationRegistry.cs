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
    private static readonly Lazy<Registry<IMirOptimization, MirOptimizationAttribute>> MirRegistry =
        new(static () => Discover<IMirOptimization, MirOptimizationAttribute>(attr => attr.Name, attr => attr.Priority));

    private static readonly Lazy<Registry<ILirOptimization, LirOptimizationAttribute>> LirRegistry =
        new(static () => Discover<ILirOptimization, LirOptimizationAttribute>(attr => attr.Name, attr => attr.Priority));

    private static readonly Lazy<Registry<IAsmOptimization, AsmOptimizationAttribute>> AsmRegistry =
        new(static () => Discover<IAsmOptimization, AsmOptimizationAttribute>(attr => attr.Name, attr => attr.Priority));

    // ── Public API ──────────────────────────────────────────────────────

    public static IMirOptimization? GetMirOptimization(string name)
        => MirRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static ILirOptimization? GetLirOptimization(string name)
        => LirRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static IAsmOptimization? GetAsmOptimization(string name)
        => AsmRegistry.Value.InstancesByName.GetValueOrDefault(name);

    public static IReadOnlyList<IMirOptimization> AllMirOptimizations
        => MirRegistry.Value.OrderedInstances;

    public static IReadOnlyList<ILirOptimization> AllLirOptimizations
        => LirRegistry.Value.OrderedInstances;

    public static IReadOnlyList<IAsmOptimization> AllAsmOptimizations
        => AsmRegistry.Value.OrderedInstances;

    public static bool IsMirRunAfterIterations(IMirOptimization optimization)
    {
        Requires.NotNull(optimization);
        Registry<IMirOptimization, MirOptimizationAttribute> registry = MirRegistry.Value;
        return registry.AttributesByInstance.TryGetValue(optimization, out MirOptimizationAttribute? attr)
            && attr.RunAfterIterations;
    }

    public static IReadOnlyList<IAsmOptimization> GetAsmOptimizationsForState(
        AsmOptimizationState state,
        IReadOnlyList<IAsmOptimization> enabledOptimizations)
    {
        Requires.NotNull(enabledOptimizations);

        Registry<IAsmOptimization, AsmOptimizationAttribute> registry = AsmRegistry.Value;
        List<IAsmOptimization> result = [];
        foreach (IAsmOptimization optimization in enabledOptimizations)
        {
            if (registry.AttributesByInstance.TryGetValue(optimization, out AsmOptimizationAttribute? attr) &&
                (attr.State & state) != 0)
            {
                result.Add(optimization);
            }
        }

        return result;
    }

    // ── Discovery ───────────────────────────────────────────────────────

    public static IReadOnlyList<string> AllMirNames
        => MirRegistry.Value.OrderedNames;

    public static IReadOnlyList<string> AllLirNames
        => LirRegistry.Value.OrderedNames;

    public static IReadOnlyList<string> AllAsmNames
        => AsmRegistry.Value.OrderedNames;

    public static IReadOnlyList<IMirOptimization> ResolveMirOptimizations(HashSet<string> enabledNames)
    {
        Requires.NotNull(enabledNames);
        Registry<IMirOptimization, MirOptimizationAttribute> registry = MirRegistry.Value;
        return FilterByName(registry, enabledNames);
    }

    public static IReadOnlyList<ILirOptimization> ResolveLirOptimizations(HashSet<string> enabledNames)
    {
        Requires.NotNull(enabledNames);
        Registry<ILirOptimization, LirOptimizationAttribute> registry = LirRegistry.Value;
        return FilterByName(registry, enabledNames);
    }

    public static IReadOnlyList<IAsmOptimization> ResolveAsmOptimizations(HashSet<string> enabledNames)
    {
        Requires.NotNull(enabledNames);
        Registry<IAsmOptimization, AsmOptimizationAttribute> registry = AsmRegistry.Value;
        return FilterByName(registry, enabledNames);
    }

    private static IReadOnlyList<TInterface> FilterByName<TInterface, TAttribute>(
        Registry<TInterface, TAttribute> registry,
        HashSet<string> enabledNames)
        where TInterface : notnull
        where TAttribute : Attribute
    {
        List<TInterface> result = [];
        for (int i = 0; i < registry.OrderedInstances.Count; i++)
        {
            if (enabledNames.Contains(registry.OrderedNames[i]))
                result.Add(registry.OrderedInstances[i]);
        }

        return result;
    }

    // ── Discovery ───────────────────────────────────────────────────────

    private sealed class Registry<TInterface, TAttribute>
        where TAttribute : Attribute
    {
        public required IReadOnlyDictionary<string, TInterface> InstancesByName { get; init; }
        public required IReadOnlyDictionary<TInterface, TAttribute> AttributesByInstance { get; init; }
        public required IReadOnlyList<TInterface> OrderedInstances { get; init; }
        public required IReadOnlyList<string> OrderedNames { get; init; }
    }

    private static Registry<TInterface, TAttribute> Discover<TInterface, TAttribute>(
        Func<TAttribute, string> getName,
        Func<TAttribute, int> getPriority)
        where TInterface : notnull
        where TAttribute : Attribute
    {
        Assembly assembly = typeof(OptimizationRegistry).Assembly;
        Dictionary<string, TInterface> instancesByName = new(StringComparer.Ordinal);
        Dictionary<TInterface, TAttribute> attributesByInstance = [];
        List<(TInterface Instance, string Name, int Priority)> ordering = [];

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

            if (!instancesByName.TryAdd(name, instance))
            {
                throw new InvalidOperationException(
                    $"Duplicate optimization name '{name}' on type {type.FullName}.");
            }

            attributesByInstance.Add(instance, attr);
            ordering.Add((instance, name, priority));
        }

        // Higher priority first, then alphabetical by name for same priority.
        ordering.Sort(static (a, b) =>
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });

        IReadOnlyList<TInterface> orderedInstances = ordering.Select(static entry => entry.Instance).ToArray();
        IReadOnlyList<string> orderedNames = ordering.Select(static entry => entry.Name).ToArray();

        return new Registry<TInterface, TAttribute>
        {
            InstancesByName = instancesByName,
            AttributesByInstance = attributesByInstance,
            OrderedInstances = orderedInstances,
            OrderedNames = orderedNames,
        };
    }
}
