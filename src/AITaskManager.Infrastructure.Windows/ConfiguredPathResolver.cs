using System.Text.RegularExpressions;
using AITaskManager.Application;

namespace AITaskManager.Infrastructure.Windows;

public sealed record ResolvedConfiguredPaths(
    string DataDirectory,
    string LogDirectory,
    IReadOnlyList<string> ModelRoots,
    IReadOnlyList<string> TrainingRoots,
    IReadOnlyList<string> DatasetRoots);

public static class ConfiguredPathResolver
{
    private static readonly Regex UnresolvedVariablePattern = new(
        @"%[^%]+%",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public static ResolvedConfiguredPaths Resolve(AITaskManagerOptions options, AppPaths defaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);

        var dataDirectory = ResolveDirectory(
            options.Storage.DataDirectory,
            defaults.DataDirectory,
            "Storage:DataDirectory");

        var logDirectory = ResolveDirectory(
            options.Storage.LogDirectory,
            defaults.LogDirectory,
            "Storage:LogDirectory");

        return new ResolvedConfiguredPaths(
            dataDirectory,
            logDirectory,
            ResolveRoots(options.Discovery.ModelRoots, "Discovery:ModelRoots"),
            ResolveRoots(options.Discovery.TrainingRoots, "Discovery:TrainingRoots"),
            ResolveRoots(options.Discovery.DatasetRoots, "Discovery:DatasetRoots"));
    }

    public static string ResolveDirectory(string configuredValue, string defaultValue, string configurationPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue.Trim();
        var expanded = Environment.ExpandEnvironmentVariables(candidate);

        if (UnresolvedVariablePattern.IsMatch(expanded))
        {
            throw new ConfigurationValidationException([
                new(configurationPath, "The path contains an unresolved environment variable."),
            ]);
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new ConfigurationValidationException([
                new(configurationPath, "Use an absolute path or an environment variable that expands to one."),
            ]);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static IReadOnlyList<string> ResolveRoots(IReadOnlyList<string> roots, string configurationPath)
    {
        var resolved = new string[roots.Count];
        for (var index = 0; index < roots.Count; index++)
        {
            resolved[index] = ResolveDirectory(roots[index], roots[index], $"{configurationPath}:{index}");
        }

        return resolved;
    }
}
