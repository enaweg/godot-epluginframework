#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class PlainPluginObserverTests
{
    private string _directory = null!;
    private string _path = null!;
    private PluginStateStore _store = null!;
    private HashSet<string> _enabled = [];
    private HashSet<string> _owned = [];
    private string? _version;
    private PlainPluginObserver _observer = null!;

    [BeforeTest]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "plain-plugin-test-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "state.json");
        _store = new PluginStateStore(_path, new NullLogger());
        _store.Load();
        _enabled = [];
        _owned = [];
        _version = "1.0.0";
        _observer = new PlainPluginObserver(_store, () => _enabled, _ => _version,
            slug => slug is "managed" or "ePlugin", slug => _owned.Contains(slug), new NullLogger());
    }

    [AfterTest]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestCase]
    public void FirstActivationAndTogglesKeepWorkingVersionAndUnchangedRefreshDoesNotWrite()
    {
        _store.TryCreateBaseline([]);
        _observer.Refresh();
        _enabled.Add("plain");
        _observer.Refresh();
        Assertions.AssertString(_store.GetShared("plain")!.Version).IsEqual("1.0.0");
        var before = File.ReadAllText(_path);
        _observer.Refresh();
        Assertions.AssertString(File.ReadAllText(_path)).IsEqual(before);
        _version = "2.0.0";
        _enabled.Clear();
        _observer.Refresh();
        Assertions.AssertObject(_store.GetShared("plain")!.State).IsEqual(PersistedPluginState.Deactivated);
        _enabled.Add("plain");
        _observer.Refresh();
        Assertions.AssertString(_store.GetShared("plain")!.Version).IsEqual("1.0.0");
        Assertions.AssertObject(_store.GetShared("plain")!.State).IsEqual(PersistedPluginState.Activated);
    }

    [TestCase]
    public void StartupAddsMissingEntriesAndPreservesDiscrepancies()
    {
        _store.TryCreateBaseline([new SharedPluginState("old", "0.9", PersistedPluginState.Deactivated)]);
        _enabled.UnionWith(["old", "plain", "managed", "ePlugin"]);
        _observer.Refresh();
        Assertions.AssertObject(_store.GetShared("old")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertString(_store.GetShared("plain")!.Version).IsEqual("1.0.0");
        Assertions.AssertObject(_store.GetShared("managed")).IsNull();
        Assertions.AssertObject(_store.GetShared("ePlugin")).IsNull();
    }

    [TestCase]
    public void InvalidVersionIsLocalOnlyAndRetryRereadsInstalledVersion()
    {
        _store.TryCreateBaseline([]);
        _enabled.Add("plain");
        _version = " ";
        _observer.Refresh();
        Assertions.AssertObject(_store.GetShared("plain")).IsNull();
        Assertions.AssertString(_store.GetLocal("plain")!.Reason).IsEqual("invalid_plugin_version");
        Assertions.AssertBool(_observer.Retry("plain")).IsFalse();
        _version = "3.0.0";
        Assertions.AssertBool(_observer.Retry("plain")).IsTrue();
        Assertions.AssertBool(_store.IsBlocked("plain")).IsFalse();
        Assertions.AssertString(_store.GetShared("plain")!.Version).IsEqual("3.0.0");
    }

    /// <summary>Disables plain, whose write is refused: a git pull left a merge conflict in the shared file.</summary>
    private string DisableWhileTheFileHasAConflict()
    {
        _store.TryCreateBaseline([]);
        _enabled.Add("plain");
        _observer.Refresh();
        var resolved = File.ReadAllText(_path);
        File.WriteAllText(_path, "<<<<<<< HEAD\n" + resolved + "=======\n>>>>>>> origin/main\n");
        _enabled.Clear();
        _observer.Refresh();
        Assertions.AssertString(_store.GetLocal("plain")!.Reason).IsEqual("state_commit_failed");
        return resolved;
    }

    [TestCase]
    public void AFailedWriteIsRetriedOnceTheFileCanBeWrittenAgain()
    {
        File.WriteAllText(_path, DisableWhileTheFileHasAConflict());

        // not toggled since
        _observer.Refresh();

        Assertions.AssertBool(_store.IsBlocked("plain")).IsFalse();
        Assertions.AssertObject(_store.GetShared("plain")!.State).IsEqual(PersistedPluginState.Deactivated);
    }

    [TestCase]
    public void AFailedWriteIsRetriedOnRequestAfterARestart()
    {
        File.WriteAllText(_path, DisableWhileTheFileHasAConflict());
        var store = new PluginStateStore(_path, new NullLogger());
        store.Load();
        var observer = new PlainPluginObserver(store, () => _enabled, _ => _version,
            slug => slug is "managed" or "ePlugin", slug => _owned.Contains(slug), new NullLogger());

        observer.Refresh();
        Assertions.AssertBool(store.IsBlocked("plain")).IsTrue();
        Assertions.AssertBool(observer.Retry("plain")).IsTrue();

        Assertions.AssertBool(store.IsBlocked("plain")).IsFalse();
        Assertions.AssertObject(store.GetShared("plain")!.State).IsEqual(PersistedPluginState.Deactivated);
    }

    [TestCase]
    public void AnAttemptSharedWithOtherPluginsIsNotRetriedAsAPlainWrite()
    {
        _store.TryCreateBaseline([]);
        Assertions.AssertBool(_store.TryBeginAttempt("app", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_store.TryAddParticipant(id, "plain", "1.0.0", PersistedPluginState.Activated)).IsTrue();
        Assertions.AssertBool(_store.TryFail(id, PersistedPluginState.Failed, "state_commit_failed")).IsTrue();

        Assertions.AssertBool(_observer.Retry("plain")).IsFalse();
        Assertions.AssertBool(_store.IsBlocked("app")).IsTrue();
    }

    [TestCase]
    public void OwnedAndBlockedPluginsAreNotWritten()
    {
        _store.TryCreateBaseline([]);
        _store.TryBeginAttempt("blocked", "1.0", PersistedPluginState.Activated, out _);
        var before = File.ReadAllText(_path);
        _enabled.UnionWith(["owned", "blocked"]);
        _owned.Add("owned");
        _observer.Refresh();
        Assertions.AssertString(File.ReadAllText(_path)).IsEqual(before);
        Assertions.AssertObject(_store.GetShared("owned")).IsNull();
        Assertions.AssertObject(_store.GetShared("blocked")).IsNull();
    }

    [TestCase]
    public void APluginRecordedAsActivatedButMissingAtStartupIsReportedAndKept()
    {
        var log = new List<string>();
        _store.TryCreateBaseline([new SharedPluginState("gone", "1.0", PersistedPluginState.Activated)]);
        var observer = new PlainPluginObserver(_store, () => _enabled, _ => _version, _ => false, _ => false, new RecordingLogger(log, "observer"));

        observer.Refresh();

        Assertions.AssertThat(log).ContainsExactly(
            "warn: observer: Plugin gone is disabled or missing but recorded as activated; persisted state was preserved.");
        Assertions.AssertObject(_store.GetShared("gone")!.State).IsEqual(PersistedPluginState.Activated);
    }

    [TestCase]
    public void APluginEnabledLaterWithoutVersionIsRecordedAsInvalid()
    {
        _store.TryCreateBaseline([]);
        _observer.Refresh();
        _version = "";

        _enabled.Add("plain");
        _observer.Refresh();

        Assertions.AssertObject(_store.GetShared("plain")).IsNull();
        Assertions.AssertString(_store.GetLocal("plain")!.Reason).IsEqual("invalid_plugin_version");
    }

    [TestCase]
    public void AStateFileEditedOnDiskIsNotOverwritten()
    {
        var log = new List<string>();
        _store.TryCreateBaseline([]);
        var observer = new PlainPluginObserver(_store, () => _enabled, _ => _version, _ => false, _ => false, new RecordingLogger(log, "observer"));
        observer.Refresh();
        // an unresolved merge conflict
        File.WriteAllText(_path, "<<<<<<< HEAD\n");
        var edited = File.ReadAllText(_path);

        _enabled.Add("plain");
        observer.Refresh();

        Assertions.AssertString(File.ReadAllText(_path)).IsEqual(edited);
        Assertions.AssertBool(log.Contains("error: observer: Cannot record observed state for plain plugin plain; persisted state was preserved.")).IsTrue();
        Assertions.AssertBool(_store.IsBlocked("plain")).IsTrue();
    }
}
#endif
