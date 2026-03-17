using System;
using System.Collections.Generic;
using System.IO;
using Blade.IR;

namespace Blade;

public static class CompilationOptionsCommandLine
{
    private static readonly OptimizationOptionDescriptor[] OptimizationOptionDescriptors =
    [
        new OptimizationOptionDescriptor("-fmir-opt=", OptimizationStage.Mir, Enable: true),
        new OptimizationOptionDescriptor("-fno-mir-opt=", OptimizationStage.Mir, Enable: false),
        new OptimizationOptionDescriptor("-flir-opt=", OptimizationStage.Lir, Enable: true),
        new OptimizationOptionDescriptor("-fno-lir-opt=", OptimizationStage.Lir, Enable: false),
        new OptimizationOptionDescriptor("-fasmir-opt=", OptimizationStage.Asmir, Enable: true),
        new OptimizationOptionDescriptor("-fno-asmir-opt=", OptimizationStage.Asmir, Enable: false),
    ];

    public static bool IsCompilationOption(string arg)
    {
        Requires.NotNull(arg);

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
        List<OptimizationDirective> optimizationDirectives = [];
        Dictionary<string, string> namedModuleRoots = new(StringComparer.Ordinal);

        foreach (string arg in args)
        {
            Requires.NotNull(arg);

            if (TryParseOptimizationDirective(arg, out OptimizationDirective directive, out errorMessage))
            {
                optimizationDirectives.Add(directive);
                continue;
            }

            if (errorMessage is not null)
            {
                options = new CompilationOptions();
                return false;
            }

            if (TryParseModuleSpecification(arg, normalizedBaseDirectory, out string? moduleName, out string? modulePath, out errorMessage))
            {
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
            OptimizationDirectives = optimizationDirectives,
            NamedModuleRoots = namedModuleRoots,
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

    private static bool TryParseOptimizationDirective(string arg, out OptimizationDirective directive, out string? errorMessage)
    {
        foreach (OptimizationOptionDescriptor descriptor in OptimizationOptionDescriptors)
        {
            if (arg.StartsWith(descriptor.Prefix, StringComparison.Ordinal))
                return TryParseOptimizationDirective(arg, descriptor, out directive, out errorMessage);
        }

        directive = default;
        errorMessage = null;
        return false;
    }

    private static bool TryParseOptimizationDirective(
        string arg,
        OptimizationOptionDescriptor descriptor,
        out OptimizationDirective directive,
        out string? errorMessage)
    {
        directive = default;
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

        List<string> names = new(rawNames.Length);
        foreach (string name in rawNames)
        {
            if (name == "*")
            {
                names.Add(name);
                continue;
            }

            if (!OptimizationCatalog.IsKnown(descriptor.Stage, name))
            {
                errorMessage = $"error: unknown {GetStageDisplayName(descriptor.Stage)} optimization '{name}'";
                return false;
            }

            names.Add(name);
        }

        directive = new OptimizationDirective(descriptor.Stage, descriptor.Enable, names);
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

    private static string GetStageDisplayName(OptimizationStage stage)
    {
        return stage switch
        {
            OptimizationStage.Mir => "mir",
            OptimizationStage.Lir => "lir",
            OptimizationStage.Asmir => "asmir",
            _ => throw new InvalidOperationException($"Unknown optimization stage '{stage}'."),
        };
    }

    private readonly record struct OptimizationOptionDescriptor(string Prefix, OptimizationStage Stage, bool Enable);
}
