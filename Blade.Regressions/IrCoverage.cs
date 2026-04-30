using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blade.IR;
using Blade.IR.Asm;
using Blade.IR.Lir;
using Blade.IR.Mir;
using Blade.Semantics.Bound;

namespace Blade.Regressions;

public sealed class RegressionIrCoverageReport(
    IReadOnlyList<RegressionIrCoverageGroupResult> groups,
    IReadOnlyList<string> regressionMessages)
{
    public IReadOnlyList<RegressionIrCoverageGroupResult> Groups { get; } = groups;
    public IReadOnlyList<string> RegressionMessages { get; } = regressionMessages;
    public bool HasRegressions => RegressionMessages.Count > 0;
}

public sealed class RegressionIrCoverageGroupResult(
    string groupKey,
    string displayName,
    IReadOnlyList<string> coveredTypeNames,
    IReadOnlyList<string> uncoveredTypeNames)
{
    public string GroupKey { get; } = groupKey;
    public string DisplayName { get; } = displayName;
    public IReadOnlyList<string> CoveredTypeNames { get; } = coveredTypeNames;
    public IReadOnlyList<string> UncoveredTypeNames { get; } = uncoveredTypeNames;
}

internal sealed class RegressionIrCoverageSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly IReadOnlyList<IrCoverageGroupSpec> GroupSpecs =
    [
        new(IrCoverageGroup.Bound, "bound", "Bound Nodes", typeof(BoundNode)),
        new(IrCoverageGroup.Mir, "mir", "MIR Nodes", typeof(MirInstruction)),
        new(IrCoverageGroup.Lir, "lir", "LIR Nodes", typeof(LirOperation)),
        new(IrCoverageGroup.Asmir, "asmir", "ASMIR Nodes", typeof(AsmNode)),
    ];

    private static readonly IReadOnlyList<IrCoverageStageAccessor> StageAccessors = DiscoverStageAccessors();
    private static readonly IReadOnlyDictionary<IrCoverageGroup, IReadOnlyDictionary<Type, string>> AssemblyTypesByGroup = DiscoverAssemblyTypesByGroup();

    private readonly string guardFilePath;
    private readonly IrRegressionGuardFile guardFile;
    private readonly Dictionary<IrCoverageGroup, HashSet<string>> observedTypesByGroup = GroupSpecs.ToDictionary(
        static spec => spec.Group,
        static _ => new HashSet<string>(StringComparer.Ordinal));

    private RegressionIrCoverageSession(string guardFilePath, IrRegressionGuardFile guardFile)
    {
        this.guardFilePath = guardFilePath;
        this.guardFile = guardFile;
    }

    public static RegressionIrCoverageSession? TryCreate(string? guardFilePath, bool isFullRun)
    {
        if (!isFullRun)
            return null;
        if (string.IsNullOrWhiteSpace(guardFilePath))
            return null;

        return new RegressionIrCoverageSession(guardFilePath, LoadGuardFile(guardFilePath));
    }

    public void Record(IrBuildResult buildResult)
    {
        Requires.NotNull(buildResult);

        foreach (IrCoverageStageAccessor accessor in StageAccessors)
        {
            object? root = accessor.Property.GetValue(buildResult);
            if (root is null)
                continue;

            RecordStage(accessor.Group, root);
        }
    }

    public RegressionIrCoverageReport Complete()
    {
        List<RegressionIrCoverageGroupResult> groups = [];
        List<string> regressionMessages = [];

        foreach (IrCoverageGroupSpec spec in GroupSpecs)
        {
            HashSet<string> assemblyTypeNames = new(AssemblyTypesByGroup[spec.Group].Values, StringComparer.Ordinal);
            IrRegressionGuardBucket bucket = guardFile.GetBucket(spec.Group);

            SortedSet<string> covered = new(bucket.Covered, StringComparer.Ordinal);
            SortedSet<string> uncovered = new(bucket.Uncovered, StringComparer.Ordinal);
            uncovered.ExceptWith(covered);

            foreach (string typeName in assemblyTypeNames)
            {
                if (!covered.Contains(typeName))
                    uncovered.Add(typeName);
            }

            foreach (string typeName in observedTypesByGroup[spec.Group])
            {
                uncovered.Remove(typeName);
                covered.Add(typeName);
            }

            foreach (string typeName in covered)
            {
                if (!assemblyTypeNames.Contains(typeName))
                    continue;
                if (observedTypesByGroup[spec.Group].Contains(typeName))
                    continue;

                regressionMessages.Add(FormattableString.Invariant(
                    $"regression detected: {typeName} is not covered by the regression suite anymore"));
            }

            bucket.Covered = covered.ToList();
            bucket.Uncovered = uncovered.ToList();

            List<string> currentCovered = covered.Where(assemblyTypeNames.Contains).ToList();
            List<string> currentUncovered = uncovered.Where(assemblyTypeNames.Contains).ToList();
            groups.Add(new RegressionIrCoverageGroupResult(spec.JsonKey, spec.DisplayName, currentCovered, currentUncovered));
        }

        SaveGuardFile(guardFilePath, guardFile);
        return new RegressionIrCoverageReport(groups, regressionMessages);
    }

    private void RecordStage(IrCoverageGroup group, object root)
    {
        Stack<object> worklist = new();
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        worklist.Push(root);

        while (worklist.Count > 0)
        {
            object current = worklist.Pop();
            Type currentType = current.GetType();

            if (TryUnpackKeyValuePair(current, currentType, out object? key, out object? value))
            {
                Push(worklist, key);
                Push(worklist, value);
                continue;
            }

            if (ShouldSkip(currentType))
                continue;

            if (!visited.Add(current))
                continue;

            if (AssemblyTypesByGroup[group].TryGetValue(currentType, out string? typeName))
                observedTypesByGroup[group].Add(typeName);

            if (current is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    Push(worklist, entry.Key);
                    Push(worklist, entry.Value);
                }

                continue;
            }

            if (current is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                    Push(worklist, item);

                continue;
            }

            foreach (PropertyInfo property in GetTraversableProperties(currentType))
                Push(worklist, property.GetValue(current));
        }
    }

    private static bool ShouldSkip(Type type)
    {
        return type.IsValueType
            || type.IsEnum
            || type == typeof(string)
            || typeof(Type).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type);
    }

    private static void Push(Stack<object> worklist, object? value)
    {
        if (value is not null)
            worklist.Push(value);
    }

    private static bool TryUnpackKeyValuePair(object instance, Type type, out object? key, out object? value)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
        {
            key = null;
            value = null;
            return false;
        }

        key = type.GetProperty(nameof(KeyValuePair<int, int>.Key), BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance);
        value = type.GetProperty(nameof(KeyValuePair<int, int>.Value), BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance);
        return true;
    }

    private static IReadOnlyDictionary<IrCoverageGroup, IReadOnlyDictionary<Type, string>> DiscoverAssemblyTypesByGroup()
    {
        Assembly assembly = typeof(BoundNode).Assembly;
        Dictionary<IrCoverageGroup, IReadOnlyDictionary<Type, string>> result = [];

        foreach (IrCoverageGroupSpec spec in GroupSpecs)
        {
            Dictionary<string, List<Type>> typesByName = assembly.GetTypes()
                .Where(type => type.IsPublic && !type.IsAbstract && spec.BaseType.IsAssignableFrom(type))
                .GroupBy(static type => type.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);

            foreach ((string typeName, List<Type> matchingTypes) in typesByName)
            {
                if (matchingTypes.Count != 1)
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"IR coverage requires unique {spec.DisplayName} type names. Found duplicate '{typeName}'."));
                }
            }

            result[spec.Group] = typesByName.ToDictionary(
                static pair => pair.Value[0],
                static pair => pair.Key);
        }

        return result;
    }

    private static IReadOnlyList<IrCoverageStageAccessor> DiscoverStageAccessors()
    {
        List<IrCoverageStageAccessor> accessors = [];
        foreach (PropertyInfo property in typeof(IrBuildResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            IrCoverageGroup? group = ClassifyStageRoot(property.PropertyType);
            if (group is null)
                continue;

            accessors.Add(new IrCoverageStageAccessor(property, group.Value));
        }

        return accessors;
    }

    private static IrCoverageGroup? ClassifyStageRoot(Type type)
    {
        Type candidateType = UnwrapEnumerableElementType(type) ?? type;

        if (typeof(BoundProgram).IsAssignableFrom(candidateType) || typeof(BoundModule).IsAssignableFrom(candidateType))
            return IrCoverageGroup.Bound;
        if (typeof(MirModule).IsAssignableFrom(candidateType))
            return IrCoverageGroup.Mir;
        if (typeof(LirModule).IsAssignableFrom(candidateType))
            return IrCoverageGroup.Lir;
        if (typeof(AsmModule).IsAssignableFrom(candidateType))
            return IrCoverageGroup.Asmir;

        return null;
    }

    private static Type? UnwrapEnumerableElementType(Type type)
    {
        Requires.NotNull(type);

        if (type.IsArray)
            return type.GetElementType();

        if (!typeof(IEnumerable).IsAssignableFrom(type) || type == typeof(string))
            return null;

        if (type.IsGenericType && type.GetGenericArguments().Length == 1)
            return type.GetGenericArguments()[0];

        Type? enumerableInterface = type
            .GetInterfaces()
            .FirstOrDefault(static iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerableInterface?.GetGenericArguments()[0];
    }

    private static PropertyInfo[] GetTraversableProperties(Type type)
    {
        lock (TraversablePropertyCacheLock)
        {
            if (!TraversablePropertyCache.TryGetValue(type, out PropertyInfo[]? properties))
            {
                properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.GetMethod is not null && property.GetIndexParameters().Length == 0)
                    .Where(property => ShouldTraverseProperty(property.PropertyType))
                    .ToArray();
                TraversablePropertyCache.Add(type, properties);
            }

            return properties;
        }
    }

    private static bool ShouldTraverseProperty(Type propertyType)
    {
        if (ShouldSkip(propertyType))
            return false;

        if (typeof(IDictionary).IsAssignableFrom(propertyType))
            return true;

        if (typeof(IEnumerable).IsAssignableFrom(propertyType))
            return true;

        return true;
    }

    private static IrRegressionGuardFile LoadGuardFile(string path)
    {
        string json = File.ReadAllText(path);
        IrRegressionGuardFile? guardFile = JsonSerializer.Deserialize<IrRegressionGuardFile>(json, JsonOptions);
        if (guardFile is null)
            throw new InvalidOperationException("Failed to deserialize ir-regression-guard.json.");

        return guardFile;
    }

    private static void SaveGuardFile(string path, IrRegressionGuardFile guardFile)
    {
        string json = JsonSerializer.Serialize(guardFile, JsonOptions);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static readonly object TraversablePropertyCacheLock = new();
    private static readonly Dictionary<Type, PropertyInfo[]> TraversablePropertyCache = [];
}

internal enum IrCoverageGroup
{
    Bound,
    Mir,
    Lir,
    Asmir,
}

internal sealed class IrCoverageGroupSpec(IrCoverageGroup group, string jsonKey, string displayName, Type baseType)
{
    public IrCoverageGroup Group { get; } = group;
    public string JsonKey { get; } = jsonKey;
    public string DisplayName { get; } = displayName;
    public Type BaseType { get; } = baseType;
}

internal sealed class IrCoverageStageAccessor(PropertyInfo property, IrCoverageGroup group)
{
    public PropertyInfo Property { get; } = property;
    public IrCoverageGroup Group { get; } = group;
}

internal sealed class IrRegressionGuardFile
{
    [JsonPropertyName("bound")]
    public IrRegressionGuardBucket Bound { get; set; } = new();

    [JsonPropertyName("mir")]
    public IrRegressionGuardBucket Mir { get; set; } = new();

    [JsonPropertyName("lir")]
    public IrRegressionGuardBucket Lir { get; set; } = new();

    [JsonPropertyName("asmir")]
    public IrRegressionGuardBucket Asmir { get; set; } = new();

    public IrRegressionGuardBucket GetBucket(IrCoverageGroup group)
    {
        return group switch
        {
            IrCoverageGroup.Bound => Bound,
            IrCoverageGroup.Mir => Mir,
            IrCoverageGroup.Lir => Lir,
            IrCoverageGroup.Asmir => Asmir,
            _ => throw new InvalidOperationException(FormattableString.Invariant($"Unknown IR coverage group '{group}'.")),
        };
    }
}

internal sealed class IrRegressionGuardBucket
{
    [JsonPropertyName("covered")]
    public List<string> Covered { get; set; } = [];

    [JsonPropertyName("uncovered")]
    public List<string> Uncovered { get; set; } = [];
}
