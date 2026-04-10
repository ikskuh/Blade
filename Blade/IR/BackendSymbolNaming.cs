using System;
using System.Collections.Generic;
using System.Text;
using Blade.Semantics;

namespace Blade.IR;

internal static class BackendSymbolNaming
{
    /// <summary>
    /// Assigns final assembly-visible identifiers to backend symbols.
    /// This is an emission concern only and must not be used as semantic or IR identity.
    /// </summary>
    public static void AssignStorageNames(IReadOnlyList<StoragePlace> places)
    {
        Requires.NotNull(places);

        Dictionary<string, int> emittedNameCounts = new(StringComparer.Ordinal);
        foreach (StoragePlace place in places)
        {
            if (place.HasAssignedEmittedName)
            {
                Track(emittedNameCounts, place.EmittedName);
                continue;
            }

            string baseName = GetBaseName(place);
            string assignedName = AllocateUniqueName(emittedNameCounts, baseName);
            place.AssignEmittedName(assignedName);
        }
    }

    /// <summary>
    /// Converts arbitrary source-facing names into legal assembly identifiers.
    /// This is a final naming step only and must not be used as compiler identity.
    /// </summary>
    public static string SanitizeIdentifier(string name)
    {
        Requires.NotNull(name);

        StringBuilder builder = new();
        if (name.Length == 0)
            return "l_";

        char first = name[0];
        if (char.IsLetter(first) || first == '_')
        {
            builder.Append(first);
        }
        else
        {
            builder.Append('l');
            builder.Append(char.IsLetterOrDigit(first) || first == '_' ? first : '_');
        }

        for (int i = 1; i < name.Length; i++)
        {
            char ch = name[i];
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static string GetBaseName(StoragePlace place)
    {
        return place switch
        {
            { RegisterRole: StoragePlaceRegisterRole.InternalShared or StoragePlaceRegisterRole.InternalDedicated } => $"abi_{SanitizeIdentifier(place.Symbol.Name)}",
            { Placement: StoragePlacePlacement.FixedAlias or StoragePlacePlacement.ExternalAlias } => place.Symbol.Name,
            _ => $"g_{SanitizeIdentifier(place.Symbol.Name)}",
        };
    }

    private static string AllocateUniqueName(IDictionary<string, int> emittedNameCounts, string baseName)
    {
        if (emittedNameCounts.TryGetValue(baseName, out int seenCount))
        {
            int nextCount = seenCount + 1;
            emittedNameCounts[baseName] = nextCount;
            return $"{baseName}_{nextCount}";
        }

        emittedNameCounts.Add(baseName, 1);
        return baseName;
    }

    private static void Track(IDictionary<string, int> emittedNameCounts, string emittedName)
    {
        if (emittedNameCounts.TryGetValue(emittedName, out int seenCount))
            emittedNameCounts[emittedName] = seenCount + 1;
        else
            emittedNameCounts.Add(emittedName, 1);
    }
}
