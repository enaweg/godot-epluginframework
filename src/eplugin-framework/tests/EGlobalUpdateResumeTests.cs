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

    private Guid Attempt(string slug = "tool")
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        return id;
    }

    /// <summary>A journal whose new files are installed and whose old ones are in its backup.</summary>
    private UpdateJournal Journal(UpdatePhase phase, Guid attempt, DateTimeOffset? created = null, string slug = "tool")
    {
        var directory = Path.Combine(_fixture.Root, ".godot", "eplugin", "updates", "update-" + Guid.NewGuid().ToString("N")[..8]);
        var journal = new UpdateJournal { Id = Path.GetFileName(directory), Directory = directory, AttemptId = attempt, CreatedUtc = created ?? DateTimeOffset.UtcNow };
        journal.Plugins.Add(new()
        {
            Slug = slug, OldVersion = "1.0.0", NewVersion = "2.0.0", SourceUrl = "https://example.org/releases", WasEnabled = true,
            SwapStarted = true
        });
        Directory.CreateDirectory(journal.Backup(slug));
        File.WriteAllText(Path.Combine(journal.Backup(slug), "plugin.cfg"), $"[plugin]\nname=\"{slug}\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n");
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
    public void UpdatesAreNotResumedWhileTheStateFilesCannotBeRead()
    {
        var journal = Journal(UpdatePhase.InterimBuilt, Attempt());
        // a git pull left a merge conflict in the shared file, so the markers cannot be read either
        var shared = Path.Combine(_fixture.Root, "addons", "eplugin-state.json");
        File.WriteAllText(shared, "<<<<<<< HEAD\n{}\n=======\n{}\n>>>>>>> origin/main\n");
        var store = new PluginStateStore(shared, new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(store.IsReadOnly).IsTrue();
        Assertions.AssertObject(UpdateJournal.Load(journal.Directory).State).IsEqual(UpdatePhase.InterimBuilt);
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertThat(_updates.Toggles).IsEmpty();
    }

    [TestCase]
    public void UpdatesResumedAtStartupScanTheFilesystemOnce()
    {
        // the editor's own update host, whose scans the editor drops while one runs
        _fixture.Global.UpdateHost = null!;
        _fixture.AddPlainPlugin("other", "2.0.0");
        _fixture.Host.Enabled.Add("other");
        Journal(UpdatePhase.Swapped, Attempt());
        Journal(UpdatePhase.Swapped, Attempt("other"), slug: "other");
        var scans = _fixture.Host.Scans;

        _fixture.Global.ResumeUpdates();

        Assertions.AssertString(Installed).Contains("1.0.0");
        Assertions.AssertString(File.ReadAllText(Path.Combine(_fixture.Addon("other"), "plugin.cfg"))).Contains("1.0.0");
        Assertions.AssertInt(_fixture.Host.Scans - scans).IsEqual(1);
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
    public void StagingFoldersWithoutAJournalAreDeletedOnceTheyAreOld()
    {
        // The manager deletes a batch it staged but did not install; an assembly reload or a crash forgets it.
        var updates = Path.Combine(_fixture.Root, ".godot", "eplugin", "updates");
        var abandoned = Path.Combine(updates, "abandoned");
        var staging = Path.Combine(updates, "staging-now");
        foreach (var folder in new[] { abandoned, staging })
        {
            Directory.CreateDirectory(Path.Combine(folder, "staging", "tool"));
            File.WriteAllText(Path.Combine(folder, "staging", "tool", "plugin.cfg"), "");
        }
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-1));
        var kept = Journal(UpdatePhase.RolledBack, Guid.NewGuid());
        Directory.SetLastWriteTimeUtc(kept.Directory, DateTime.UtcNow.AddDays(-1));

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(abandoned)).IsFalse();
        Assertions.AssertBool(Directory.Exists(staging)).IsTrue();
        Assertions.AssertBool(Directory.Exists(kept.Directory)).IsTrue();
    }

    [TestCase]
    public void AVerifiedUpdateIsCommittedAndOneThatLostItsMarkerIsLeftForRepair()
    {
        // lost before the acknowledgement: the shared state does not hold its versions
        var unmarked = Journal(UpdatePhase.Verified, Guid.NewGuid());
        _fixture.Global.ResumeUpdates();

        Assertions.AssertObject(UpdateJournal.Load(unmarked.Directory).State).IsEqual(UpdatePhase.Verified);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("Update marker is missing"))).IsTrue();
        Directory.Delete(unmarked.Directory, true);

        var verified = Journal(UpdatePhase.Verified, Attempt());

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(verified.Directory)).IsFalse();
        Assertions.AssertString(_fixture.Store.GetShared("tool")!.Version).IsEqual("2.0.0");
        Assertions.AssertObject(_fixture.Store.LastCompletedAttemptId).IsEqual(verified.AttemptId);
        Assertions.AssertInt(_fixture.Store.LocalAttempts.Count).IsEqual(0);
    }

    [TestCase]
    public void AnAcknowledgedUpdateWhoseJournalWasNotCommittedIsFinishedAndNoLongerBlocksUpdates()
    {
        // acknowledged, then the editor stopped (or the commit could not save the journal) before it was committed;
        // a later group's commit took lastCompletedAttemptId
        var attempt = Attempt();
        var acknowledged = Journal(UpdatePhase.Verified, attempt);
        Assertions.AssertBool(_fixture.Store.TryAcknowledgeVersions(attempt, new Dictionary<string, string> { ["tool"] = "2.0.0" })).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("other", "1.0.0", PersistedPluginState.Activated, out var later)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryAcknowledgeVersions(later, new Dictionary<string, string> { ["other"] = "2.0.0" })).IsTrue();
        var journals = new UpdateJournals(Path.Combine(_fixture.Root, ".godot", "eplugin", "updates"));
        Assertions.AssertBool(journals.Read().Any(j => j.IsActive)).IsTrue();

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(acknowledged.Directory)).IsFalse();
        Assertions.AssertBool(journals.Read().Any(j => j.IsActive)).IsFalse();
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Update marker is missing"))).IsFalse();
    }

    [TestCase]
    public void AnUpdateTheEditorStoppedRightAfterAcknowledgingFinishesItsCommit()
    {
        var attempt = Attempt();
        var acknowledged = Journal(UpdatePhase.Verified, attempt);
        Assertions.AssertBool(_fixture.Store.TryAcknowledgeVersions(attempt, new Dictionary<string, string> { ["tool"] = "2.0.0" })).IsTrue();
        Assertions.AssertObject(_fixture.Store.LastCompletedAttemptId).IsEqual(attempt);

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(acknowledged.Directory)).IsFalse();
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertThat(_updates.Toggles).IsEmpty();
        // the rest of the commit ran: the installed revision is recorded, so the update is no longer offered
        Assertions.AssertString(_fixture.Global.UpdateCache!.Revisions["tool"].Version).IsEqual("2.0.0");
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
    public void AJournalWithReadOnlyFilesIsRemoved()
    {
        var attempt = Attempt();
        Assertions.AssertBool(_fixture.Store.TryAcknowledgeVersions(attempt, new Dictionary<string, string> { ["tool"] = "2.0.0" })).IsTrue();
        var journal = Journal(UpdatePhase.Swapped, attempt);
        // version control systems such as Perforce sync files read-only, which Windows refuses to delete
        File.SetAttributes(Path.Combine(journal.Backup("tool"), "plugin.cfg"), FileAttributes.ReadOnly);

        _fixture.Global.ResumeUpdates();

        Assertions.AssertBool(Directory.Exists(journal.Directory)).IsFalse();
    }

    [TestCase]
    public void AJournalWithNullEntriesIsSkippedAndTheOthersResume()
    {
        var broken = Journal(UpdatePhase.Swapped, Guid.NewGuid());
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(broken.Directory, "journal.json")))!;
        json["plugins"]![0]!["preserved"] = null;
        File.WriteAllText(Path.Combine(broken.Directory, "journal.json"), json.ToJsonString());
        var journal = Journal(UpdatePhase.Swapped, Attempt());

        Assertions.AssertThrown(() => UpdateJournal.Load(broken.Directory)).IsInstanceOf<InvalidDataException>();
        _fixture.Global.ResumeUpdates();

        AssertRolledBack(journal, "Interrupted update.");
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
    public void TheJournalOfAFailedUpdateIsFoundByItsAttemptAndOnlyAKeptUpdateIsRolledBackOnRequest()
    {
        var attempt = Attempt();
        Assertions.AssertBool(_fixture.Store.TryFail(attempt, PersistedPluginState.Failed, "update_commit_failed")).IsTrue();
        Journal(UpdatePhase.RolledBack, Guid.NewGuid());
        var journal = Journal(UpdatePhase.Swapped, attempt);

        Assertions.AssertString(_fixture.Global.FailedUpdateJournal("tool")!.Id).IsEqual(journal.Id);
        Assertions.AssertObject(_fixture.Global.FailedUpdateJournal("other")).IsNull();

        // a journal that was not kept with build errors is left alone
        _fixture.Global.RollbackKeptUpdate(journal);

        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("Cannot roll back the kept update"))).IsTrue();
        Assertions.AssertObject(UpdateJournal.Load(journal.Directory).State).IsEqual(UpdatePhase.Swapped);
        Assertions.AssertString(Installed).Contains("2.0.0");
        Assertions.AssertThat(_updates.Toggles).IsEmpty();
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
