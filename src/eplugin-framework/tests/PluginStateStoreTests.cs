using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class PluginStateStoreTests
{
    private readonly List<string> _directories = [];

    [AfterTest]
    public void Cleanup()
    {
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        _directories.Clear();
    }

    private (PluginStateStore Store, string Path) NewStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var path = Path.Combine(directory, "eplugin-state.json");
        var store = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(store.Load()).IsTrue();
        return (store, path);
    }

    [TestCase]
    public void FailedAttemptSurvivesReloadWithoutChangingSharedState()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.2.3-beta.1", PersistedPluginState.Activated)
        ])).IsTrue();
        var sharedBefore = File.ReadAllBytes(path);

        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "2.0.0", PersistedPluginState.Deactivated,
            out var id)).IsTrue();
        Assertions.AssertBool(store.TryFail(id, PersistedPluginState.Failed, "remove_package_failed")).IsTrue();

        Assertions.AssertBool(sharedBefore.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.IsBlocked("sample_plugin")).IsTrue();
        Assertions.AssertString(reloaded.GetShared("sample_plugin")!.Version).IsEqual("1.2.3-beta.1");
        Assertions.AssertObject(reloaded.GetLocal("sample_plugin")!.State).IsEqual(PersistedPluginState.Failed);
    }

    [TestCase]
    public void SuccessfulAttemptAdvancesStateButKeepsAcknowledgedVersion()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0.0", PersistedPluginState.Deactivated)
        ])).IsTrue();

        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "2.0.0", PersistedPluginState.Activated,
            out var id)).IsTrue();
        Assertions.AssertBool(store.TryComplete(id, [
            new SharedPluginState("sample_plugin", "2.0.0", PersistedPluginState.Activated)
        ])).IsTrue();

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertObject(reloaded.GetShared("sample_plugin")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertString(reloaded.GetShared("sample_plugin")!.Version).IsEqual("1.0.0");
        Assertions.AssertBool(File.Exists(path + ".user")).IsFalse();
    }

    [TestCase]
    public void CompletionRequiresEveryParticipant()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        var sharedBefore = File.ReadAllBytes(path);

        Assertions.AssertBool(store.TryBeginAttempt("dependency", "1.0", PersistedPluginState.Activated,
            out var id)).IsTrue();
        Assertions.AssertBool(store.TryAddParticipant(id, "consumer", "1.0", PersistedPluginState.Activated)).IsTrue();
        Assertions.AssertBool(store.TryComplete(id, [
            new SharedPluginState("dependency", "1.0", PersistedPluginState.Activated)
        ])).IsFalse();

        Assertions.AssertBool(sharedBefore.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        Assertions.AssertBool(File.Exists(path + ".user")).IsTrue();
    }

    [TestCase]
    public void InvalidSharedFileIsPreservedAndBlocksWrites()
    {
        var (store, path) = NewStore();
        File.WriteAllText(path, "{\"schemaVersion\":42,\"plugins\":[]}");
        var original = File.ReadAllBytes(path);

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsFalse();
        Assertions.AssertBool(reloaded.IsReadOnly).IsTrue();
        Assertions.AssertBool(reloaded.TryCreateBaseline([])).IsFalse();
        Assertions.AssertBool(original.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
    }

    [TestCase]
    public void ExternalEditPreventsFinalization()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated,
            out var id)).IsTrue();

        File.AppendAllText(path, " ");
        var edited = File.ReadAllBytes(path);
        Assertions.AssertBool(store.TryComplete(id, [
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsFalse();

        Assertions.AssertBool(edited.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        Assertions.AssertBool(File.Exists(path + ".user")).IsTrue();
    }

    [TestCase]
    public void CompletedAttemptLeftInLocalFileIsClearedAfterReload()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated,
            out var id)).IsTrue();

        // Simulate a crash after the shared atomic write and before local cleanup.
        var shared = File.ReadAllText(path).Replace(
            "\"lastCompletedAttemptId\": null",
            $"\"lastCompletedAttemptId\": \"{id}\"");
        File.WriteAllText(path, shared);

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.IsBlocked("sample_plugin")).IsFalse();
        Assertions.AssertBool(File.Exists(path + ".user")).IsFalse();
    }

    [TestCase]
    public void UnfinishedAtomicWriteIsRemovedWithoutChangingLastSharedState()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        var original = File.ReadAllBytes(path);
        var abandoned = path + ".tmp-abandoned";
        File.WriteAllText(abandoned, "truncated");

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(File.Exists(abandoned)).IsFalse();
        Assertions.AssertBool(original.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
    }

    [TestCase]
    public void CorruptLocalJournalIsPreservedAndDoesNotReplaceSharedHistory()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        var shared = File.ReadAllBytes(path);
        File.WriteAllText(path + ".user", "{invalid json");

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsFalse();
        Assertions.AssertBool(reloaded.IsReadOnly).IsTrue();
        Assertions.AssertBool(reloaded.TryBeginAttempt("sample_plugin", "1.0",
            PersistedPluginState.Deactivated, out _)).IsFalse();
        Assertions.AssertBool(shared.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        Assertions.AssertString(File.ReadAllText(path + ".user")).IsEqual("{invalid json");
    }

    [TestCase]
    public void FailedAttemptRequiresExplicitRetryBeforeItCanComplete()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0",
            PersistedPluginState.Activated, out var failedId)).IsTrue();
        Assertions.AssertBool(store.TryFail(failedId, PersistedPluginState.Failed, "solution_build_failed")).IsTrue();

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.TryBeginAttempt("sample_plugin", "1.0",
            PersistedPluginState.Activated, out _)).IsFalse();
        Assertions.AssertBool(reloaded.TryBeginAttempt("sample_plugin", "1.0",
            PersistedPluginState.Activated, out var retryId, manualRetry: true)).IsTrue();
        Assertions.AssertBool(retryId != failedId).IsTrue();
        Assertions.AssertBool(reloaded.TryComplete(retryId, [
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        Assertions.AssertBool(File.Exists(path + ".user")).IsFalse();
    }

    [TestCase]
    public void ManualRetryReplacesBlockedAttemptWithInvalidRecord()
    {
        var (store, _) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryRecordInvalid("sample_plugin", "", "invalid_plugin_version")).IsTrue();
        var first = store.GetLocal("sample_plugin")!.AttemptId;

        // without a manual retry the existing block is kept as is
        Assertions.AssertBool(store.TryRecordInvalid("sample_plugin", "", "invalid_plugin_version")).IsFalse();
        Assertions.AssertBool(store.GetLocal("sample_plugin")!.AttemptId == first).IsTrue();

        Assertions.AssertBool(store.TryRecordInvalid("sample_plugin", "", "invalid_plugin_version",
            manualRetry: true)).IsTrue();
        var replaced = store.GetLocal("sample_plugin")!;
        Assertions.AssertBool(replaced.AttemptId != first).IsTrue();
        Assertions.AssertObject(replaced.State).IsEqual(PersistedPluginState.Invalid);
        Assertions.AssertString(replaced.Reason).IsEqual("invalid_plugin_version");
    }
}
