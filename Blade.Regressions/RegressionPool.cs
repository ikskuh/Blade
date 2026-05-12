using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Blade.Regressions;

/// <summary>Identifies the default expectation assigned by a configured regression pool.</summary>
internal enum RegressionPoolExpectation
{
    Accept,
    Reject,
    Encoded,
}

/// <summary>Describes one configured directory of regression fixtures.</summary>
internal sealed class RegressionPoolConfiguration(string absolutePath, string relativePath, RegressionPoolExpectation expectation)
{
    /// <summary>Gets the absolute pool directory path.</summary>
    public string AbsolutePath { get; } = absolutePath;

    /// <summary>Gets the repository-relative pool directory path.</summary>
    public string RelativePath { get; } = relativePath;

    /// <summary>Gets the expectation applied to fixtures discovered in the pool.</summary>
    public RegressionPoolExpectation Expectation { get; } = expectation;
}

/// <summary>Represents a fixture file discovered from the configured regression pools.</summary>
internal sealed class DiscoveredRegressionFixture(string absolutePath, string relativePath, RegressionPoolExpectation poolExpectation)
{
    /// <summary>Gets the absolute path of the discovered fixture file.</summary>
    public string AbsolutePath { get; } = absolutePath;

    /// <summary>Gets the repository-relative path of the discovered fixture file.</summary>
    public string RelativePath { get; } = relativePath;

    /// <summary>Gets the default expectation inherited from the containing pool.</summary>
    public RegressionPoolExpectation PoolExpectation { get; } = poolExpectation;
}

/// <summary>Discovers regression fixtures from configured pools and optional path filters.</summary>
internal static class RegressionPool
{
    /// <summary>Discovers and orders the fixture set for a regression run.</summary>
    public static List<DiscoveredRegressionFixture> DiscoverFixtures(
        RegressionSuiteConfiguration configuration,
        IReadOnlyList<string> filters)
    {
        Dictionary<string, DiscoveredRegressionFixture> fixturesByPath = new(PathComparer.Instance);
        foreach (RegressionPoolConfiguration pool in configuration.Pools)
        {
            AddFixturePaths(fixturesByPath, configuration.RepositoryRootPath, pool, "*.blade");
            AddFixturePaths(fixturesByPath, configuration.RepositoryRootPath, pool, "*.blade.crash");
        }

        IEnumerable<DiscoveredRegressionFixture> filteredPaths = fixturesByPath.Values;
        if (filters.Count > 0)
            filteredPaths = filteredPaths.Where(path => MatchesAnyFilter(path.RelativePath, filters));

        return filteredPaths
            .OrderBy(path => path.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static bool MatchesAnyFilter(string relativePath, IReadOnlyList<string> filters)
    {
        foreach (string filter in filters)
        {
            if (MatchesFilter(relativePath, filter))
                return true;
        }

        return false;
    }

    private static bool MatchesFilter(string relativePath, string filter)
    {
        string normalizedFilter = NormalizeRelativePath(filter);
        if (normalizedFilter.Length == 0)
            return false;

        if (!IsGlobFilter(normalizedFilter))
            return PathComparer.Instance.Equals(relativePath, normalizedFilter);

        Regex pattern = CreateGlobRegex(normalizedFilter);
        return pattern.IsMatch(relativePath);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool IsGlobFilter(string filter)
    {
        return filter.Contains('*', StringComparison.Ordinal)
            || filter.Contains('?', StringComparison.Ordinal);
    }

    private static Regex CreateGlobRegex(string filter)
    {
        StringBuilder builder = new();
        builder.Append('^');
        for (int index = 0; index < filter.Length; index++)
        {
            char ch = filter[index];
            switch (ch)
            {
                case '*':
                    if (index + 1 < filter.Length && filter[index + 1] == '*')
                    {
                        builder.Append(".*");
                        index++;
                    }
                    else
                    {
                        builder.Append(@"[^/]*");
                    }

                    break;

                case '?':
                    builder.Append(@"[^/]");
                    break;

                case '/':
                    builder.Append('/');
                    break;

                default:
                    builder.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        builder.Append('$');
        RegexOptions options = OperatingSystem.IsWindows()
            ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            : RegexOptions.CultureInvariant;
        return new Regex(builder.ToString(), options);
    }

    private static void AddFixturePaths(
        Dictionary<string, DiscoveredRegressionFixture> fixturesByPath,
        string repositoryRootPath,
        RegressionPoolConfiguration pool,
        string searchPattern)
    {
        string[] discovered = Directory.GetFiles(pool.AbsolutePath, searchPattern, SearchOption.AllDirectories);
        foreach (string fixturePath in discovered)
        {
            string absolutePath = Path.GetFullPath(fixturePath);
            string relativePath = Path.GetRelativePath(repositoryRootPath, absolutePath).Replace('\\', '/');
            DiscoveredRegressionFixture fixture = new(absolutePath, relativePath, pool.Expectation);
            if (!fixturesByPath.TryAdd(absolutePath, fixture))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Fixture '{relativePath}' was discovered more than once. Check for overlapping regression pools."));
            }
        }
    }
}
