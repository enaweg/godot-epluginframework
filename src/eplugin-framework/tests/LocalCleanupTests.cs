#if TOOLS
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>Cleanup of a local directory: its statistics, which packages each rule deletes, and deleting them.</summary>
[TestSuite]
public class LocalCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private string _root = null!;

    [BeforeTest] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "local-cleanup-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [AfterTest] public void Cleanup() { Directory.Delete(_root, true); }

    private static LocalPackage Package(string slug, string version, DateTime modified, long length = 100, string? file = null) =>
        new(Path.Combine("/packages", file ?? $"{slug}-{version}.zip"), $"addons/{slug}/", slug, version, modified, length);

    private static string[] Removed(CleanupPlan plan) => plan.Remove.Select(p => Path.GetFileName(p.ZipPath)).Order(StringComparer.Ordinal).ToArray();

    [TestCase]
    public static void VersionRulesKeepTheLatestVersionOfEveryPlugin()
    {
        LocalPackage[] packages =
        [
            Package("tool", "1.0.0", Now.AddYears(-2)), Package("tool", "1.1.0-beta.1", Now.AddYears(-1)), Package("tool", "1.1.0", Now.AddMonths(-8)),
            Package("tool", "1.2.0", Now.AddMonths(-2)), Package("tool", "2.0.0-rc.1", Now.AddDays(-3)),
            // a plugin that only ever had prereleases keeps the latest one
            Package("beta", "0.1.0-alpha", Now.AddYears(-1)), Package("beta", "0.2.0-alpha", Now.AddYears(-1))
        ];

        Assertions.AssertThat(Removed(LocalCleanup.Plan(packages, CleanupRule.StableVersions, Now)))
            .ContainsExactly("beta-0.1.0-alpha.zip", "tool-1.1.0-beta.1.zip");
        Assertions.AssertThat(Removed(LocalCleanup.Plan(packages, CleanupRule.LastThreeVersions, Now)))
            .ContainsExactly("tool-1.0.0.zip", "tool-1.1.0-beta.1.zip");
        var latest = LocalCleanup.Plan(packages, CleanupRule.LatestVersion, Now);
        Assertions.AssertThat(Removed(latest))
            .ContainsExactly("beta-0.1.0-alpha.zip", "tool-1.0.0.zip", "tool-1.1.0-beta.1.zip", "tool-1.1.0.zip", "tool-1.2.0.zip");
        Assertions.AssertInt((int)latest.Bytes).IsEqual(500);
    }

    [TestCase]
    public static void DateRulesKeepNewerFilesAndTheLatestVersion()
    {
        LocalPackage[] packages =
        [
            Package("tool", "1.0.0", new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)), Package("tool", "1.1.0", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            Package("tool", "1.2.0", Now.AddMonths(-1)),
            // a plugin without any recent release keeps its latest one
            Package("old", "1.0.0", Now.AddYears(-3)), Package("old", "1.1.0", Now.AddYears(-2))
        ];

        Assertions.AssertThat(Removed(LocalCleanup.Plan(packages, CleanupRule.ThisYear, Now))).ContainsExactly("old-1.0.0.zip", "tool-1.0.0.zip");
        Assertions.AssertThat(Removed(LocalCleanup.Plan(packages, CleanupRule.LastSixMonths, Now)))
            .ContainsExactly("old-1.0.0.zip", "tool-1.0.0.zip", "tool-1.1.0.zip");
    }

    [TestCase]
    public static void InstalledVersionsAreKeptAndOlderCopiesOfAVersionAreRemoved()
    {
        LocalPackage[] packages =
        [
            Package("tool", "1.0.0", Now.AddYears(-2)), Package("tool", "1.1.0", Now.AddYears(-1)), Package("tool", "1.2.0", Now.AddMonths(-1)),
            // copies of one version, written differently: only the newest file is offered, so the older one goes
            Package("tool", "v1.2", Now.AddMonths(-3), file: "tool-1.2.0-old.zip"),
            Package("other", "1.0.0", Now.AddYears(-1))
        ];

        var plan = LocalCleanup.Plan(packages, CleanupRule.LatestVersion, Now, [("tool", "v1.1"), ("other", "1.0.0"), ("missing", "1.0.0")]);

        Assertions.AssertThat(Removed(plan)).ContainsExactly("tool-1.0.0.zip", "tool-1.2.0-old.zip");
        Assertions.AssertThat(Removed(LocalCleanup.Plan(packages, CleanupRule.StableVersions, Now))).ContainsExactly("tool-1.2.0-old.zip");
        Assertions.AssertThat(Removed(LocalCleanup.Plan([], CleanupRule.LatestVersion, Now))).IsEmpty();
    }

    [TestCase]
    public static void StatisticsCountPackagesPluginsVersionsAndSize()
    {
        LocalPackage[] packages =
        [
            Package("tool", "1.0.0", Now.AddYears(-2), 1000), Package("tool", "v1.0", Now.AddYears(-1), 1000, "copy.zip"),
            Package("tool", "1.1.0-beta", Now.AddMonths(-1), 2000), Package("other", "3.0.0", Now, 5000)
        ];

        var statistics = LocalCleanup.Statistics(packages);

        Assertions.AssertObject(statistics).IsEqual(new LocalDirectoryStatistics(4, 2, 3, 1, 9000, Now.AddYears(-2), Now));
        Assertions.AssertObject(LocalCleanup.Statistics([])).IsEqual(new LocalDirectoryStatistics(0, 0, 0, 0, 0, null, null));
        Assertions.AssertThat(LocalCleanup.PerPlugin(packages).ToArray()).ContainsExactly(
            new LocalPluginPackages("other", 1, 1, 5000, "3.0.0"), new LocalPluginPackages("tool", 3, 2, 4000, "1.1.0-beta"));
    }

    [TestCase]
    public static void ADirectoryAboveTheLimitIsSuggestedForACleanup()
    {
        var index = new LocalPackageIndex(
        [
            new LocalPackage(Path.Combine("/a", "x.zip"), "x/", "x", "1.0.0", Now, 2 * LocalCleanup.Gigabyte),
            new LocalPackage(Path.Combine("/b", "y.zip"), "y/", "y", "1.0.0", Now, 6 * LocalCleanup.Gigabyte)
        ], [], 2, []);

        Assertions.AssertObject(LocalCleanup.OverLimit(index, ["/a", "/b"], 5))
            .IsEqual(new CleanupSuggestion("/b", 6 * LocalCleanup.Gigabyte, 5 * LocalCleanup.Gigabyte));
        Assertions.AssertObject(LocalCleanup.OverLimit(index, ["/a", "/b"], 1.5)!.Directory).IsEqual("/a");
        Assertions.AssertObject(LocalCleanup.OverLimit(index, ["/a", "/b"], 6)).IsNull();
        // 0 never suggests one
        Assertions.AssertObject(LocalCleanup.OverLimit(index, ["/a", "/b"], 0)).IsNull();
    }

    [TestCase]
    public void DeleteRemovesOnlyUnchangedPackagesInsideTheDirectory()
    {
        var directory = Path.Combine(_root, "packages");
        Zip(Path.Combine(directory, "old", "tool-1.0.zip"), "1.0.0");
        Zip(Path.Combine(directory, "changed.zip"), "1.1.0");
        Zip(Path.Combine(_root, "outside.zip"), "0.9.0");
        Zip(Path.Combine(directory, "tool-2.0.zip"), "2.0.0");
        var index = LocalPackageIndexer.Build([directory, _root], CancellationToken.None);
        Assertions.AssertThat(index.In(directory).Select(p => p.Length > 0).Distinct().ToArray()).ContainsExactly(true);
        File.AppendAllText(Path.Combine(directory, "changed.zip"), "appended");
        var gone = index.In(directory).First() with { ZipPath = Path.Combine(directory, "gone.zip") };

        var result = LocalCleanup.Delete(directory, index.Packages.Where(p => !p.ZipPath.EndsWith("tool-2.0.zip", StringComparison.Ordinal)).Append(gone));

        Assertions.AssertInt(result.Deleted).IsEqual(1);
        Assertions.AssertBool(File.Exists(Path.Combine(directory, "old", "tool-1.0.zip"))).IsFalse();
        Assertions.AssertInt((int)result.Bytes).IsEqual((int)index.Packages.Single(p => p.ZipPath.EndsWith("tool-1.0.zip", StringComparison.Ordinal)).Length);
        Assertions.AssertThat(result.Failures.Select(f => Path.GetFileName(f.Path)).Order(StringComparer.Ordinal).ToArray()).ContainsExactly("changed.zip", "outside.zip");
        Assertions.AssertBool(File.Exists(Path.Combine(directory, "changed.zip"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "outside.zip"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(directory, "tool-2.0.zip"))).IsTrue();
    }

    [TestCase]
    public static void SizesAreShortForPeople()
    {
        Assertions.AssertString(LocalCleanup.FormatSize(0)).IsEqual("0 bytes");
        Assertions.AssertString(LocalCleanup.FormatSize(1023)).IsEqual("1023 bytes");
        Assertions.AssertString(LocalCleanup.FormatSize(1536)).IsEqual("1.5 KB");
        Assertions.AssertString(LocalCleanup.FormatSize(25L << 20)).IsEqual("25 MB");
        Assertions.AssertString(LocalCleanup.FormatSize(5 * LocalCleanup.Gigabyte + (LocalCleanup.Gigabyte / 5))).IsEqual("5.2 GB");
    }

    private static void Zip(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("tool/plugin.cfg").Open());
        writer.Write($"[plugin]\nname=\"Tool\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n");
    }
}
#endif
