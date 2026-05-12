using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Blade.Regressions;

/// <summary>Represents the resolved regression suite configuration for a run.</summary>
internal sealed class RegressionSuiteConfiguration(
    string repositoryRootPath,
    string configPath,
    IReadOnlyList<RegressionPoolConfiguration> pools,
    string? hardwareRuntimePath,
    string? irCoverageGuardPath)
{
    /// <summary>Gets the repository root used to resolve suite-relative paths.</summary>
    public string RepositoryRootPath { get; } = repositoryRootPath;

    /// <summary>Gets the absolute path of the loaded configuration file.</summary>
    public string ConfigPath { get; } = configPath;

    /// <summary>Gets the configured regression pools that supply fixture files.</summary>
    public IReadOnlyList<RegressionPoolConfiguration> Pools { get; } = pools;

    /// <summary>Gets the optional hardware runtime fixture used by hardware tests.</summary>
    public string? HardwareRuntimePath { get; } = hardwareRuntimePath;

    /// <summary>Gets the optional IR coverage guard file path.</summary>
    public string? IrCoverageGuardPath { get; } = irCoverageGuardPath;
}

/// <summary>Loads and validates the regression suite configuration file.</summary>
internal static class RegressionConfigurationLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Loads the regression suite configuration for the supplied run options.</summary>
    public static RegressionSuiteConfiguration Load(RegressionRunOptions options)
    {
        string repositoryRootPath = RepositoryLayout.FindRepositoryRoot(options.RepositoryRootPath, options.ConfigPath);
        string configPath = RepositoryLayout.FindConfigurationPath(repositoryRootPath, options.ConfigPath);
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Regression config file was not found: {configPath}");

        byte[] jsonBytes = File.ReadAllBytes(configPath);
        ReadOnlyMemory<byte> jsonMemory = jsonBytes;
        if (jsonBytes.Length >= 3
            && jsonBytes[0] == 0xEF
            && jsonBytes[1] == 0xBB
            && jsonBytes[2] == 0xBF)
        {
            jsonMemory = jsonBytes.AsMemory(3);
        }

        using JsonDocument document = JsonDocument.Parse(jsonMemory, JsonOptions);

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Regression config root must be a JSON object.");

        string configDirectoryPath = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Regression config path has no parent directory.");

        List<RegressionPoolConfiguration> pools = LoadPools(root, configDirectoryPath, repositoryRootPath);
        string? hardwareRuntimePath = LoadOptionalFilePath(root, "hardwareRuntimePath", configDirectoryPath);
        string? irCoverageGuardPath = LoadOptionalFilePath(root, "irCoverageGuardPath", configDirectoryPath);

        return new RegressionSuiteConfiguration(
            repositoryRootPath,
            configPath,
            pools,
            hardwareRuntimePath,
            irCoverageGuardPath);
    }

    private static List<RegressionPoolConfiguration> LoadPools(
        JsonElement root,
        string configDirectoryPath,
        string repositoryRootPath)
    {
        if (!root.TryGetProperty("pools", out JsonElement poolsElement))
            throw new InvalidOperationException("Regression config is missing required property 'pools'.");
        if (poolsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Regression config property 'pools' must be an array.");

        List<RegressionPoolConfiguration> pools = [];
        HashSet<string> seenPaths = new(PathComparer.Instance);
        int index = 0;
        foreach (JsonElement poolElement in poolsElement.EnumerateArray())
        {
            if (poolElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Regression pool at index {index} must be an object."));
            }

            string path = ReadRequiredString(poolElement, "path", index);
            string expect = ReadRequiredString(poolElement, "expect", index);
            string absolutePath = ResolveDirectoryPath(path, configDirectoryPath, index);
            if (!seenPaths.Add(absolutePath))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Regression pool path '{path}' is duplicated in regressions.cfg.json."));
            }

            pools.Add(new RegressionPoolConfiguration(
                absolutePath,
                Path.GetRelativePath(repositoryRootPath, absolutePath).Replace('\\', '/'),
                ParsePoolExpectation(expect, index)));
            index++;
        }

        return pools;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, int index)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} is missing required property '{propertyName}'."));
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} property '{propertyName}' must be a non-empty string."));
        }

        return property.GetString()!;
    }

    private static RegressionPoolExpectation ParsePoolExpectation(string expect, int index)
    {
        return expect switch
        {
            "accept" => RegressionPoolExpectation.Accept,
            "reject" => RegressionPoolExpectation.Reject,
            "encoded" => RegressionPoolExpectation.Encoded,
            _ => throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} has unsupported expect value '{expect}'.")),
        };
    }

    private static string ResolveDirectoryPath(string path, string configDirectoryPath, int index)
    {
        string absolutePath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(configDirectoryPath, path));

        if (!Directory.Exists(absolutePath))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression pool at index {index} points to a missing directory: {path}"));
        }

        return absolutePath;
    }

    private static string? LoadOptionalFilePath(JsonElement root, string propertyName, string configDirectoryPath)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property))
            return null;

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression config property '{propertyName}' must be a non-empty string when present."));
        }

        string configuredPath = property.GetString()!;
        string absolutePath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(configDirectoryPath, configuredPath));

        if (!File.Exists(absolutePath))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Regression config property '{propertyName}' points to a missing file: {configuredPath}"));
        }

        return absolutePath;
    }
}

/// <summary>Resolves repository and configuration paths for regression runs.</summary>
internal static class RepositoryLayout
{
    private const string DefaultConfigFileName = "regressions.cfg.json";

    /// <summary>Finds the repository root from explicit options or config discovery.</summary>
    public static string FindRepositoryRoot(string? explicitRootPath, string? explicitConfigPath)
    {
        if (explicitRootPath is not null)
            return Path.GetFullPath(explicitRootPath);

        if (explicitConfigPath is not null)
        {
            string configPath = Path.GetFullPath(explicitConfigPath);
            return Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Configured regression config path has no parent directory.");
        }

        string configPathFromSearch = FindDefaultConfigurationPath();
        return Path.GetDirectoryName(configPathFromSearch)
            ?? throw new InvalidOperationException("Located regression config path has no parent directory.");
    }

    /// <summary>Finds the effective configuration file path for a regression run.</summary>
    public static string FindConfigurationPath(string repositoryRootPath, string? explicitConfigPath)
    {
        return explicitConfigPath is not null
            ? Path.GetFullPath(explicitConfigPath)
            : Path.Combine(repositoryRootPath, DefaultConfigFileName);
    }

    private static string FindDefaultConfigurationPath()
    {
        string[] candidates =
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        ];

        foreach (string candidate in candidates)
        {
            string? current = Path.GetFullPath(candidate);
            while (current is not null)
            {
                string configPath = Path.Combine(current, DefaultConfigFileName);
                if (File.Exists(configPath))
                    return configPath;

                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate regressions.cfg.json.");
    }
}

/// <summary>Compares filesystem paths using the platform's path-casing behavior.</summary>
internal sealed class PathComparer : IEqualityComparer<string>
{
    /// <summary>Gets the shared path comparer instance.</summary>
    public static PathComparer Instance { get; } = new();

    private readonly StringComparer comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private PathComparer()
    {
    }

    /// <summary>Determines whether two filesystem paths should be treated as equal.</summary>
    public bool Equals(string? x, string? y)
    {
        return comparer.Equals(x, y);
    }

    /// <summary>Gets a hash code that matches the platform-specific equality behavior.</summary>
    public int GetHashCode(string obj)
    {
        return comparer.GetHashCode(obj);
    }
}
