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
    [AfterTest] public void Cleanup() { AddonFiles.DeleteTree(_root); }
    private (ValidatedPackage Package, string Directory) Package(bool csharp = false)
    {
        var directory = Path.Combine(_root, ".godot/eplugin/updates/test-id");
        var stage = Path.Combine(directory, "staging/plugin"); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, "v2.0.0", new ZipPackageRef("https://example.org/package.zip", "plugin"));
        Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
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
    public void ARollbackRestoresReadOnlyProjectAndPluginFiles()
    {
        // version control systems such as Perforce sync files read-only
        File.SetAttributes(Path.Combine(_root, "Game.csproj"), FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(_root, "addons/plugin/old.txt"), FileAttributes.ReadOnly);
        _host.Managed = true;
        _host.Builds.Enqueue(new(1, ["compile error"]));
        _host.Builds.Enqueue(new(0, []));
        var package = Package(true);

        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);

        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "Game.csproj"))).IsEqual("old project");
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "addons/plugin/old.txt"))).IsTrue();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
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
        // a teammate's change of the same plugin's entry, pulled while the update runs
        _host.OnScan = () => File.WriteAllText(_shared, File.ReadAllText(_shared).Replace("\"0.9.0\"", "\"0.9.5\""));
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
    /// <summary>A build error in the project's own code, as the update breaks its use of a plugin API.</summary>
    private BuildOutcome ProjectError(params int[] lines)
    {
        File.WriteAllText(Path.Combine(_root, "Main.cs"), "class Main {}");
        var main = Path.Combine(_root, "Main.cs");
        return new(1, (lines.Length == 0 ? [2] : lines).Select(line => $"{main}({line},5): error CS0117: 'Plug' does not contain a definition for 'Api'").ToArray());
    }
    private BuildOutcome PluginError() => new(1, [$"{Path.Combine(_root, "addons", "plugin", "Plugin.cs")}(1,1): error CS0246: missing type"]);
    /// <summary>An update installed with the batch Update button, with the defaults: ask, keep on project errors.</summary>
    private void Defaults() { _host.Managed = true; _host.HasUi = true; _host.Policy = "ask"; _host.KeepProjectErrors = true; }

    [TestCase]
    public void AnUpdateThatOnlyBreaksProjectCodeIsKeptWhenThePluginsCompileAlone()
    {
        Defaults();
        _host.Builds.Enqueue(ProjectError());
        var package = Package(true);
        var results = _applier.ApplyGroups([[package.Package]], package.Directory, NewDirectory);
        Assertions.AssertObject(results.Single().Outcome).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertBool(results.Single().KeptForProjectCode).IsTrue();
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.CommittedWithErrors);
        Assertions.AssertBool(journal.KeptForProjectCode).IsTrue();
        Assertions.AssertBool(journal.BeforeReconcile).IsTrue();
        Assertions.AssertString(journal.ProjectErrors.Single().File!).IsEqual("Main.cs");
        Assertions.AssertString(journal.Failure!).Contains("1 error");
        Assertions.AssertThat(journal.Builds.Select(b => b.Name).ToArray()).ContainsExactly("interim", "interim-plugins-only");
        // The plugins-only build gets the addons folder, and its work folder is removed afterwards.
        Assertions.AssertThat(_host.PluginsOnlyRuns.Single().PluginRoots).Contains(Path.Combine(_root, "addons"));
        Assertions.AssertBool(Directory.Exists(_host.PluginsOnlyRuns.Single().WorkDirectory)).IsFalse();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_kept_build_failed");
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("0.9.0");
        // The editor keeps the code it runs: no restart into an assembly that does not build.
        Assertions.AssertInt(_host.Reloads).IsEqual(0);
    }

    [TestCase]
    public void AnUpdateFromThePluginsDetailsStillAsksWhenOnlyProjectCodeFails()
    {
        Defaults();
        _host.Builds.Enqueue(ProjectError());
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory, askOnBuildFailure: true)).IsEqual(UpdateOutcome.AwaitingDecision);
        Assertions.AssertBool(UpdateJournal.Load(package.Directory).KeptForProjectCode).IsTrue();
    }

    [TestCase]
    public void AnUpdateIsRolledBackWhenThePluginsDoNotCompileAlone()
    {
        Defaults();
        _host.Builds.Enqueue(ProjectError()); _host.PluginsOnly.Enqueue(PluginError());
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertBool(journal.PluginsBuildAlone == false).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }

    [TestCase]
    public void ErrorsInPluginCodeRollBackWithoutAPluginsOnlyBuild()
    {
        Defaults();
        var project = ProjectError();
        _host.Builds.Enqueue(new(1, [..project.Output, ..PluginError().Output]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertInt(_host.PluginsOnlyRuns.Count).IsEqual(0);
        Assertions.AssertObject(UpdateJournal.Load(package.Directory).FailureCause).IsEqual(BuildFailureCause.PluginCode);
    }

    [TestCase]
    public void ProjectErrorsRollBackWhenTheSettingsOrAHeadlessRunSaySo()
    {
        foreach (var setup in new Action[] { () => _host.Policy = "rollback", () => _host.KeepProjectErrors = false, () => _host.HasUi = false })
        {
            Cleanup(); Setup(); Defaults(); setup();
            _host.Builds.Enqueue(ProjectError());
            var package = Package(true);
            Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);
            Assertions.AssertInt(_host.PluginsOnlyRuns.Count).IsEqual(0);
        }
    }

    [TestCase]
    public void ATransientFailureIsBuiltOnceMore()
    {
        Defaults();
        _host.Builds.Enqueue(new(1, ["C:\\sdk\\Microsoft.Common.CurrentVersion.targets(5034,5): error MSB3027: Could not copy \"Game.dll\"."]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        Assertions.AssertInt(_host.BuildCount).IsEqual(2);
        Assertions.AssertThat(UpdateJournal.Load(package.Directory).Builds.Select(b => b.Name).ToArray()).ContainsExactly("interim", "interim-again");
    }

    [TestCase]
    public void LaterGroupsFailingWithTheSameProjectErrorsAreKeptToo()
    {
        Defaults();
        var package = Package(true);
        var other = Other(package.Directory, true);
        _host.Builds.Enqueue(ProjectError()); _host.Builds.Enqueue(ProjectError());
        var results = _applier.ApplyGroups([[package.Package], [other]], package.Directory, NewDirectory);
        Assertions.AssertThat(results.Select(r => r.Outcome).ToArray()).ContainsExactly(UpdateOutcome.KeptWithErrors, UpdateOutcome.KeptWithErrors);
        Assertions.AssertBool(results.All(r => r.KeptForProjectCode)).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/other/plugin.cfg")).Contains("2.0.0")).IsTrue();
    }

    [TestCase]
    public void AFinalBuildThatOnlyBreaksProjectCodeIsKeptAfterReconciling()
    {
        Defaults();
        var package = Package(true);
        _host.OnPrepareJournal = journal => journal.Recipes["plugin"] = new() { Old = new() { Nugets = [new("Shared", "1.0", null)] }, Applied = new() };
        _host.OnReconcile = journal => journal.Recipes["plugin"].Target = new() { Nugets = [new("Shared", "2.0", null)] };
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        _host.Builds.Enqueue(ProjectError());
        Assertions.AssertObject(_applier.Resume(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.KeptWithErrors);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertBool(journal.KeptForProjectCode).IsTrue();
        Assertions.AssertBool(journal.BeforeReconcile).IsFalse();
        Assertions.AssertThat(journal.Builds.Select(b => b.Name).ToArray()).ContainsExactly("interim", "final", "final-plugins-only");
    }

    [TestCase]
    public void RetryShowsTheProjectErrorsThatAreLeftAndFinishesOnceTheProjectBuilds()
    {
        Defaults();
        _host.Builds.Enqueue(ProjectError(2, 3, 4));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.KeptWithErrors);
        _host.Builds.Enqueue(ProjectError(7));
        Assertions.AssertObject(_applier.Retry(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.KeptWithErrors);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertInt(journal.ProjectErrors.Single().Line).IsEqual(7);
        Assertions.AssertString(journal.Failure!).Contains("1 error");
        // Fixed: the update restarts into the new code and continues with reconciling.
        Assertions.AssertObject(_applier.Retry(journal)).IsEqual(UpdateOutcome.AwaitingReload);
        journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.InterimBuilt);
        Assertions.AssertObject(journal.FailureCause).IsNull();
        Assertions.AssertInt(journal.ProjectErrors.Count).IsEqual(0);
    }

    [TestCase]
    public void AKeptUpdateCanBeRolledBackKeepingTheProjectFilesChangedSince()
    {
        Defaults();
        _host.Builds.Enqueue(ProjectError());
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.KeptWithErrors);
        File.WriteAllText(Path.Combine(_root, "Game.csproj"), "project changed while fixing");
        Assertions.AssertObject(_applier.RollbackKept(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "addons/plugin/old.txt"))).IsTrue();
        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "Game.csproj"))).IsEqual("project changed while fixing");
        Assertions.AssertInt(_host.Rebases).IsEqual(1);
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("0.9.0");
    }

    [TestCase]
    public void ARollbackWhoseBuildFailsStaysBlockedUntilRetryRollsBackAgain()
    {
        _host.Managed = true;
        _host.Builds.Enqueue(new(1, ["compile error"]));
        _host.Builds.Enqueue(new(1, ["restored project does not build"]));
        var package = Package(true);

        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.KeptWithErrors);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.RollingBack);
        Assertions.AssertString(journal.Failure).Contains("rollback failed: Restored project failed to build.");
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_rollback_failed");
        // the files were restored before the build failed
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();

        // the project was repaired; its backup is gone, but the installed version shows the files are restored
        Assertions.AssertObject(_applier.Retry(journal)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertObject(UpdateJournal.Load(package.Directory).State).IsEqual(UpdatePhase.RolledBack);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("0.9.0");
    }

    [TestCase]
    public void ARollbackWithoutTheBackupLeavesTheNewFilesAndAsksForManualRepair()
    {
        _host.HasUi = true; _host.Policy = "ask"; _host.Builds.Enqueue(new(1, ["error CS0246"]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory, askOnBuildFailure: true)).IsEqual(UpdateOutcome.AwaitingDecision);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(_applier.Keep(journal)).IsEqual(UpdateOutcome.KeptWithErrors);
        Directory.Delete(journal.Backup("plugin"), true);

        Assertions.AssertObject(_applier.RollbackKept(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.KeptWithErrors);

        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_rollback_failed");
        Assertions.AssertString(UpdateJournal.Load(package.Directory).Failure).Contains("Backup for plugin is missing");
    }

    [TestCase]
    public void RetryRollsBackAnUpdateWhosePluginsNoLongerVerify()
    {
        // a commit that failed: a teammate's change of the same plugin's entry was pulled while the update ran
        _host.OnScan = () => File.WriteAllText(_shared, File.ReadAllText(_shared).Replace("\"0.9.0\"", "\"0.9.5\""));
        var package = Package();
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_commit_failed");
        _host.OnScan = null;
        _host.Verified = false;

        Assertions.AssertObject(_applier.Retry(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertString(UpdateJournal.Load(package.Directory).Failure).IsEqual("Retry verification failed.");

        // an update kept after reconciling whose project builds now, but whose plugin does not load
        Cleanup(); Setup();
        Defaults();
        package = Package(true);
        _host.OnPrepareJournal = journal => journal.Recipes["plugin"] = new() { Old = new() { Nugets = [new("Shared", "1.0", null)] }, Applied = new() };
        _host.OnReconcile = journal => journal.Recipes["plugin"].Target = new() { Nugets = [new("Shared", "2.0", null)] };
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingReload);
        _host.Builds.Enqueue(ProjectError());
        Assertions.AssertObject(_applier.Resume(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.KeptWithErrors);
        _host.Verified = false;

        Assertions.AssertObject(_applier.Retry(UpdateJournal.Load(package.Directory))).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/plugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }

    [TestCase]
    public void JournalsWithoutTheBuildFailureFieldsStillLoad()
    {
        var directory = Path.Combine(_root, ".godot/eplugin/updates", "older");
        var journal = new UpdateJournal { Id = "older", Directory = directory, AttemptId = Guid.NewGuid() };
        journal.Plugins.Add(new() { Slug = "plugin", OldVersion = "1.0.0", NewVersion = "2.0.0" });
        Directory.CreateDirectory(directory);
        journal.Save(UpdatePhase.CommittedWithErrors);
        var path = Path.Combine(directory, "journal.json");
        foreach (var older in new Action<System.Text.Json.Nodes.JsonObject>[]
                 {
                     // Written by a framework before this feature, or with an empty list written as null.
                     json => { json.Remove("failureCause"); json.Remove("pluginsBuildAlone"); json.Remove("projectErrors"); },
                     json => json["projectErrors"] = null,
                 })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            older(json);
            File.WriteAllText(path, json.ToJsonString());
            var loaded = UpdateJournal.Load(directory);
            Assertions.AssertObject(loaded.FailureCause).IsNull();
            Assertions.AssertInt(loaded.ProjectErrors.Count).IsEqual(0);
            Assertions.AssertBool(loaded.KeptForProjectCode).IsFalse();
        }
    }

    [TestCase]
    public static void ProjectCodeOnlyFailuresKeepUnlessTheSettingRollsBack()
    {
        Assertions.AssertString(UpdateFailureDecision.Policy("ask", false, false, true)).IsEqual("keep");
        Assertions.AssertString(UpdateFailureDecision.Policy("ask", false, true, true)).IsEqual("ask");
        Assertions.AssertString(UpdateFailureDecision.Policy("ask", true, true, true)).IsEqual("keep");
        Assertions.AssertString(UpdateFailureDecision.Policy("keep", true, false, true)).IsEqual("keep");
        Assertions.AssertString(UpdateFailureDecision.Policy("rollback", false, true, true)).IsEqual("rollback");
        // A self-update or a headless run still rolls back.
        Assertions.AssertString(UpdateFailureDecision.Choose(true, "keep", true)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Choose(false, "keep", false)).IsEqual("rollback");
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
        Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
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
    public void RollbackUsesTheNewProjectBackupWhenReplacingItWasInterrupted()
    {
        var package = Package(true);
        var other = Other(package.Directory);
        _host.ManagedSlugs.Add("plugin");
        _host.OnReconcile = journal =>
        {
            if (journal.Plugins.Any(p => p.Slug == "other")) File.WriteAllText(Path.Combine(_root, "Game.csproj"), "project with other");
        };
        _applier.ApplyGroups([[package.Package], [other]], package.Directory, NewDirectory);
        // The editor stopped between moving the old backup aside and moving the complete new one into its place.
        var backup = Path.Combine(package.Directory, "backup-project");
        Directory.Move(backup, backup + ".new");
        Directory.CreateDirectory(backup + ".old");
        File.WriteAllText(Path.Combine(backup + ".old", "Game.csproj"), "project before other");
        Assertions.AssertObject(_applier.Rollback(UpdateJournal.Load(package.Directory), "test")).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "Game.csproj"))).IsEqual("project with other");
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

        // a journal Load refuses is logged and skipped, not thrown at every caller
        var invalid = new UpdateJournal { Id = "d-invalid", Directory = Path.Combine(root, "d-invalid"), AttemptId = Guid.NewGuid(), CreatedUtc = created };
        invalid.Plugins.Add(new() { Slug = "d-invalid", OldVersion = "1.0.0", NewVersion = "not a version" });
        Directory.CreateDirectory(invalid.Directory);
        invalid.Save(UpdatePhase.InterimBuilt);
        var log = new List<string>();
        Assertions.AssertThat(new UpdateJournals(root, log.Add).Read().Select(j => j.Id).ToArray()).ContainsExactly("b-first", "a-second", "c-third");
        Assertions.AssertString(log.Single()).Contains("Invalid plugin in update journal");
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
    [TestCase]
    public void CleanupFailureAfterARollbackDoesNotMarkItAsRollingBackAgain()
    {
        _host.Managed = true;
        _host.Builds.Enqueue(new(1, ["compile error"]));
        _host.Builds.Enqueue(new(0, []));
        var package = Package(true);
        var applier = new UpdateApplier(_root, _store, _host, new ThrowingCache());

        Assertions.AssertObject(applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.RolledBack);

        // a journal left rolling back would restore the project files again at the next start
        Assertions.AssertObject(UpdateJournal.Load(package.Directory).State).IsEqual(UpdatePhase.RolledBack);
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertInt(_host.Reloads).IsEqual(1);
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
        public bool KeepProjectErrors; public Queue<BuildOutcome> PluginsOnly { get; } = new(); public List<PluginsOnlyBuild> PluginsOnlyRuns { get; } = [];
        public bool KeepOnProjectErrors => KeepProjectErrors;
        public BuildOutcome BuildPluginsOnly(PluginsOnlyBuild build)
        {
            PluginsOnlyRuns.Add(build);
            Directory.CreateDirectory(build.WorkDirectory);
            return PluginsOnly.Count > 0 ? PluginsOnly.Dequeue() : new(0, []);
        }
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
        public bool Verified = true;
        public bool Verify(UpdatePluginJournal plugin) => Verified;
        public void Log(string message) { }
    }
}
#endif
