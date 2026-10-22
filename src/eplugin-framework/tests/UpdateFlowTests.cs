#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The ePlugin Manager's update workflows without its dialog: checking, loading versions, and staging, reviewing and
/// installing a batch with a fake framework.
/// </summary>
[TestSuite]
public class UpdateFlowTests : IDisposable
{
    private FakeHost _host = null!;
    private LicenseReview? _askedLicenses;
    private UpdateReview? _askedReview;
    private Func<UpdateReview, bool> _review = _ => true;
    private Action<LicenseReview> _decide = review => review.AcceptAll();
    private readonly List<string> _progress = [];
    private readonly List<string> _status = [];
    private int _swaps;
    private bool _alive = true;

    [BeforeTest]
    public void Setup()
    {
        _host = new FakeHost();
        _askedLicenses = null;
        _askedReview = null;
        _review = _ => true;
        _decide = review => review.AcceptAll();
        _progress.Clear(); _status.Clear(); _swaps = 0; _alive = true;
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    private UpdateFlow Flow() => new(_host, review =>
    {
        _askedReview = review;
        return Task.FromResult(_review(review));
    }, review =>
    {
        _askedLicenses = review;
        _decide(review);
        return Task.FromResult(review);
    }, text => new RecordingScope(_progress, text));

    private InstallView View() => new()
    {
        Alive = () => _alive,
        Status = _status.Add,
        Swapping = () => _swaps++
    };

    private static UpdateCandidate Candidate(string slug) =>
        new(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));

    private static Finding Warning(bool trust = false) => new("source_changed", FindingSeverity.Warning, "Update source changed", trust);

    [TestCase]
    public async Task ABatchWithoutWarningsIsStagedAndInstalledInOneGo()
    {
        _host.Packages["a"] = [];
        var flow = Flow();

        var outcome = await flow.InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Installed);
        Assertions.AssertBool(outcome.Refresh).IsTrue();
        Assertions.AssertBool(outcome.IsError).IsFalse();
        Assertions.AssertString(outcome.Notice).Contains("a 2.0.0");
        Assertions.AssertThat(_host.Applied.Single().Packages.Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("a");
        Assertions.AssertBool(_host.Applied.Single().AskOnBuildFailure).IsFalse();
        Assertions.AssertInt(_swaps).IsEqual(1);
        // the download reports its percentage in the progress window, each value once
        Assertions.AssertThat(_progress).ContainsExactly("Downloading and validating selected addons...",
            "Downloading and validating selected addons... 50%", "Downloading and validating selected addons... 100%", "end");
        Assertions.AssertThat(_status).ContainsExactly("Downloading and validating selected addons...", "Installing selected addons...");
        Assertions.AssertObject(flow.Staged).IsNull();
        Assertions.AssertObject(flow.Request).IsNull();
        Assertions.AssertObject(_askedReview).IsNull();
    }

    [TestCase]
    public async Task WarningsAreReviewedBeforeAnythingIsInstalled()
    {
        _host.Packages["a"] = [Warning()];
        _host.Packages["b"] = [];
        _review = _ =>
        {
            Assertions.AssertInt(_host.Applied.Count).IsEqual(0);
            return true;
        };

        var outcome = await Flow().InstallAsync(new([Candidate("a"), Candidate("b")]), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Installed);
        // only the package with a warning is listed; the other one is installed with it
        Assertions.AssertThat(_askedReview!.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("a");
        Assertions.AssertInt(_askedReview.Unlisted).IsEqual(1);
        Assertions.AssertThat(_host.Applied.Single().Packages.Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("a", "b");
        Assertions.AssertThat(_status).Contains("Review the updates.");
    }

    [TestCase]
    public async Task UpdatesLeftOutInTheReviewAreNotInstalled()
    {
        _host.Packages["a"] = [Warning()];
        _host.Packages["b"] = [];
        _review = review => { review.Entries.Single().Install = false; return true; };

        var outcome = await Flow().InstallAsync(new([Candidate("a"), Candidate("b")]), View(), CancellationToken.None);

        Assertions.AssertThat(_host.Applied.Single().Packages.Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("b");
        Assertions.AssertString(outcome.Notice).Contains("Left out a 2.0.0.");
        Assertions.AssertBool(outcome.IsError).IsFalse();
    }

    [TestCase]
    public async Task CancelingTheReviewInstallsNothing()
    {
        _host.Packages["a"] = [Warning()];
        _review = _ => false;
        var flow = Flow();

        var outcome = await flow.InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Canceled);
        Assertions.AssertString(outcome.Notice).IsEqual("Update canceled. Addon files were not changed.");
        Assertions.AssertInt(_host.Applied.Count).IsEqual(0);
        Assertions.AssertObject(flow.Staged).IsNull();
        Assertions.AssertBool(Directory.Exists(_host.StagingDirectory)).IsFalse();
    }

    [TestCase]
    public async Task AChangedUpdateHostIsInstalledOnlyWhenTheUserTrustsIt()
    {
        _host.Packages["a"] = [new Finding("source_changed", FindingSeverity.Info, "Update host changed", RequiresTrust: true)];

        // not checked by default, so confirming the review installs nothing
        var untrusted = await Flow().InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);
        Assertions.AssertBool(_askedReview!.Entries.Single().NeedsTrust).IsTrue();
        Assertions.AssertObject(untrusted.Step).IsEqual(InstallStep.Canceled);
        Assertions.AssertString(untrusted.Notice).StartsWith("No update was installed");
        Assertions.AssertInt(_host.Applied.Count).IsEqual(0);

        _review = review => { review.Entries.Single().Install = true; return true; };
        Assertions.AssertObject((await Flow().InstallAsync(new([Candidate("a")]), View(), CancellationToken.None)).Step).IsEqual(InstallStep.Installed);
        Assertions.AssertBool(_host.Applied.Single().Trust).IsTrue();
    }

    [TestCase]
    public async Task ADeclinedLicenseLeavesItsUpdateOutAndDecliningAllChangesNothing()
    {
        _host.Packages["a"] = [];
        _host.Packages["b"] = [];
        _host.LicensesToReview = ["a", "b"];
        _decide = review => { review.Accept("a"); review.Decline("b"); };
        var flow = Flow();

        var outcome = await flow.InstallAsync(new([Candidate("a"), Candidate("b")]), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Installed);
        Assertions.AssertThat(_host.Applied.Single().Packages.Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("a");
        Assertions.AssertThat(_host.Recorded.Single().Accepted.Select(e => e.Slug).ToArray()).ContainsExactly("a");

        _decide = review => review.DeclineRemaining();
        var canceled = await Flow().InstallAsync(new([Candidate("a"), Candidate("b")]), View(), CancellationToken.None);

        Assertions.AssertObject(canceled.Step).IsEqual(InstallStep.Canceled);
        Assertions.AssertString(canceled.Notice).Contains("license was declined");
        Assertions.AssertInt(_host.Applied.Count).IsEqual(1);
        // the staging directory of a batch that is not installed is removed
        Assertions.AssertBool(Directory.Exists(_host.StagingDirectory)).IsFalse();
    }

    [TestCase]
    public async Task AChosenVersionAsksWhetherToKeepAVersionThatDoesNotBuild()
    {
        _host.Packages["a"] = [];
        _host.Outcome = UpdateOutcome.AwaitingDecision;

        var outcome = await Flow().InstallAsync(new([Candidate("a")], VersionInstall: true, AllowDowngrade: true), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.AwaitingDecision);
        Assertions.AssertBool(_host.Applied.Single().AskOnBuildFailure).IsTrue();
        Assertions.AssertBool(_host.AllowDowngrade).IsTrue();
        Assertions.AssertString(_status[0]).IsEqual("Downloading and validating a 2.0.0...");
        Assertions.AssertString(_status[^1]).IsEqual("Installing the selected version...");
    }

    [TestCase]
    public async Task CancelingTheDownloadAndFailuresChangeNoAddonFiles()
    {
        _host.Packages["a"] = [];
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var cancel = await Flow().InstallAsync(new([Candidate("a")]), View(), canceled.Token);
        Assertions.AssertObject(cancel.Step).IsEqual(InstallStep.Canceled);
        Assertions.AssertString(cancel.Notice).IsEqual("Download canceled. Addon files were not changed.");

        _host.ApplyFails = true;
        var flow = Flow();
        var failed = await flow.InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);
        Assertions.AssertObject(failed.Step).IsEqual(InstallStep.Failed);
        Assertions.AssertBool(failed.IsError).IsTrue();
        Assertions.AssertString(failed.Notice).IsEqual("Update failed: no space left");
        // a refusal before the transaction leaves no journal, so the staged batch is dropped
        Assertions.AssertObject(flow.Staged).IsNull();
        Assertions.AssertBool(Directory.Exists(_host.StagingDirectory)).IsFalse();
    }

    [TestCase]
    public async Task AManagerClosedWhileStagingInstallsNothing()
    {
        _host.Packages["a"] = [];
        _alive = false;
        var flow = Flow();

        var outcome = await flow.InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);

        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Abandoned);
        Assertions.AssertInt(_host.Applied.Count).IsEqual(0);
        Assertions.AssertObject(flow.Staged).IsNull();
    }

    [TestCase]
    public async Task AManagerClosedWhileADecisionIsAskedInstallsNothing()
    {
        _host.Packages["a"] = [Warning()];
        _review = _ => { _alive = false; return true; };
        var flow = Flow();

        var review = await flow.InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);

        Assertions.AssertObject(review.Step).IsEqual(InstallStep.Abandoned);
        Assertions.AssertObject(flow.Staged).IsNull();
        Assertions.AssertBool(Directory.Exists(_host.StagingDirectory)).IsFalse();

        _alive = true;
        _host.Packages["a"] = [];
        _host.LicensesToReview = ["a"];
        _decide = licenses => { licenses.AcceptAll(); _alive = false; };
        var license = await Flow().InstallAsync(new([Candidate("a")]), View(), CancellationToken.None);

        Assertions.AssertObject(license.Step).IsEqual(InstallStep.Abandoned);
        // the decision is kept, as the dialog recorded it before the manager was found closed
        Assertions.AssertInt(_host.Recorded.Count).IsEqual(1);
        Assertions.AssertInt(_host.Applied.Count).IsEqual(0);
        Assertions.AssertBool(Directory.Exists(_host.StagingDirectory)).IsFalse();
    }

    [TestCase]
    public async Task RefusedPackagesAreReviewedAndTheRestIsInstalled()
    {
        _host.Packages["a"] = [];
        _host.Refused = [new RefusedPackage(Candidate("b"), "contains a GDExtension")];

        var outcome = await Flow().InstallAsync(new([Candidate("a"), Candidate("b")]), View(), CancellationToken.None);

        var refused = _askedReview!.Entries.Single();
        Assertions.AssertString(refused.RefusedReason).IsEqual("contains a GDExtension");
        Assertions.AssertBool(refused.CanInstall || refused.Install).IsFalse();
        Assertions.AssertThat(_host.Applied.Single().Packages.Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("a");
        Assertions.AssertObject(outcome.Step).IsEqual(InstallStep.Installed);
        Assertions.AssertBool(outcome.IsError).IsTrue();
        Assertions.AssertString(outcome.Notice).Contains("contains a GDExtension");
    }

    [TestCase]
    public async Task ACheckReportsFailedAndReplacedUpdateSitesAndForgetsLoadedVersions()
    {
        var flow = Flow();
        var target = new PluginUpdateTarget("a", "a", "1.0.0", "https://example.org/a/releases", "/unused");
        await flow.LoadVersionsAsync(target, CancellationToken.None);
        Assertions.AssertBool(flow.RemoteVersions.ContainsKey("a")).IsTrue();

        _host.Fallbacks = [new UpdateCheckFailure("a", "404")];
        var fallback = await flow.CheckAsync();
        Assertions.AssertBool(fallback.IsError).IsFalse();
        Assertions.AssertString(fallback.Notice).Contains("The update site of a did not work");
        Assertions.AssertBool(flow.RemoteVersions.ContainsKey("a")).IsFalse();

        _host.Failures = [new UpdateCheckFailure("b", "timed out")];
        var failed = await flow.CheckAsync();
        Assertions.AssertBool(failed.IsError).IsTrue();
        Assertions.AssertString(failed.Notice).IsEqual("Update check failed: b: timed out");

        _host.CheckThrows = true;
        Assertions.AssertString((await flow.CheckAsync()).Notice).IsEqual("Update check failed: offline");
    }

    [TestCase]
    public async Task VersionsAreLoadedOnceAndAFailureIsKeptAsTheReason()
    {
        var flow = Flow();
        var target = new PluginUpdateTarget("a", "a", "1.0.0", "https://example.org/a/releases", "/unused");
        var pending = new TaskCompletionSource<IReadOnlyList<UpdateCandidate>>();
        _host.Versions = pending.Task;

        var first = flow.LoadVersionsAsync(target, CancellationToken.None);
        Assertions.AssertBool(flow.IsLoadingVersions("a")).IsTrue();
        Assertions.AssertBool(await flow.LoadVersionsAsync(target, CancellationToken.None)).IsFalse();
        pending.SetResult([Candidate("a")]);
        Assertions.AssertBool(await first).IsTrue();
        Assertions.AssertBool(flow.IsLoadingVersions("a")).IsFalse();
        Assertions.AssertInt(flow.RemoteVersions["a"].Count).IsEqual(1);

        flow.ClearVersions();
        _host.Versions = Task.FromException<IReadOnlyList<UpdateCandidate>>(new InvalidOperationException("rate limited"));
        Assertions.AssertBool(await flow.LoadVersionsAsync(target, CancellationToken.None)).IsTrue();
        Assertions.AssertString(flow.RemoteVersionErrors["a"]).IsEqual("rate limited");

        // a manager closed meanwhile keeps nothing
        flow.ClearVersions();
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        _host.Versions = Task.FromCanceled<IReadOnlyList<UpdateCandidate>>(closed.Token);
        Assertions.AssertBool(await flow.LoadVersionsAsync(target, closed.Token)).IsFalse();
        Assertions.AssertBool(flow.RemoteVersions.ContainsKey("a") || flow.RemoteVersionErrors.ContainsKey("a")).IsFalse();
    }

    [TestCase]
    public static async Task WorkStartedFromASignalReportsItsFailureInsteadOfThrowing()
    {
        var reported = new TaskCompletionSource<Exception>();
        ObservedTask.Run(Task.Run(() => throw new InvalidOperationException("broken")), reported.SetResult);
        Assertions.AssertString((await reported.Task).Message).IsEqual("broken");

        var silent = true;
        var done = Task.CompletedTask;
        ObservedTask.Run(done, _ => silent = false);
        await done;
        Assertions.AssertBool(silent).IsTrue();
    }

    private sealed class RecordingScope : IEPluginProgressScope
    {
        private readonly List<string> _log;

        public RecordingScope(List<string> log, string text)
        {
            _log = log;
            _log.Add(text);
        }

        public void Report(string value)
        {
            lock (_log) _log.Add(value);
        }

        public void Dispose() => _log.Add("end");
    }

    /// <summary>The framework of the flow: stages into a temporary directory and records what it is asked to install.</summary>
    private sealed class FakeHost : IUpdateWorkflowHost, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "eplugin-flow-" + Guid.NewGuid().ToString("N"));

        public Dictionary<string, Finding[]> Packages { get; } = [];
        public IReadOnlyList<RefusedPackage> Refused { get; set; } = [];
        public string[] LicensesToReview { get; set; } = [];
        public UpdateOutcome Outcome { get; set; } = UpdateOutcome.Completed;
        public bool ApplyFails { get; set; }
        public bool CheckThrows { get; set; }
        public IReadOnlyList<UpdateCheckFailure> Failures { get; set; } = [];
        public IReadOnlyList<UpdateCheckFailure>? Fallbacks { get; set; }
        public Task<IReadOnlyList<UpdateCandidate>> Versions { get; set; } = Task.FromResult<IReadOnlyList<UpdateCandidate>>([]);
        public List<(IReadOnlyList<ValidatedPackage> Packages, bool Trust, bool AskOnBuildFailure)> Applied { get; } = [];
        public List<LicenseReview> Recorded { get; } = [];
        public int Stagings { get; private set; }
        public bool AllowDowngrade { get; private set; }
        public string StagingDirectory { get; private set; } = "";

        public IReadOnlyList<UpdateCandidate> PendingUpdates => [];
        public bool IsRestartPending => false;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(bool force) => CheckThrows
            ? Task.FromException<UpdateCheckResult>(new InvalidOperationException("offline"))
            : Task.FromResult(new UpdateCheckResult([], Failures, DateTimeOffset.UtcNow, Fallbacks));

        public Task<IReadOnlyList<UpdateCandidate>> ListRemoteVersionsAsync(PluginUpdateTarget target, CancellationToken ct) => Versions;

        public Task<(IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused, string Directory)> StageUpdatesAsync(
            IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress, bool allowDowngrade, bool allowReinstall, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Stagings++;
            AllowDowngrade = allowDowngrade;
            progress?.Report(0.5); progress?.Report(0.5); progress?.Report(1.0);
            StagingDirectory = Path.Combine(_root, "stage-" + Stagings);
            Directory.CreateDirectory(StagingDirectory);
            Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
            var packages = candidates.Where(c => Packages.ContainsKey(c.Slug))
                .Select(c => new ValidatedPackage(c, Path.Combine(StagingDirectory, c.Slug), version, null, false, Packages[c.Slug]))
                .ToArray();
            return Task.FromResult<(IReadOnlyList<ValidatedPackage>, IReadOnlyList<RefusedPackage>, string)>((packages, Refused, StagingDirectory));
        }

        public LicenseReview? ReviewUpdateLicenses(IEnumerable<ValidatedPackage> packages)
        {
            var entries = packages.Where(p => LicensesToReview.Contains(p.Candidate.Slug))
                .Select(p => PluginLicense.FromText(p.Candidate.Slug, p.Candidate.PluginName, "2.0.0", "Terms")).ToArray();
            return entries.Length == 0 ? null
                : new LicenseReview(entries, entries.Select(e => new LicenseActivation(e.Slug, e.Name, [e.Slug])).ToArray(), isUpdate: true);
        }

        public void RecordLicenses(LicenseReview review) => Recorded.Add(review);

        public Task<IReadOnlyList<UpdateGroupResult>> ApplyUpdatesAsync(IReadOnlyList<ValidatedPackage> packages, string directory,
            bool trustChangedSources, bool askOnBuildFailure)
        {
            if (ApplyFails) return Task.FromException<IReadOnlyList<UpdateGroupResult>>(new IOException("no space left"));
            Applied.Add((packages, trustChangedSources, askOnBuildFailure));
            return Task.FromResult<IReadOnlyList<UpdateGroupResult>>([new UpdateGroupResult(packages, directory, Outcome, null)]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
#endif
