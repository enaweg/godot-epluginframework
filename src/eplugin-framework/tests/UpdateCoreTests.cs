using System;
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
    public void SemVerParsingAndPrecedence()
    {
        var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "v1.0.0+build", "1.1", "2.0.0" };
        for (var i = 0; i < ordered.Length; i++)
        {
            Assertions.AssertBool(SemVer.TryParse(ordered[i], out var current)).IsTrue();
            if (i > 0)
            {
                SemVer.TryParse(ordered[i - 1], out var previous);
                Assertions.AssertBool(current.CompareTo(previous) > 0).IsTrue();
            }
        }
        foreach (var invalid in new[] { "1", "01.0", "1.0.0.0", "1.0.0-01", "1.0.0+", "garbage", "1.0.0\nextra" })
            Assertions.AssertBool(SemVer.TryParse(invalid, out _)).IsFalse();
    }

    [TestCase]
    public void ScheduleBoundaryAndCacheUseInstalledVersion()
    {
        var now = DateTimeOffset.UtcNow;
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, now.AddHours(-20), true)).IsTrue();
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, now.AddHours(-19), true)).IsFalse();
        Assertions.AssertBool(UpdateScheduler.ShouldCheck(now, null, false)).IsFalse();
        var cache = new UpdateCache { Results = [Candidate("plugin", "2.0.0")] };
        Assertions.AssertInt(UpdateScheduler.CurrentCached(cache, [Target("plugin", "3.0.0")], false).Count).IsEqual(0);
        Assertions.AssertInt(UpdateScheduler.CurrentCached(cache, [Target("plugin", "1.0.0")], false).Count).IsEqual(1);
    }

    [TestCase]
    public async Task FailingSourceDoesNotStopOthersAndOfflineDoesNotAdvanceTimestamp()
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
    public async Task TimeoutAndCancellationAreDistinct()
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

    private static PluginUpdateTarget Target(string slug, string installed = "1.0.0") => new(slug, slug, installed, "https://example.org/releases", "/unused");
    private static UpdateCandidate Candidate(string slug, string version) => new(slug, slug, "1.0.0", version,
        "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/package.zip", "plugin"));
    private sealed class Factory : IUpdateSourceFactory
    {
        public IUpdateSource Create(string url) => new Source();
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

