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

    public static IReadOnlyList<string> GetDefaultOrder(OptimizationStage stage)
        => stage switch
        {
            OptimizationStage.Mir => MirRegistry.Value.DefaultOrder,
            OptimizationStage.Lir => LirRegistry.Value.DefaultOrder,
            OptimizationStage.Asmir => AsmRegistry.Value.DefaultOrder,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };

    public static bool IsKnown(OptimizationStage stage, string name)
        => GetDefaultOrder(stage).Contains(name, StringComparer.Ordinal);

    public static IMirOptimization GetMirOptimization(string name)
        => MirRegistry.Value.Instances[name];

    public static ILirOptimization GetLirOptimization(string name)
        => LirRegistry.Value.Instances[name];

    public static IAsmOptimization GetAsmOptimization(string name)
        => AsmRegistry.Value.Instances[name];

    public static bool IsMirRunAfterIterations(string name)
    {
        Registry<IMirOptimization, MirOptimizationAttribute> registry = MirRegistry.Value;
        return registry.Attributes.TryGetValue(name, out MirOptimizationAttribute? attr) && attr.RunAfterIterations;
    }

    public static IReadOnlyList<string> GetAsmOptimizationsForState(
        AsmOptimizationState state,
        IReadOnlyList<string> enabledOptimizations)
    {
        Requires.NotNull(enabledOptimizations);

        Registry<IAsmOptimization, AsmOptimizationAttribute> registry = AsmRegistry.Value;
        List<string> result = [];
        foreach (string name in enabledOptimizations)
        {
            if (registry.Attributes.TryGetValue(name, out AsmOptimizationAttribute? attr) &&
                (attr.State & state) != 0)
            {
                result.Add(name);
            }
        }

        return result;
    }

    // ── Discovery ───────────────────────────────────────────────────────

    private sealed class Registry<TInterface, TAttribute>
        where TAttribute : Attribute
    {
        public required IReadOnlyDictionary<string, TInterface> Instances { get; init; }
        public required IReadOnlyDictionary<string, TAttribute> Attributes { get; init; }
        public required IReadOnlyList<string> DefaultOrder { get; init; }
    }

    private static Registry<TInterface, TAttribute> Discover<TInterface, TAttribute>(
        Func<TAttribute, string> getName,
        Func<TAttribute, int> getPriority)
        where TAttribute : Attribute
    {
        Assembly assembly = typeof(OptimizationRegistry).Assembly;
        Dictionary<string, TInterface> instances = new(StringComparer.Ordinal);
        Dictionary<string, TAttribute> attributes = new(StringComparer.Ordinal);
        List<(string Name, int Priority)> ordering = [];

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

            if (!instances.TryAdd(name, (TInterface)Activator.CreateInstance(type)!))
            {
                throw new InvalidOperationException(
                    $"Duplicate optimization name '{name}' on type {type.FullName}.");
            }

            attributes.Add(name, attr);
            ordering.Add((name, priority));
        }

        // Higher priority first, then alphabetical by name for same priority.
        ordering.Sort(static (a, b) =>
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });

        IReadOnlyList<string> defaultOrder = ordering.Select(static entry => entry.Name).ToArray();

        return new Registry<TInterface, TAttribute>
        {
            Instances = instances,
            Attributes = attributes,
            DefaultOrder = defaultOrder,
        };
    }
}
