#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateCoreTests
{
    [TestCase]
    public static void SemVerParsingAndPrecedence()
    {
        var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "v1.0.0+build", "1.0.0.1-rc", "1.0.0.1", "1.0.0.12", "1.1", "2.0.0" };
        for (var i = 0; i < ordered.Length; i++)
        {
            Assertions.AssertBool(SemVer.TryParse(ordered[i], out var current)).IsTrue();
            if (i > 0)
            {
                Assertions.AssertBool(SemVer.TryParse(ordered[i - 1], out var previous)).IsTrue();
                Assertions.AssertBool(current.CompareTo(previous) > 0).IsTrue();
            }
        }
        foreach (var invalid in new[] { "1", "01.0", "1.0.0.0.0", "1.0.0.01", "1.0.0-", "1.0.0+", "1.0.0-a b", "1.0 beta", "garbage", "1.0.0\nextra" })
            Assertions.AssertBool(SemVer.TryParse(invalid, out _)).IsFalse();
        // Any suffix after '-' is a prerelease of its numbers, and versions are shown as they are written.
        var loose = new[] { "0.1.2-b1", "0.1.2-beta.1", "0.1.2", "v0.2-anystringhere", "0.2", "0.2.0.1-01_x/y", "0.2.0.1" };
        for (var i = 0; i < loose.Length; i++)
        {
            Assertions.AssertBool(SemVer.TryParse(loose[i], out var current)).IsTrue();
            Assertions.AssertString(current.ToString()).IsEqual(loose[i]);
            if (i == 0) continue;
            Assertions.AssertBool(SemVer.TryParse(loose[i - 1], out var previous)).IsTrue();
            Assertions.AssertBool(current.CompareTo(previous) > 0).IsTrue();
        }
        Assertions.AssertBool(SemVer.TryParse(" v0.11.0.3 ", out var four)).IsTrue();
        Assertions.AssertString(four.ToString()).IsEqual("v0.11.0.3");
        Assertions.AssertString(four.Key).IsEqual("0.11.0.3");
        Assertions.AssertBool(SemVer.TryParse("v1.2+build", out var twoNumbers)).IsTrue();
        Assertions.AssertBool(SemVer.TryParse("1.2.3.0", out var zero)).IsTrue();
        Assertions.AssertString(twoNumbers.Key).IsEqual("1.2.0");
        Assertions.AssertString(zero.Key).IsEqual("1.2.3");
        Assertions.AssertInt(zero.CompareTo(new SemVer(1, 2, 3))).IsEqual(0);
    }

    [TestCase]
    public static void PluginIniReadsGodotStrings()
    {
        var values = PluginIni.Parse("[plugin]\r\n\r\nname=\"Edit Resources as Spreadsheet\"\r\n" +
            "description=\"Edit Many Resources from one Folder as a table.\r\n[not a section] key=\\\"quoted\\\"\r\n\ttab\\u00e9\\'\"\r\n" +
            "author=\"Don Tnowe\" ; comment\r\nversion=\"3.4.0\"\r\n\r\n[other]\r\nnote=\"line\nline\"\r\n");
        Assertions.AssertString(values["name"]).IsEqual("Edit Resources as Spreadsheet");
        Assertions.AssertString(values["description"]).IsEqual("Edit Many Resources from one Folder as a table.\r\n[not a section] key=\"quoted\"\r\n\ttab\u00e9'");
        Assertions.AssertString(values["author"]).IsEqual("Don Tnowe");
        Assertions.AssertString(values["version"]).IsEqual("3.4.0");
        Assertions.AssertBool(values.ContainsKey("note")).IsFalse();
        foreach (var invalid in new[] { "[plugin]\nversion=\"1.0", "[plugin]\nversion=\"1.0\" + \"1\"", "[plugin]\nversion=\"\\u12\"" })
            Assertions.AssertThrown(() => PluginIni.Parse(invalid)).IsInstanceOf<InvalidDataException>();
    }

    [TestCase]
    public static void PluginIniReadsEscapesBareValuesAndIgnoresOtherSections()
    {
        var values = PluginIni.Parse("[editor]\nnot an assignment\n[plugin]\n" +
            "description=\"a\\bb\\tc\\nd\\fe\\rf\\U01F600\"\nversion = 1.2.0 ; bare, with a comment\n# comment\n");
        Assertions.AssertString(values["description"]).IsEqual("a\bb\tc\nd\fe\rf\U0001F600");
        Assertions.AssertString(values["version"]).IsEqual("1.2.0");
        foreach (var invalid in new[] { "[plugin]\nnot an assignment", "[plugin]\nname=\"a\"\nname=\"b\"", "[plugin]\nname=\"\\U110000\"" })
            Assertions.AssertThrown(() => PluginIni.Parse(invalid)).IsInstanceOf<InvalidDataException>();
    }

    [TestCase]
    public static void ScheduleBoundaryAndCacheUseInstalledVersion()
    {
        var now = DateTimeOffset.UtcNow;
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, now.AddHours(-20), true)).IsTrue();
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, now.AddHours(-19), true)).IsFalse();
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, null, false)).IsFalse();
        Assertions.AssertObject(UpdateScheduler.NextCheck(now, true, 5)).IsEqual(now.AddHours(5));
        Assertions.AssertObject(UpdateScheduler.NextCheck(now, true, 0)).IsEqual(now.AddHours(1));
        Assertions.AssertObject(UpdateScheduler.NextCheck(null, true)).IsEqual(DateTimeOffset.MinValue);
        Assertions.AssertObject(UpdateScheduler.NextCheck(now, false)).IsNull();
        var cache = new UpdateCache { Results = [Candidate("plugin", "2.0.0")] };
        Assertions.AssertInt(UpdateScheduler.CurrentCached(cache, [Target("plugin", "3.0.0")]).Count).IsEqual(0);
        Assertions.AssertInt(UpdateScheduler.CurrentCached(cache, [Target("plugin", "1.0.0")]).Count).IsEqual(1);
        // A prerelease is an update of a prerelease only; a stable install gets it from the version list.
        var beta = new UpdateCache { Results = [Candidate("plugin", "2.0.0-beta.1")] };
        Assertions.AssertInt(UpdateScheduler.CurrentCached(beta, [Target("plugin", "1.0.0")]).Count).IsEqual(0);
        Assertions.AssertInt(UpdateScheduler.CurrentCached(beta, [Target("plugin", "1.0.0-beta.3")]).Count).IsEqual(1);
        Assertions.AssertInt(UpdateScheduler.CurrentCached(beta, [Target("plugin", "2.0.0-beta.1")]).Count).IsEqual(0);
    }

    [TestCase]
    public static async Task FailingSourceDoesNotStopOthersAndOfflineDoesNotAdvanceTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateStateStore(Path.Combine(directory, "state.json"));
            var service = new UpdateService(new Factory(), new SystemClock(), store);
            var offline = await service.CheckAsync([Target("broken")], new(), CancellationToken.None);
            Assertions.AssertInt(offline.Failures.Count).IsEqual(1);
            Assertions.AssertObject(store.State.LastCheckUtc).IsNull();
            var mixed = await service.CheckAsync([Target("broken"), Target("healthy")], new(), CancellationToken.None);
            Assertions.AssertInt(mixed.Updates.Count).IsEqual(1);
            Assertions.AssertInt(mixed.Failures.Count).IsEqual(1);
            var loaded = new UpdateStateStore(Path.Combine(directory, "state.json"));
            loaded.Load();
            Assertions.AssertString(loaded.State.Results[0].Slug).IsEqual("healthy");
            File.WriteAllText(Path.Combine(directory, "state.json"), "broken json");
            loaded.Load();
            Assertions.AssertObject(loaded.State.LastCheckUtc).IsNull();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase]
    public static async Task APluginWhoseSiteFailsKeepsTheUpdateAnEarlierCheckFound()
    {
        var store = new MemoryStore();
        store.State.Results.Add(Candidate("broken", "2.0.0"));
        var service = new UpdateService(new Factory(), new SystemClock(), store);

        var mixed = await service.CheckAsync([Target("broken"), Target("healthy")], new(), CancellationToken.None);
        Assertions.AssertThat(mixed.Updates.Select(u => u.Slug).ToArray()).ContainsExactly("broken", "healthy");
        Assertions.AssertThat(store.State.Results.Select(u => u.Slug).ToArray()).ContainsExactlyInAnyOrder("broken", "healthy");

        var offline = await service.CheckAsync([Target("broken")], new(), CancellationToken.None);
        Assertions.AssertThat(offline.Updates.Select(u => u.Slug).ToArray()).ContainsExactly("broken");
        // once the update is installed, the kept entry is no update any more
        var installed = await service.CheckAsync([Target("broken", "2.0.0"), Target("healthy")], new(), CancellationToken.None);
        Assertions.AssertThat(installed.Updates.Select(u => u.Slug).ToArray()).ContainsExactly("healthy");
    }

    [TestCase]
    public static async Task TimeoutAndCancellationAreDistinct()
    {
        var store = new MemoryStore();
        var service = new UpdateService(new Factory(), new SystemClock(), store);
        var result = await service.CheckAsync([Target("slow")], new(TimeoutSeconds: 1), CancellationToken.None);
        Assertions.AssertString(result.Failures[0].Message).IsEqual("Request timed out.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await service.CheckAsync([Target("slow")], new(), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assertions.AssertBool(cancelled).IsTrue();
    }

    [TestCase]
    public static async Task VersionListIsNewestFirstAndNeedsAListingSource()
    {
        var service = new UpdateService(new Factory(), new SystemClock(), new MemoryStore());
        var versions = await service.ListVersionsAsync(Target("listed") with { UpdateUrl = "https://example.org/listed/releases" }, new(), CancellationToken.None);
        Assertions.AssertArray(versions.Select(v => v.NewVersion).ToArray()).ContainsExactly("3.0.0-beta.1", "2.0.0", "1.10.0", "1.2.0");
        var refused = false;
        try { await service.ListVersionsAsync(Target("plain"), new(), CancellationToken.None); }
        catch (NotSupportedException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();
    }

    [TestCase]
    public static async Task NewerListedVersionIsCachedAsTheUpdate()
    {
        var store = new MemoryStore();
        var service = new UpdateService(new Factory(), new SystemClock(), store);
        var target = Target("listed", "1.5.0") with { UpdateUrl = "https://example.org/listed/releases" };
        await service.ListVersionsAsync(target, new(), CancellationToken.None);
        var cached = UpdateScheduler.CurrentCached(store.State, [target]).Single();
        Assertions.AssertString(cached.NewVersion).IsEqual("2.0.0");
        Assertions.AssertString(cached.InstalledVersion).IsEqual("1.5.0");
        Assertions.AssertObject(store.State.LastCheckUtc).IsNull();

        // A cached update at least as new stays, e.g. a pinned branch head; one of an older update_url is replaced.
        store.State.Results = [Candidate("listed", "3.0.0") with { SourceUrl = target.UpdateUrl }];
        await service.ListVersionsAsync(target, new(), CancellationToken.None);
        Assertions.AssertString(store.State.Results.Single().NewVersion).IsEqual("3.0.0");
        store.State.Results = [Candidate("listed", "3.0.0") with { SourceUrl = "https://example.org/old" }];
        await service.ListVersionsAsync(target, new(), CancellationToken.None);
        Assertions.AssertString(store.State.Results.Single().NewVersion).IsEqual("2.0.0");

        // Nothing newer than the installed version but a prerelease, which is no update of a stable install: the cache
        // is left alone. A prerelease install caches it.
        store.State.Results = [];
        await service.ListVersionsAsync(target with { InstalledVersion = "2.0.0" }, new(), CancellationToken.None);
        Assertions.AssertInt(store.State.Results.Count).IsEqual(0);
        await service.ListVersionsAsync(target with { InstalledVersion = "2.0.0-rc.1" }, new(), CancellationToken.None);
        Assertions.AssertString(store.State.Results.Single().NewVersion).IsEqual("3.0.0-beta.1");
    }

    [TestCase]
    public static async Task ChecksChangeTheCacheOnlyWhereItsOtherWritersRun()
    {
        // Checks and version lists run on a thread-pool thread, while installing an update changes the cache on the editor
        // thread: the cache is read and written only through the work handed to the editor thread.
        var store = new MemoryStore();
        var queued = new List<Action>();
        var service = new UpdateService(new Factory(), new SystemClock(), store, (action, _) => { queued.Add(action); return Task.CompletedTask; });
        await service.CheckAsync([Target("healthy")], new(), CancellationToken.None);
        await service.ListVersionsAsync(Target("listed", "1.5.0") with { UpdateUrl = "https://example.org/listed/releases" }, new(), CancellationToken.None);
        Assertions.AssertObject(store.State.LastCheckUtc).IsNull();
        Assertions.AssertInt(store.State.Results.Count).IsEqual(0);
        Assertions.AssertInt(queued.Count).IsEqual(2);
        foreach (var action in queued) action();
        Assertions.AssertObject(store.State.LastCheckUtc).IsNotNull();
        Assertions.AssertArray(store.State.Results.Select(c => c.Slug).ToArray()).ContainsExactly("healthy", "listed");
    }

    [TestCase]
    public static void VersionsOfFailuresAndInstalledReleasesAreComparedByValue()
    {
        // A failure records the plugin.cfg version, a release announces its tag's; Godot reads an unquoted 1.2.0 as 1.2.
        var cache = new UpdateCache();
        cache.FailedUpdates["plugin"] = [new("1.2.0", "failed", DateTimeOffset.UtcNow)];
        Assertions.AssertObject(cache.LastFailure(Candidate("plugin", "v1.2.0"))).IsNotNull();
        Assertions.AssertObject(cache.LastFailure(Candidate("plugin", "v1.2.1"))).IsNull();
        var installed = Candidate("plugin", "v1.3.0") with { InstalledVersion = "1.2", ResolvedRevision = "abc", SourceUrl = "https://example.org/plugin" };
        var revisions = new Dictionary<string, AppliedRevision> { ["plugin"] = new("https://example.org/plugin", "abc", DateTimeOffset.UtcNow, "1.2.0") };
        Assertions.AssertThat(UpdateService.WithoutInstalled([installed], revisions)).IsEmpty();
    }

    [TestCase]
    public static void UpdatesMoveToTheNextBetterVersionOfTheSameKind()
    {
        static bool IsUpdate(string installed, string version) =>
            SemVer.TryParse(installed, out var i) && SemVer.TryParse(version, out var v) && UpdateService.IsUpdate(i, v);
        // stable to stable only
        Assertions.AssertBool(IsUpdate("1.0.0", "1.1.0")).IsTrue();
        Assertions.AssertBool(IsUpdate("1.0.0", "2.0.0-beta.1")).IsFalse();
        // a prerelease to a newer prerelease or to the release
        Assertions.AssertBool(IsUpdate("1.0.0-beta.1", "1.0.0-beta.2")).IsTrue();
        Assertions.AssertBool(IsUpdate("1.0.0-beta.2", "1.0.0")).IsTrue();
        Assertions.AssertBool(IsUpdate("1.0.0-beta.2", "1.1.0-alpha")).IsTrue();
        // never to an older or the same version
        Assertions.AssertBool(IsUpdate("1.0.0-beta.2", "1.0.0-beta.1")).IsFalse();
        Assertions.AssertBool(IsUpdate("1.0.0", "1.0.0")).IsFalse();
        Assertions.AssertBool(IsUpdate("1.0.0", "0.9.0")).IsFalse();
    }

    private static PluginUpdateTarget Target(string slug, string installed = "1.0.0") => new(slug, slug, installed, "https://example.org/releases", "/unused");
    private static UpdateCandidate Candidate(string slug, string version) => new(slug, slug, "1.0.0", version,
        "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/package.zip", "plugin"));
    private sealed class Factory : IUpdateSourceFactory
    {
        public IUpdateSource Create(string url) => url.Contains("listed") ? new ListingSource() : new Source();
    }
    private sealed class ListingSource : IUpdateSource, IVersionListSource
    {
        public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) => Task.FromResult<UpdateCandidate?>(null);
        public Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UpdateCandidate>>([Candidate(target.Slug, "1.2.0"), Candidate(target.Slug, "3.0.0-beta.1"), Candidate(target.Slug, "2.0.0"), Candidate(target.Slug, "1.10.0"), Candidate(target.Slug, "2.0.0"), Candidate(target.Slug, "bad")]);
    }
    private sealed class Source : IUpdateSource
    {
        public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
        {
            if (target.Slug == "broken") throw new IOException("offline");
            if (target.Slug == "slow") await Task.Delay(Timeout.Infinite, ct);
            return Candidate(target.Slug, "2.0.0");
        }
    }
    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateCache State { get; } = new();
        public void Save() { }
    }
}
#endif
