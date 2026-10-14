#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateTransactionTests
{
    private string _root = null!;
    private PluginStateStore _store = null!;
    private FakeHost _host = null!;
    private UpdateApplier _applier = null!;
    private string _shared = null!;
    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "update-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "addons/plugin"));
        File.WriteAllText(Path.Combine(_root, "addons/plugin/plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(_root, "addons/plugin/old.txt"), "old payload");
        File.WriteAllText(Path.Combine(_root, "Game.csproj"), "old project");
        _shared = Path.Combine(_root, "addons/eplugin-state.json");
        _store = new(_shared, new NullLogger()); _store.Load();
        _store.TryCreateBaseline([new("plugin", "0.9.0", PersistedPluginState.Activated)]);
        _host = new(_store);
        _applier = new(_root, _store, _host, new MemoryStore());
    }
    [AfterTest] public void Cleanup() { Directory.Delete(_root, true); }
    private (ValidatedPackage Package, string Directory) Package(bool csharp = false)
    {
        var directory = Path.Combine(_root, ".godot/eplugin/updates/test-id");
        var stage = Path.Combine(directory, "staging/plugin"); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, "v2.0.0", new ZipPackageRef("https://example.org/package.zip", "plugin"));
        SemVer.TryParse("2.0.0", out var version);
        return (new(candidate, stage, version, null, csharp, []), directory);
    }
    [TestCase]
    public void PlainCommitWritesMarkerBeforeToggleAndAcknowledgesActualInstalledVersion()
    {
        var package = Package();
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertBool(_host.MarkerBeforeToggle).IsTrue();
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("2.0.0");
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertBool(Directory.Exists(package.Directory)).IsFalse();
        // updates never show a welcome page, so the updated plugin's page counts as shown
        Assertions.AssertString(_store.GetWelcome("plugin")!.Version).IsEqual("2.0.0");
    }
    [TestCase]
    public void ScenesAreClosedAfterMarkerAndBeforeAnyAddonIsSwapped()
    {
        var closed = 0;
        _host.OnCloseScenes = () =>
        {
            closed++;
            Assertions.AssertBool(_store.IsBlocked("plugin")).IsTrue();
            Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
            Assertions.AssertInt(_host.Toggles).IsEqual(0);
        };
        var package = Package();
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertInt(closed).IsEqual(1);
    }
    [TestCase]
    public void InterimFailureRestoresFilesAndProjectWithoutChangingSharedIndex()
    {
        _host.Managed = true;
        _host.Builds.Enqueue(new(1, ["compile error"]));
        _host.Builds.Enqueue(new(0, []));
        var shared = File.ReadAllText(_shared);
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "addons/plugin/old.txt"))).IsTrue();
        Assertions.AssertString(File.ReadAllText(_shared)).IsEqual(shared);
        Assertions.AssertBool(_store.IsWelcomeShown("plugin")).IsFalse();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertInt(_host.Toggles).IsEqual(0);
    }
    [TestCase]
    public void BuildFailureAsksOnlyForAnUpdateFromTheDetailsAndHeadlessOrSelfUpdatesRollBack()
    {
        Assertions.AssertString(UpdateFailureDecision.Choose(false, "keep", false)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Choose(true, "ask", true)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Choose(false, "ask", true)).IsEqual("ask");
        Assertions.AssertString(UpdateFailureDecision.Choose(false, "keep", true)).IsEqual("keep");
        // installing all updates never asks, a group of a batch never asks or keeps
        Assertions.AssertString(UpdateFailureDecision.Policy("ask", false, false)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Policy("ask", false, true)).IsEqual("ask");
        Assertions.AssertString(UpdateFailureDecision.Policy("rollback", false, true)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Policy("keep", false, false)).IsEqual("keep");
        Assertions.AssertString(UpdateFailureDecision.Policy("keep", true, true)).IsEqual("rollback");
        _host.HasUi = true; _host.Policy = "ask"; _host.Builds.Enqueue(new(1, ["error CS0246"]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory, askOnBuildFailure: true)).IsEqual(UpdateOutcome.AwaitingDecision);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertBool(journal.AskOnBuildFailure).IsTrue();
        Assertions.AssertObject(_applier.Keep(journal)).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("0.9.0");
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_kept_build_failed");
        Assertions.AssertBool(Directory.Exists(journal.Backup("plugin"))).IsTrue();
    }
    [TestCase]
    public void InstallingAllUpdatesRollsBackABuildFailureWithoutAsking()
    {
        _host.HasUi = true; _host.Policy = "ask"; _host.Builds.Enqueue(new(1, ["error CS0246"]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }
    [TestCase]
    public void KeptInterimFailureResumesWithReconcilingOnceRetryBuilds()
    {
        _host.Managed = true; _host.HasUi = true; _host.Policy = "ask";
        _host.OnPrepareJournal = journal => journal.Recipes["plugin"] = new() { Old = new(), Applied = new() };
        _host.OnReconcile = journal => journal.Recipes["plugin"].Target = new() { Nugets = [new("Needed", "1.0", null)] };
        _host.Builds.Enqueue(new(1, ["error CS0246"]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory, askOnBuildFailure: true)).IsEqual(UpdateOutcome.AwaitingDecision);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertBool(journal.BeforeReconcile).IsTrue();
        Assertions.AssertObject(_applier.Keep(journal)).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        // still broken: the update stays kept
        _host.Builds.Enqueue(new(1, ["error CS0246"]));
        Assertions.AssertObject(_applier.Retry(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_kept_build_failed");
        // the user fixed the project in the meantime; a rollback from here on keeps that fix
        File.WriteAllText(Path.Combine(_root, "Game.csproj"), "fixed project");
        Assertions.AssertObject(_applier.Retry(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertInt(_host.Rebases).IsEqual(1);
        Assertions.AssertInt(_host.Reloads).IsEqual(1);
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("attempt_in_progress");
        journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.InterimBuilt);
        Assertions.AssertBool(journal.BeforeReconcile).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(package.Directory, "backup-project/Game.csproj"))).IsEqual("fixed project");
        // after the restart the update finishes as usual: reconcile, final build, commit
        Assertions.AssertObject(_applier.Resume(journal)).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("2.0.0");
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }
    [TestCase]
    public void AcknowledgeRequiresWholeAttemptAndAbandonLeavesSharedUntouched()
    {
        var before = File.ReadAllText(_shared);
        _store.TryBeginAttempt("plugin", "1.0.0", PersistedPluginState.Activated, out var id);
        _store.TryAddParticipant(id, "other", "1.0", PersistedPluginState.Activated);
        Assertions.AssertBool(_store.TryAcknowledgeVersions(id, new Dictionary<string,string> { ["plugin"] = "2.0" })).IsFalse();
        Assertions.AssertBool(_store.TryAbandonAttempt(id)).IsTrue();
        Assertions.AssertString(File.ReadAllText(_shared)).IsEqual(before);
    }
    [TestCase]
    public void ExternalSharedChangePreventsCommitAndPreservesHealthyFiles()
    {
        _host.OnScan = () => File.AppendAllText(_shared, " ");
        var package = Package();
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_commit_failed");
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(package.Directory, "backup/plugin"))).IsTrue();
    }
    [TestCase]
    public void JournalAcceptsUnknownFieldsButRejectsFutureSchemaAndUnsafeSlugs()
    {
        var package = Package(true); _host.Managed = true;
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        var path = Path.Combine(package.Directory, "journal.json");
        var json = File.ReadAllText(path); File.WriteAllText(path, json.Replace("\"schema\": 1", "\"unknown\": 42, \"schema\": 1"));
        Assertions.AssertInt(UpdateJournal.Load(package.Directory).Plugins.Count).IsEqual(1);
        File.WriteAllText(path, json.Replace("\"schema\": 1", "\"schema\": 99"));
        var refused = false; try { UpdateJournal.Load(package.Directory); } catch (InvalidDataException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();
    }
    [TestCase]
    public void SelfUpdateQuiescesBeforeSwapAndRestartsWithoutDisablingFramework()
    {
        var package = Package(true);
        Directory.Move(Path.Combine(_root, "addons/plugin"), Path.Combine(_root, "addons/ePlugin"));
        Directory.Move(package.Package.StagingDir, Path.Combine(package.Directory, "staging/ePlugin"));
        var self = package.Package with { Candidate = package.Package.Candidate with { Slug = "ePlugin" }, StagingDir = Path.Combine(package.Directory, "staging/ePlugin") };
        _host.Managed = true;
        _host.OnBeforeSwap = () =>
        {
            Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/ePlugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
            Assertions.AssertBool(_store.IsBlocked("ePlugin")).IsTrue();
        };
        Assertions.AssertObject(_applier.Apply([self], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertInt(_host.Reloads).IsEqual(1);
        Assertions.AssertObject(_applier.Resume(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertInt(_host.Toggles).IsEqual(0);
        // The recipe is unchanged, so the editor keeps running the interim build.
        Assertions.AssertInt(_host.Reloads).IsEqual(1);
    }
    [TestCase]
    public void ManagedUpdateRestartsOnceWhenReconcilingLeavesTheBuildUnchanged()
    {
        var package = Package(true); _host.Managed = true;
        _host.OnPrepareJournal = journal => journal.Recipes["plugin"] = new() { Old = new() { Nugets = [new("Shared", "1.0", null)] }, Applied = new() };
        _host.OnReconcile = journal => journal.Recipes["plugin"].Target = new() { Nugets = [new("Shared", "1.0", null)], Autoloads = [new("Global", "global.gd")] };
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertObject(_applier.Resume(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertInt(_host.BuildCount).IsEqual(1);
        Assertions.AssertInt(_host.Reloads).IsEqual(1);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("2.0.0");
    }
    [TestCase]
    public void ManagedUpdateBuildsAndRestartsAgainWhenReconcilingChangesTheBuild()
    {
        foreach (var bridged in new[] { false, true })
        {
            Cleanup(); Setup();
            var package = Package(true); _host.Managed = true;
            _host.OnPrepareJournal = journal => journal.Recipes["plugin"] = new() { Old = new() { Nugets = [new("Shared", "1.0", null)] }, Applied = new() };
            _host.OnBridge = plugin => { if (bridged) plugin.Preserved.Add("src"); };
            _host.OnReconcile = journal => journal.Recipes["plugin"].Target = new() { Nugets = [new("Shared", bridged ? "1.0" : "2.0", null)] };
            Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
            Assertions.AssertObject(_applier.Resume(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.Completed);
            Assertions.AssertInt(_host.BuildCount).IsEqual(2);
            Assertions.AssertInt(_host.Reloads).IsEqual(2);
        }
    }
    /// <summary>A second plugin "other" 1.0.0 staged as 2.0.0 into the batch folder of <see cref="Package"/>.</summary>
    private ValidatedPackage Other(string directory, bool csharp = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "addons/other"));
        File.WriteAllText(Path.Combine(_root, "addons/other/plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(_root, "addons/other/old.txt"), "old payload");
        var stage = Path.Combine(directory, "staging/other"); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
        var candidate = new UpdateCandidate("other", "Other", "1.0.0", "2.0.0", "https://example.org/releases", null, "v2.0.0", new ZipPackageRef("https://example.org/other.zip", "other"));
        SemVer.TryParse("2.0.0", out var version);
        return new(candidate, stage, version, null, csharp, []);
    }
    private string NewDirectory() => Path.Combine(_root, ".godot/eplugin/updates", "group-" + Guid.NewGuid().ToString("N")[..8]);
    [TestCase]
    public void GroupedBatchRollsBackOnlyTheFailingGroupWithoutAsking()
    {
        var package = Package(true);
        var other = Other(package.Directory, true);
        // Asking would stop the batch; a group's failure always rolls back.
        _host.HasUi = true; _host.Policy = "ask";
        _host.Builds.Enqueue(new(0, [])); _host.Builds.Enqueue(new(1, ["error CS0246"])); _host.Builds.Enqueue(new(0, []));
        var started = new List<int>();
        var results = _applier.ApplyGroups([[package.Package], [other]], package.Directory, NewDirectory, false, (index, _) => started.Add(index));
        Assertions.AssertThat(started).ContainsExactly(0, 1);
        Assertions.AssertObject(results[0].Outcome).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertObject(results[1].Outcome).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(results[1].Failure).IsEqual("Build failed.");
        Assertions.AssertString(results[1].Directory).IsNotEqual(package.Directory);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/other/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "addons/other/old.txt"))).IsTrue();
        Assertions.AssertBool(_store.IsBlocked("plugin")).IsTrue();
        Assertions.AssertBool(_store.IsBlocked("other")).IsFalse();
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.InterimBuilt);
        Assertions.AssertInt(journal.Group ?? -1).IsEqual(0);
    }
    [TestCase]
    public void RefusedGroupIsSkippedAndItsStagingRemoved()
    {
        var package = Package();
        var other = Other(package.Directory);
        // Changed since the update check, so the second group is refused before its transaction begins.
        File.WriteAllText(Path.Combine(_root, "addons/other/plugin.cfg"), Config("1.5.0"));
        var results = _applier.ApplyGroups([[package.Package], [other]], package.Directory, NewDirectory);
        Assertions.AssertObject(results[0].Outcome).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertObject(results[1].Outcome).IsNull();
        Assertions.AssertBool(Directory.Exists(results[1].Directory)).IsFalse();
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("2.0.0");
    }
    [TestCase]
    public void FinishedGroupRebasesTheBackupsOfAPendingGroup()
    {
        var package = Package(true);
        var other = Other(package.Directory);
        _host.ManagedSlugs.Add("plugin");
        _host.OnPrepareJournal = journal => journal.Recipes["lib"] = new() { Old = new() { Nugets = [new("Shared", "1.0", null)] }, Applied = new() { Nugets = [new("Shared", "1.0", null)] } };
        _host.OnReconcile = journal =>
        {
            if (journal.Plugins.All(p => p.Slug != "other")) return;
            // The plain group changes the project and a shared recipe while the managed group waits for its restart.
            File.WriteAllText(Path.Combine(_root, "Game.csproj"), "project with other");
            journal.Recipes["lib"].Applied = new() { Nugets = [new("Shared", "2.0", null)] };
        };
        var results = _applier.ApplyGroups([[package.Package], [other]], package.Directory, NewDirectory);
        Assertions.AssertObject(results[0].Outcome).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertObject(results[1].Outcome).IsEqual(UpdateOutcome.Completed);
        var pending = UpdateJournal.Load(package.Directory);
        Assertions.AssertString(pending.Recipes["lib"].Old.Nugets.Single().Version).IsEqual("2.0");
        Assertions.AssertString(File.ReadAllText(Path.Combine(package.Directory, "backup-project/Game.csproj"))).IsEqual("project with other");
        // Rolling the managed group back no longer undoes the plain group's project change.
        Assertions.AssertObject(_applier.Rollback(pending, "test")).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "Game.csproj"))).IsEqual("project with other");
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/other/plugin.cfg")).Contains("2.0.0")).IsTrue();
    }
    [TestCase]
    public void JournalsAreReadInTheOrderTheirGroupsWereApplied()
    {
        var root = Path.Combine(_root, ".godot/eplugin/updates");
        var created = DateTimeOffset.UtcNow;
        foreach (var (name, group) in new[] { ("a-second", 1), ("b-first", 0), ("c-third", 2) })
        {
            var journal = new UpdateJournal { Id = name, Directory = Path.Combine(root, name), AttemptId = Guid.NewGuid(), CreatedUtc = created, Group = group };
            journal.Plugins.Add(new() { Slug = name, OldVersion = "1.0.0", NewVersion = "2.0.0" });
            Directory.CreateDirectory(journal.Directory);
            journal.Save(UpdatePhase.InterimBuilt);
        }
        Assertions.AssertThat(new UpdateJournals(root).Read().Select(j => j.Id).ToArray()).ContainsExactly("b-first", "a-second", "c-third");
    }
    [TestCase]
    public void CleanupFailureAfterAcknowledgementNeverRollsBackWorkingFiles()
    {
        var package = Package();
        var applier = new UpdateApplier(_root, _store, _host, new ThrowingCache());
        Assertions.AssertObject(applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("2.0.0");
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }
    private sealed class ThrowingCache : IUpdateStateStore { public UpdateCache State { get; } = new(); public void Save() => throw new IOException("cleanup failure"); }
    private static string Config(string version) => $"[plugin]\nname=\"Plugin\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n";
    private sealed class MemoryStore : IUpdateStateStore { public UpdateCache State { get; } = new(); public void Save() { } }
    private sealed class FakeHost(PluginStateStore store) : IUpdateHost
    {
        public bool Managed; public HashSet<string> ManagedSlugs { get; } = []; public bool HasUi; public string Policy = "rollback"; public int Toggles; public bool MarkerBeforeToggle;
        public Action? OnScan; public Action? OnBeforeSwap; public Action? OnCloseScenes; public int Reloads; public int BuildCount; public int Rebases;
        public Action<UpdateJournal>? OnPrepareJournal; public Action<UpdatePluginJournal>? OnBridge; public Action<UpdateJournal>? OnReconcile;
        public Queue<BuildOutcome> Builds { get; } = new();
        public bool UiAvailable => HasUi;
        public string BuildFailurePolicy => Policy;
        public bool RestartAlways => false;
        public bool IsEnabled(string slug) => true;
        public bool IsManaged(string slug) => Managed || ManagedSlugs.Contains(slug);
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) => OnPrepareJournal?.Invoke(journal);
        public void BeforeSwap(UpdateJournal journal) => OnBeforeSwap?.Invoke();
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { OnBridge?.Invoke(plugin); journal.Save(); }
        public void Reconcile(UpdateJournal journal, bool rollback) { if (!rollback) OnReconcile?.Invoke(journal); }
        public void RebaseRecipes(UpdateJournal journal) => Rebases++;
        public void SetPlainEnabled(string slug, bool enabled) { Toggles++; MarkerBeforeToggle |= store.IsBlocked(slug); }
        public void CloseScenes() => OnCloseScenes?.Invoke();
        public void Scan() => OnScan?.Invoke();
        public BuildOutcome Build() { BuildCount++; return Builds.Count > 0 ? Builds.Dequeue() : new(0, []); }
        public void RequestReload(UpdateJournal journal) { Reloads++; }
        public bool Verify(UpdatePluginJournal plugin) => true;
        public void Log(string message) { }
    }
}
#endif
