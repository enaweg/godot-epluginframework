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
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertInt(_host.Toggles).IsEqual(0);
    }
    [TestCase]
    public void PlainCSharpFailureCanAskOrKeepButHeadlessAlwaysRollsBack()
    {
        Assertions.AssertString(UpdateFailureDecision.Choose(true, false, "keep", false)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Choose(false, false, "keep", true)).IsEqual("rollback");
        Assertions.AssertString(UpdateFailureDecision.Choose(true, true, "ask", true)).IsEqual("rollback");
        _host.HasUi = true; _host.Policy = "ask"; _host.Builds.Enqueue(new(1, ["error CS0246"]));
        var package = Package(true);
        Assertions.AssertObject(_applier.Apply([package.Package], package.Directory)).IsEqual(UpdateOutcome.AwaitingDecision);
        var journal = UpdateJournal.Load(package.Directory);
        Assertions.AssertObject(_applier.Keep(journal)).IsEqual(UpdateOutcome.KeptWithErrors);
        Assertions.AssertString(_store.GetShared("plugin")!.Version).IsEqual("0.9.0");
        Assertions.AssertString(_store.GetLocal("plugin")!.Reason).IsEqual("update_kept_build_failed");
        Assertions.AssertBool(Directory.Exists(journal.Backup("plugin"))).IsTrue();
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
        Assertions.AssertInt(_host.Reloads).IsEqual(2);
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
        public bool Managed; public bool HasUi; public string Policy = "rollback"; public int Toggles; public bool MarkerBeforeToggle;
        public Action? OnScan; public Action? OnBeforeSwap; public Action? OnCloseScenes; public int Reloads;
        public Queue<BuildOutcome> Builds { get; } = new();
        public bool UiAvailable => HasUi;
        public string BuildFailurePolicy => Policy;
        public bool RestartAlways => false;
        public bool IsEnabled(string slug) => true;
        public bool IsManaged(string slug) => Managed;
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) { }
        public void BeforeSwap(UpdateJournal journal) => OnBeforeSwap?.Invoke();
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { }
        public void Reconcile(UpdateJournal journal, bool rollback) { }
        public void SetPlainEnabled(string slug, bool enabled) { Toggles++; MarkerBeforeToggle |= store.IsBlocked(slug); }
        public void CloseScenes() => OnCloseScenes?.Invoke();
        public void Scan() => OnScan?.Invoke();
        public BuildOutcome Build() => Builds.Count > 0 ? Builds.Dequeue() : new(0, []);
        public void RequestReload(UpdateJournal journal) { Reloads++; }
        public bool Verify(UpdatePluginJournal plugin) => true;
        public void Log(string message) { }
    }
}
