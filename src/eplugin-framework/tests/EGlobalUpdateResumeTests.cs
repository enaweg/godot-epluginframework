#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Update transactions <see cref="EGlobal"/> finds at startup, and the decisions the ePlugin Manager asks for: journals
/// of a plain plugin <c>tool</c> updated from 1.0.0 to 2.0.0 in the fixture's project, with an editor that only records
/// what the transaction asks of it.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalUpdateResumeTests : IDisposable
{
    private LifecycleFixture _fixture = null!;
    private RecordingUpdateHost _updates = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _updates = new RecordingUpdateHost(_fixture);
        _fixture.Global.UpdateHost = _updates;
        _fixture.Global.InitializeUpdateJournals();
        _fixture.AddPlainPlugin("tool", "2.0.0");
        _fixture.Host.Enabled.Add("tool");
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Installed => File.ReadAllText(Path.Combine(_fixture.Addon("tool"), "plugin.cfg"));

    private Guid Attempt()
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("tool", "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        return id;
    }

    /// <summary>A journal whose new files are installed and whose old ones are in its backup.</summary>
    private UpdateJournal Journal(UpdatePhase phase, Guid attempt, DateTimeOffset? created = null)
    {
        var directory = Path.Combine(_fixture.Root, ".godot", "eplugin", "updates", "update-" + Guid.NewGuid().ToString("N")[..8]);
        var journal = new UpdateJournal { Id = Path.GetFileName(directory), Directory = directory, AttemptId = attempt, CreatedUtc = created ?? DateTimeOffset.UtcNow };
        journal.Plugins.Add(new()
        {
            Slug = "tool", OldVersion = "1.0.0", NewVersion = "2.0.0", SourceUrl = "https://example.org/releases", WasEnabled = true,
            SwapStarted = true
        });
        Directory.CreateDirectory(journal.Backup("tool"));
        File.WriteAllText(Path.Combine(journal.Backup("tool"), "plugin.cfg"), "[plugin]\nname=\"tool\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n");
        journal.Save(phase);
        return journal;
    }

    private void AssertRolledBack(UpdateJournal journal, string reason)
    {
        Assertions.AssertString(Installed).Contains("1.0.0");
        var after = UpdateJournal.Load(journal.Directory);
        Assertions.AssertObject(after.State).IsEqual(UpdatePhase.RolledBack);
        Assertions.AssertString(after.Failure).IsEqual(reason);
        Assertions.AssertInt(_fixture.Store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertBool(_fixture.Global.UpdateCache!.FailedUpdates["tool"].Any(f => f.Version == "2.0.0")).IsTrue();
        // a plain plugin is disabled while its files are swapped back, then enabled as it was
        Assertions.AssertThat(_updates.Toggles).ContainsExactly("tool False", "tool True");
    }

    [TestCase]
    public void AnUpdateInterruptedAfterTheSwapIsRolledBackAtStartup()
    {
        var journal = Journal(UpdatePhase.Swapped, Attempt());

        _fixture.Global.ResumeUpdates();

        AssertRolledBack(journal, "Interrupted update.");
    }

    [TestCase]
    public void StagedUpdatesAreDiscardedWithTheirMarker()
    {
        var marked = Journal(UpdatePhase.Staged, Attempt());
        var unmarked = Journal(UpdatePhase.Staged, Guid.NewGuid());

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(marked.Directory)).IsFalse();
        Assertions.AssertBool(Directory.Exists(unmarked.Directory)).IsFalse();
        Assertions.AssertInt(_fixture.Store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertString(Installed).Contains("2.0.0");
    }

    [TestCase]
    public void AVerifiedUpdateIsCommittedAndOneThatLostItsMarkerIsLeftForRepair()
    {
        var verified = Journal(UpdatePhase.Verified, Attempt());

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(verified.Directory)).IsFalse();
        Assertions.AssertString(_fixture.Store.GetShared("tool")!.Version).IsEqual("2.0.0");
        Assertions.AssertObject(_fixture.Store.LastCompletedAttemptId).IsEqual(verified.AttemptId);
        Assertions.AssertInt(_fixture.Store.LocalAttempts.Count).IsEqual(0);

        var unmarked = Journal(UpdatePhase.Verified, Guid.NewGuid());
        _fixture.Global.ResumeUpdates();

        Assertions.AssertObject(UpdateJournal.Load(unmarked.Directory).State).IsEqual(UpdatePhase.Verified);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("Update marker is missing"))).IsTrue();
    }

    [TestCase]
    public void TheJournalOfAnAcknowledgedUpdateIsOnlyRemoved()
    {
        var attempt = Attempt();
        Assertions.AssertBool(_fixture.Store.TryAcknowledgeVersions(attempt, new Dictionary<string, string> { ["tool"] = "2.0.0" })).IsTrue();
        // the editor stopped after the acknowledgement, before the journal was removed
        var journal = Journal(UpdatePhase.Swapped, attempt);

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(journal.Directory)).IsFalse();
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertThat(_updates.Toggles).IsEmpty();
    }

    [TestCase]
    public void AnInterimBuildWithoutItsMarkerIsRolledBackAndOneWithItResumes()
    {
        var lost = Journal(UpdatePhase.InterimBuilt, Guid.NewGuid());

        _fixture.Global.ResumeUpdates();

        Assertions.AssertString(UpdateJournal.Load(lost.Directory).Failure).IsEqual("Update marker was lost.");
        Assertions.AssertString(Installed).Contains("1.0.0");

        Cleanup(); Setup();
        var marked = Journal(UpdatePhase.InterimBuilt, Attempt());
        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(marked.Directory)).IsFalse();
        Assertions.AssertString(_fixture.Store.GetShared("tool")!.Version).IsEqual("2.0.0");
    }

    [TestCase]
    public void AnUpdateAwaitingADecisionRollsBackWithoutDialogs()
    {
        var journal = Journal(UpdatePhase.AwaitingDecision, Attempt());

        _fixture.Global.ResumeUpdates();

        AssertRolledBack(journal, "Cannot show the build-failure dialog.");
    }

    [TestCase]
    public void AnUpdateAwaitingADecisionAsksOnTheNextFrameAndIsRolledBackWithoutSomeoneToAsk()
    {
        _updates.UiAvailable = true;
        var journal = Journal(UpdatePhase.AwaitingDecision, Attempt());

        _fixture.Global.ResumeUpdates();
        Assertions.AssertString(Installed).Contains("2.0.0");
        // no dialog is listening
        EGlobal.RunPosted();

        AssertRolledBack(journal, "No build-failure dialog is available.");
    }

    [TestCase]
    public void TheDecisionDialogCanKeepAnUpdateThatIsRolledBackLaterOnRequest()
    {
        _updates.UiAvailable = true;
        var journal = Journal(UpdatePhase.AwaitingDecision, Attempt());
        UpdateJournal? asked = null;
        _fixture.Global.UpdateDecisionNeeded += j => asked = j;

        _fixture.Global.ResumeUpdates();
        EGlobal.RunPosted();

        Assertions.AssertString(asked!.Id).IsEqual(journal.Id);
        _fixture.Global.DecideUpdate(asked, keep: true);
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertString(_fixture.Store.GetLocal("tool")!.Reason).IsEqual("update_kept_build_failed");

        // the ePlugin Manager offers to roll back the kept update; a restart leaves it alone
        var kept = _fixture.Global.KeptUpdate("tool")!;
        Assertions.AssertString(kept.Id).IsEqual(journal.Id);
        Assertions.AssertObject(_fixture.Global.KeptUpdate("other")).IsNull();
        _fixture.Global.ResumeUpdates();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Update needs manual recovery"))).IsTrue();
        Assertions.AssertString(Installed).Contains("2.0.0");

        _fixture.Global.RollbackKeptUpdate(kept);

        AssertRolledBack(journal, "Rolled back on request after it was kept with build errors.");
        Assertions.AssertObject(_fixture.Global.KeptUpdate("tool")).IsNull();
    }

    [TestCase]
    public void RetryFinishesAKeptUpdateOnceItBuilds()
    {
        _updates.UiAvailable = true;
        var journal = Journal(UpdatePhase.AwaitingDecision, Attempt());
        _fixture.Global.UpdateDecisionNeeded += j => _fixture.Global.DecideUpdate(j, keep: true);
        _fixture.Global.ResumeUpdates();
        EGlobal.RunPosted();
        Assertions.AssertString(_fixture.Store.GetLocal("tool")!.Reason).IsEqual("update_kept_build_failed");

        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertBool(Directory.Exists(journal.Directory)).IsFalse();
        Assertions.AssertString(_fixture.Store.GetShared("tool")!.Version).IsEqual("2.0.0");
        Assertions.AssertInt(_fixture.Store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertInt(_updates.Builds).IsEqual(1);
    }

    [TestCase]
    public void RetryReportsAnUpdateWhoseJournalIsGone()
    {
        var attempt = Attempt();
        Assertions.AssertBool(_fixture.Store.TryFail(attempt, PersistedPluginState.Failed, "update_commit_failed")).IsTrue();

        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("Update journal is missing"))).IsTrue();
        Assertions.AssertObject(_fixture.Store.GetLocal("tool")).IsNotNull();
    }

    [TestCase]
    public void FinishedJournalsAreKeptForAWeek()
    {
        var old = Journal(UpdatePhase.RolledBack, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-8));
        var recent = Journal(UpdatePhase.Committed, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-6));

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(old.Directory)).IsFalse();
        Assertions.AssertBool(Directory.Exists(recent.Directory)).IsTrue();
    }

    private sealed class RecordingUpdateHost(LifecycleFixture fixture) : IUpdateHost
    {
        public bool UiAvailable { get; set; }
        public string BuildFailurePolicy => "ask";
        public bool RestartAlways => false;
        public List<string> Toggles { get; } = [];
        public int Builds { get; private set; }
        public bool IsEnabled(string slug) => fixture.Host.IsPluginEnabled(slug);
        public bool IsManaged(string slug) => false;
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) { }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { }
        public void Reconcile(UpdateJournal journal, bool rollback) { }
        public void SetPlainEnabled(string slug, bool enabled)
        {
            Toggles.Add($"{slug} {enabled}");
            fixture.Host.SetPluginEnabled(slug, enabled);
        }
        public void CloseScenes() { }
        public void Scan() { }
        public BuildOutcome Build()
        {
            Builds++;
            return new(0, []);
        }
        public void RequestReload(UpdateJournal journal) { }
        public bool Verify(UpdatePluginJournal plugin) => true;
        public void Log(string message) => fixture.Log.Add("log: update: " + message);
    }
}
#endif
