#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>Which packages a cleanup keeps; every rule keeps the latest version of each plugin.</summary>
internal enum CleanupRule
{
    /// <summary>Keeps the stable versions and removes the prereleases.</summary>
    StableVersions,
    LastThreeVersions,
    LatestVersion,
    /// <summary>Keeps the ZIP files changed since the start of the current year.</summary>
    ThisYear,
    LastSixMonths
}

/// <summary>What a local directory holds, from the index.</summary>
/// <param name="Versions">Different versions of all plugins together; copies of one version count once.</param>
internal sealed record LocalDirectoryStatistics(int Packages, int Plugins, int Versions, int Prereleases, long Bytes,
    DateTime? OldestUtc, DateTime? NewestUtc);

/// <summary>The packages of one plugin in a local directory.</summary>
internal sealed record LocalPluginPackages(string Slug, int Packages, int Versions, long Bytes, string Latest);

/// <summary>The packages a cleanup removes and the bytes it frees.</summary>
internal sealed record CleanupPlan(IReadOnlyList<LocalPackage> Remove, long Bytes)
{
    public static readonly CleanupPlan Empty = new([], 0);
}

/// <param name="Failures">Files that could not be deleted, or were left because they changed since they were indexed.</param>
internal sealed record CleanupResult(int Deleted, long Bytes, IReadOnlyList<LocalIndexFailure> Failures);

/// <summary>A local directory whose plugin packages take more space than the user wants, see <see cref="LocalCleanup.OverLimit"/>.</summary>
internal sealed record CleanupSuggestion(string Directory, long Bytes, long Limit);

/// <summary>
/// Cleanup of the local directories: statistics of the plugin packages the index found in a directory and which of
/// them a <see cref="CleanupRule"/> removes. Only what the index found is ever deleted. Pure apart from
/// <see cref="Delete"/>; it never looks at the disk.
/// </summary>
internal static class LocalCleanup
{
    public const long Gigabyte = 1L << 30;

    public static string Describe(CleanupRule rule) => rule switch
    {
        CleanupRule.StableVersions => "Stable versions (remove prereleases)",
        CleanupRule.LastThreeVersions => "The last three versions",
        CleanupRule.LatestVersion => "Only the latest version",
        CleanupRule.ThisYear => "Files from this year",
        CleanupRule.LastSixMonths => "Files from the last six months",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
    };

    public static readonly CleanupRule[] VersionRules = [CleanupRule.StableVersions, CleanupRule.LastThreeVersions, CleanupRule.LatestVersion];
    public static readonly CleanupRule[] DateRules = [CleanupRule.ThisYear, CleanupRule.LastSixMonths];

    public static LocalDirectoryStatistics Statistics(IReadOnlyCollection<LocalPackage> packages)
    {
        var versions = packages.DistinctBy(p => (p.Slug, Key(p.Version))).ToArray();
        return new(packages.Count, packages.Select(p => p.Slug).Distinct(StringComparer.Ordinal).Count(), versions.Length,
            versions.Count(p => Parse(p.Version)?.Prerelease is not null), packages.Sum(p => p.Length),
            packages.Count == 0 ? null : packages.Min(p => p.ModifiedUtc), packages.Count == 0 ? null : packages.Max(p => p.ModifiedUtc));
    }

    /// <summary>The packages per plugin, those taking the most space first.</summary>
    public static IReadOnlyList<LocalPluginPackages> PerPlugin(IEnumerable<LocalPackage> packages) =>
        packages.GroupBy(p => p.Slug, StringComparer.Ordinal)
            .Select(g => new LocalPluginPackages(g.Key, g.Count(), g.Select(p => Key(p.Version)).Distinct().Count(), g.Sum(p => p.Length),
                g.OrderByDescending(p => Parse(p.Version) ?? default).First().Version))
            .OrderByDescending(p => p.Bytes).ThenBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The packages <paramref name="rule"/> removes. Each plugin keeps its latest version and the versions in
    /// <paramref name="installed"/> (the project's plugins). Of several files of one version that hold the same plugin
    /// (<see cref="LocalPackage.Contents"/>) only the newest is kept, the only one updates offer; an older copy is removed
    /// whatever the rule. Files of one version that differ, such as builds for different platforms, are each kept or
    /// removed like their version, by their own date for the date rules. An archive holding more than the plugin
    /// (<see cref="LocalPackage.Bundled"/>) is never removed.
    /// </summary>
    /// <param name="installed">Slug and version of the plugins installed in the project.</param>
    public static CleanupPlan Plan(IEnumerable<LocalPackage> packages, CleanupRule rule, DateTime nowUtc,
        IReadOnlyCollection<(string Slug, string Version)>? installed = null)
    {
        var keepVersions = (installed ?? []).Select(i => (i.Slug, Key(i.Version))).ToHashSet();
        var cutoff = rule switch
        {
            CleanupRule.ThisYear => new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CleanupRule.LastSixMonths => nowUtc.AddMonths(-6),
            _ => DateTime.MinValue
        };
        var remove = new List<LocalPackage>();
        foreach (var plugin in packages.GroupBy(p => p.Slug, StringComparer.Ordinal))
        {
            // Newest version first; of copies of one version the newest file, like LocalDirectorySource offers them.
            var versions = plugin.GroupBy(p => Key(p.Version))
                .Select(g => (Version: Parse(g.First().Version), Files: g.OrderByDescending(p => p.ModifiedUtc).ToArray()))
                .OrderByDescending(v => v.Version ?? default)
                .ToArray();
            for (var i = 0; i < versions.Length; i++)
            {
                var (version, files) = versions[i];
                // a file whose plugin is not known is no copy of another
                var distinct = files.DistinctBy(p => p.Contents ?? p.ZipPath, StringComparer.Ordinal).ToArray();
                remove.AddRange(files.Except(distinct));
                // A version that cannot be compared is not the index's; keep it rather than guess.
                bool? keep = i == 0 || version is null || keepVersions.Contains((plugin.Key, version.Value.Key)) ? true : rule switch
                {
                    CleanupRule.StableVersions => version.Value.Prerelease is null,
                    CleanupRule.LastThreeVersions => i < 3,
                    CleanupRule.LatestVersion => false,
                    _ => null
                };
                remove.AddRange(distinct.Where(p => !(keep ?? p.ModifiedUtc >= cutoff)));
            }
        }
        remove.RemoveAll(p => p.Bundled);
        return new(remove, remove.Sum(p => p.Length));
    }

    /// <summary>
    /// The first listed directory whose plugin packages take more than <paramref name="limitGigabytes"/>; null when none
    /// does, or the limit is 0.
    /// </summary>
    public static CleanupSuggestion? OverLimit(LocalPackageIndex index, IEnumerable<string> directories, double limitGigabytes)
    {
        if (!(limitGigabytes > 0)) return null;
        var limit = (long)(limitGigabytes * Gigabyte);
        return directories.Select(d => new CleanupSuggestion(d, index.In(d).Sum(p => p.Length), limit))
            .FirstOrDefault(s => s.Bytes > limit);
    }

    /// <summary>
    /// Deletes the ZIP files of <paramref name="packages"/> that are inside <paramref name="directory"/>. A file whose size
    /// or modification time changed since it was indexed is left alone: it may be another package by now.
    /// </summary>
    public static CleanupResult Delete(string directory, IEnumerable<LocalPackage> packages)
    {
        var deleted = 0;
        long bytes = 0;
        var failures = new List<LocalIndexFailure>();
        foreach (var package in packages.DistinctBy(p => p.ZipPath, PackageFiles.PathComparer))
        {
            try
            {
                if (!LocalPackageIndex.IsInside(directory, package.ZipPath))
                    throw new IOException($"It is not inside {directory}.");
                var file = new FileInfo(package.ZipPath);
                if (!file.Exists) continue;
                if (file.Length != package.Length || file.LastWriteTimeUtc != package.ModifiedUtc)
                    throw new IOException("It changed since it was indexed; rescan and try again.");
                file.Delete();
                deleted++;
                bytes += package.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(new(package.ZipPath, ex.Message)); }
        }
        return new(deleted, bytes, failures);
    }

    /// <summary>A size for people: "512 KB", "1.5 MB", "12.3 GB", counting 1024 bytes as a kilobyte like the file managers on Windows.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {units[0]}"
            : (size < 10 ? size.ToString("0.#", CultureInfo.InvariantCulture) : size.ToString("0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    private static SemVer? Parse(string version) => SemVer.TryParse(version, out var parsed) ? parsed : null;
    // Differently written tags of one version ("v1.2", "1.2.0") are the same version.
    private static string Key(string version) => Parse(version)?.Key ?? version;
}
#endif
