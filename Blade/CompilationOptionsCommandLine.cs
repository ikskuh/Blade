using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Blade.IR;

namespace Blade;

public static class CompilationOptionsCommandLine
{
    private enum OptimizationTier
    {
        Mir,
        Lir,
        Asmir,
    }

    private static readonly OptimizationOptionDescriptor[] OptimizationOptionDescriptors =
    [
        new("-fmir-opt=", OptimizationTier.Mir, Enable: true),
        new("-fno-mir-opt=", OptimizationTier.Mir, Enable: false),
        new("-flir-opt=", OptimizationTier.Lir, Enable: true),
        new("-fno-lir-opt=", OptimizationTier.Lir, Enable: false),
        new("-fasmir-opt=", OptimizationTier.Asmir, Enable: true),
        new("-fno-asmir-opt=", OptimizationTier.Asmir, Enable: false),
    ];

    public static bool IsCompilationOption(string arg)
    {
        Requires.NotNull(arg);

        if (arg.StartsWith("--comptime-fuel=", StringComparison.Ordinal))
            return true;

        if (arg.StartsWith("--module=", StringComparison.Ordinal))
            return true;

        foreach (OptimizationOptionDescriptor descriptor in OptimizationOptionDescriptors)
        {
            if (arg.StartsWith(descriptor.Prefix, StringComparison.Ordinal))
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

        // Track enabled optimizations per tier by name. Start with all enabled.
        HashSet<string> mirEnabled = new(OptimizationRegistry.AllMirNames, StringComparer.Ordinal);
        HashSet<string> lirEnabled = new(OptimizationRegistry.AllLirNames, StringComparer.Ordinal);
        HashSet<string> asmirEnabled = new(OptimizationRegistry.AllAsmNames, StringComparer.Ordinal);

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

            if (TryParseOptimizationDirective(arg, mirEnabled, lirEnabled, asmirEnabled, out errorMessage))
                continue;

            if (errorMessage is not null)
            {
                options = new CompilationOptions();
                return false;
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
            EnabledMirOptimizations = OptimizationRegistry.ResolveMirOptimizations(mirEnabled),
            EnabledLirOptimizations = OptimizationRegistry.ResolveLirOptimizations(lirEnabled),
            EnabledAsmirOptimizations = OptimizationRegistry.ResolveAsmOptimizations(asmirEnabled),
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

    private static bool TryParseOptimizationDirective(
        string arg,
        HashSet<string> mirEnabled,
        HashSet<string> lirEnabled,
        HashSet<string> asmirEnabled,
        out string? errorMessage)
    {
        foreach (OptimizationOptionDescriptor descriptor in OptimizationOptionDescriptors)
        {
            if (arg.StartsWith(descriptor.Prefix, StringComparison.Ordinal))
            {
                HashSet<string> enabledSet = descriptor.Tier switch
                {
                    OptimizationTier.Mir => mirEnabled,
                    OptimizationTier.Lir => lirEnabled,
                    OptimizationTier.Asmir => asmirEnabled,
                    _ => throw new UnreachableException(),
                };
                return TryApplyOptimizationDirective(arg, descriptor, enabledSet, out errorMessage);
            }
        }

        errorMessage = null;
        return false;
    }

    private static bool TryApplyOptimizationDirective(
        string arg,
        OptimizationOptionDescriptor descriptor,
        HashSet<string> enabledSet,
        out string? errorMessage)
    {
        errorMessage = null;

        int equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex < 0 || equalsIndex == arg.Length - 1)
        {
            errorMessage = $"error: missing optimization list in '{arg}'";
            return false;
        }

        string csv = arg[(equalsIndex + 1)..];
        string[] rawNames = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawNames.Length == 0)
        {
            errorMessage = $"error: missing optimization list in '{arg}'";
            return false;
        }

        IReadOnlyList<string> allNames = GetAllNames(descriptor.Tier);

        foreach (string name in rawNames)
        {
            if (name == "*")
            {
                if (descriptor.Enable)
                {
                    foreach (string opt in allNames)
                        enabledSet.Add(opt);
                }
                else
                {
                    enabledSet.Clear();
                }

                continue;
            }

            if (!IsKnown(descriptor.Tier, name))
            {
                errorMessage = $"error: unknown {GetTierDisplayName(descriptor.Tier)} optimization '{name}'";
                return false;
            }

            if (descriptor.Enable)
                enabledSet.Add(name);
            else
                enabledSet.Remove(name);
        }

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

    private static bool IsKnown(OptimizationTier tier, string name)
    {
        return tier switch
        {
            OptimizationTier.Mir => OptimizationRegistry.GetMirOptimization(name) is not null,
            OptimizationTier.Lir => OptimizationRegistry.GetLirOptimization(name) is not null,
            OptimizationTier.Asmir => OptimizationRegistry.GetAsmOptimization(name) is not null,
            _ => throw new UnreachableException(),
        };
    }

    private static IReadOnlyList<string> GetAllNames(OptimizationTier tier)
    {
        return tier switch
        {
            OptimizationTier.Mir => OptimizationRegistry.AllMirNames,
            OptimizationTier.Lir => OptimizationRegistry.AllLirNames,
            OptimizationTier.Asmir => OptimizationRegistry.AllAsmNames,
            _ => throw new UnreachableException(),
        };
    }

    private static string GetTierDisplayName(OptimizationTier tier)
    {
        return tier switch
        {
            OptimizationTier.Mir => "mir",
            OptimizationTier.Lir => "lir",
            OptimizationTier.Asmir => "asmir",
            _ => throw new UnreachableException(),
        };
    }

    private readonly record struct OptimizationOptionDescriptor(string Prefix, OptimizationTier Tier, bool Enable);
}
