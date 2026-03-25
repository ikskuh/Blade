using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Blade.IR;

namespace Blade;

public static class CompilationOptionsCommandLine
{
    public static bool IsCompilationOption(string arg)
    {
        Requires.NotNull(arg);

        if (arg.StartsWith("--comptime-fuel=", StringComparison.Ordinal))
            return true;

        if (arg.StartsWith("--module=", StringComparison.Ordinal))
            return true;

        if (arg.StartsWith("-fmir-opt=", StringComparison.Ordinal) ||
            arg.StartsWith("-fno-mir-opt=", StringComparison.Ordinal) ||
            arg.StartsWith("-flir-opt=", StringComparison.Ordinal) ||
            arg.StartsWith("-fno-lir-opt=", StringComparison.Ordinal) ||
            arg.StartsWith("-fasmir-opt=", StringComparison.Ordinal) ||
            arg.StartsWith("-fno-asmir-opt=", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool TryParse(IReadOnlyList<string> args, string baseDirectory, out CompilationOptions options, out string? errorMessage)
    {
        Requires.NotNull(args);
        Requires.NotNull(baseDirectory);

        string normalizedBaseDirectory = Path.GetFullPath(baseDirectory);
        Dictionary<string, string> namedModuleRoots = new(StringComparer.Ordinal);
        int parsedComptimeFuel = 250;

        OptimizationSet<MirOptimization> mirSet = new(OptimizationRegistry.AllMirOptimizations, "mir");
        OptimizationSet<LirOptimization> lirSet = new(OptimizationRegistry.AllLirOptimizations, "lir");
        OptimizationSet<AsmOptimization> asmirSet = new(OptimizationRegistry.AllAsmOptimizations, "asmir");

        foreach (string arg in args)
        {
            Requires.NotNull(arg);

            if (TryParseComptimeFuel(arg, out int? comptimeFuelOverride, out errorMessage))
            {
                if (comptimeFuelOverride is not null)
                    parsedComptimeFuel = comptimeFuelOverride.Value;
                continue;
            }

            if (errorMessage is not null)
            {
                options = new CompilationOptions();
                return false;
            }

            if (TryGetArgumentValue(arg, "-fmir-opt=", out string? csv))
            {
                errorMessage = mirSet.ApplyDirective(csv, enable: true);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryGetArgumentValue(arg, "-fno-mir-opt=", out csv))
            {
                errorMessage = mirSet.ApplyDirective(csv, enable: false);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryGetArgumentValue(arg, "-flir-opt=", out csv))
            {
                errorMessage = lirSet.ApplyDirective(csv, enable: true);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryGetArgumentValue(arg, "-fno-lir-opt=", out csv))
            {
                errorMessage = lirSet.ApplyDirective(csv, enable: false);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryGetArgumentValue(arg, "-fasmir-opt=", out csv))
            {
                errorMessage = asmirSet.ApplyDirective(csv, enable: true);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryGetArgumentValue(arg, "-fno-asmir-opt=", out csv))
            {
                errorMessage = asmirSet.ApplyDirective(csv, enable: false);
                if (errorMessage is not null) { options = new CompilationOptions(); return false; }
                continue;
            }

            if (TryParseModuleSpecification(arg, normalizedBaseDirectory, out string? moduleName, out string? modulePath, out errorMessage))
            {
                if (string.Equals(moduleName, "builtin", StringComparison.Ordinal))
                {
                    options = new CompilationOptions();
                    errorMessage = "error: module name 'builtin' is reserved for the compiler-provided builtin module.";
                    return false;
                }

                if (namedModuleRoots.ContainsKey(moduleName!))
                {
                    options = new CompilationOptions();
                    errorMessage = $"error: duplicate module specification for '{moduleName}'.";
                    return false;
                }

                namedModuleRoots[moduleName!] = modulePath!;
                continue;
            }

            if (errorMessage is not null)
            {
                options = new CompilationOptions();
                return false;
            }

            options = new CompilationOptions();
            errorMessage = $"error: unsupported compiler option '{arg}'.";
            return false;
        }

        options = new CompilationOptions
        {
            EnabledMirOptimizations = mirSet.ToList(),
            EnabledLirOptimizations = lirSet.ToList(),
            EnabledAsmirOptimizations = asmirSet.ToList(),
            NamedModuleRoots = namedModuleRoots,
            ComptimeFuel = parsedComptimeFuel,
        };
        errorMessage = null;
        return true;
    }

    public static CompilationOptions Parse(IReadOnlyList<string> args, string baseDirectory)
    {
        if (!TryParse(args, baseDirectory, out CompilationOptions options, out string? errorMessage))
            throw new InvalidOperationException(errorMessage);

        return options;
    }

    private static bool TryGetArgumentValue(string arg, string prefix, [NotNullWhen(true)] out string? value)
    {
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseComptimeFuel(string arg, out int? fuel, out string? errorMessage)
    {
        fuel = null;
        errorMessage = null;

        if (!arg.StartsWith("--comptime-fuel=", StringComparison.Ordinal))
            return false;

        string payload = arg["--comptime-fuel=".Length..].Trim();
        if (!int.TryParse(payload, out int parsedFuel) || parsedFuel <= 0)
        {
            errorMessage = $"error: invalid comptime fuel '{payload}'. Expected a positive integer.";
            return false;
        }

        fuel = parsedFuel;
        return true;
    }

    private static bool TryParseModuleSpecification(
        string arg,
        string baseDirectory,
        out string? moduleName,
        out string? modulePath,
        out string? errorMessage)
    {
        moduleName = null;
        modulePath = null;
        errorMessage = null;

        if (!arg.StartsWith("--module=", StringComparison.Ordinal))
            return false;

        string payload = arg["--module=".Length..];
        int equalsIndex = payload.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex <= 0 || equalsIndex == payload.Length - 1)
        {
            errorMessage = $"error: invalid module specification '{arg}'. Expected --module=<name>=<path>.";
            return false;
        }

        string name = payload[..equalsIndex].Trim();
        string pathText = payload[(equalsIndex + 1)..].Trim();
        if (name.Length == 0 || pathText.Length == 0)
        {
            errorMessage = $"error: invalid module specification '{arg}'. Expected --module=<name>=<path>.";
            return false;
        }

        moduleName = name;
        modulePath = Path.GetFullPath(pathText, baseDirectory);
        return true;
    }

    private sealed class OptimizationSet<T> where T : Optimization
    {
        private readonly Dictionary<string, T> _byName;
        private readonly HashSet<T> _enabled;
        private readonly string _tierName;

        public OptimizationSet(IReadOnlyList<T> all, string tierName)
        {
            _byName = new(StringComparer.Ordinal);
            foreach (T item in all)
                _byName[item.Name] = item;
            _enabled = new(all);
            _tierName = tierName;
        }

        public string? ApplyDirective(string csv, bool enable)
        {
            string[] names = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0)
                return $"error: missing optimization list in '{csv}'";

            foreach (string name in names)
            {
                if (name == "*")
                {
                    if (enable)
                        _enabled.UnionWith(_byName.Values);
                    else
                        _enabled.Clear();
                    continue;
                }

                if (!_byName.TryGetValue(name, out T? instance))
                {
                    string known = string.Join(", ", SortedKnownNames());
                    return $"error: unknown {_tierName} optimization '{name}'. Known options: {known}";
                }

                if (enable)
                    _enabled.Add(instance);
                else
                    _enabled.Remove(instance);
            }

            return null;
        }

        public IReadOnlyList<T> ToList() => [.. _enabled];

        private IEnumerable<string> SortedKnownNames()
        {
            List<string> names = [.. _byName.Keys];
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
