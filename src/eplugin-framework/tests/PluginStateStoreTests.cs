#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public void AParticipantJoiningAgainChangesItsTargetAndKeepsItsInstalledVersion()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("dependency", "1.0", PersistedPluginState.Deactivated, out var id)).IsTrue();
        Assertions.AssertBool(store.TryAddParticipant(id, "consumer", "1.0", PersistedPluginState.Activated)).IsTrue();

        Assertions.AssertBool(store.TryAddParticipant(id, "consumer", "2.0", PersistedPluginState.Deactivated)).IsTrue();
        Assertions.AssertBool(store.TryAddParticipant(Guid.NewGuid(), "consumer", "1.0", PersistedPluginState.Deactivated)).IsFalse();

        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        var consumer = reloaded.GetLocal("consumer")!;
        Assertions.AssertObject(consumer.TargetState).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertString(consumer.InstalledVersion).IsEqual("1.0");
        Assertions.AssertObject(consumer.AttemptId).IsEqual(id);
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
    public void AnExternalEditOfTheSamePluginPreventsFinalization()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Deactivated)])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated,
            out var id)).IsTrue();

        // a teammate enabled it too, in another version
        Pull(path, other => Complete(other, "sample_plugin", "1.1", PersistedPluginState.Activated));
        var edited = File.ReadAllBytes(path);
        Assertions.AssertBool(store.TryComplete(id, [
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsFalse();

        Assertions.AssertBool(edited.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        Assertions.AssertBool(File.Exists(path + ".user")).IsTrue();
    }

    [TestCase]
    public void AnExternalEditOfOtherEntriesIsKeptWhenAPluginIsFinalized()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated,
            out var id)).IsTrue();

        // e.g. a git pull of another plugin's state, a license and a welcome page while the editor runs
        Pull(path, other =>
        {
            Complete(other, "other_plugin", "2.0", PersistedPluginState.Activated);
            Assertions.AssertBool(other.TryRecordLicenses([new AcceptedLicense("other_plugin", "LICENSE", "2.0", DateTimeOffset.UnixEpoch, false)])).IsTrue();
        });
        Assertions.AssertBool(store.TryComplete(id, [
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        Assertions.AssertBool(store.TryRecordWelcomes([new ShownWelcome("sample_plugin", "1.0", DateTimeOffset.UnixEpoch)])).IsTrue();

        Assertions.AssertBool(store.IsBlocked("sample_plugin")).IsFalse();
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertObject(reloaded.GetShared("sample_plugin")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertString(reloaded.GetShared("other_plugin")!.Version).IsEqual("2.0");
        Assertions.AssertBool(reloaded.IsLicenseAccepted("other_plugin", "LICENSE")).IsTrue();
        Assertions.AssertBool(reloaded.IsWelcomeShown("sample_plugin")).IsTrue();
    }

    [TestCase]
    public void LicensesAndWelcomePagesPulledBeforeAFailedWriteAreKept()
    {
        // a write fails because the folder is read-only, which Windows does not enforce for new files
        if (OperatingSystem.IsWindows()) return;
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        var directory = Path.GetDirectoryName(path)!;
        void Failing(Action write)
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { write(); }
            finally { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }

        Pull(path, other => Assertions.AssertBool(other.TryRecordLicenses([new AcceptedLicense("pulled", "LICENSE", "1.0", DateTimeOffset.UnixEpoch, false)])).IsTrue());
        Failing(() => Assertions.AssertBool(store.TryRecordLicenses([new AcceptedLicense("local", "LICENSE", "1.0", DateTimeOffset.UnixEpoch, false)])).IsFalse());
        Pull(path, other => Assertions.AssertBool(other.TryRecordWelcomes([new ShownWelcome("pulled", "1.0", DateTimeOffset.UnixEpoch)])).IsTrue());
        Failing(() => Assertions.AssertBool(store.TryRecordWelcomes([new ShownWelcome("local", "1.0", DateTimeOffset.UnixEpoch)])).IsFalse());

        // they still count for this session, and the next write saves them with what was pulled
        Assertions.AssertBool(store.IsLicenseAccepted("local", "LICENSE")).IsTrue();
        Assertions.AssertBool(store.IsWelcomeShown("local")).IsTrue();
        Complete(store, "sample_plugin", "1.0", PersistedPluginState.Activated);
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.IsLicenseAccepted("pulled", "LICENSE")).IsTrue();
        Assertions.AssertBool(reloaded.IsLicenseAccepted("local", "LICENSE")).IsTrue();
        Assertions.AssertBool(reloaded.IsWelcomeShown("pulled")).IsTrue();
        Assertions.AssertBool(reloaded.IsWelcomeShown("local")).IsTrue();
    }

    /// <summary>Changes the shared file like a git pull of a teammate's change: through another store on a copy of it.</summary>
    private void Pull(string path, Action<PluginStateStore> change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-state-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var copy = Path.Combine(directory, "eplugin-state.json");
        File.Copy(path, copy);
        var other = new PluginStateStore(copy, new NullLogger());
        Assertions.AssertBool(other.Load()).IsTrue();
        change(other);
        File.Copy(copy, path, true);
    }

    private static void Complete(PluginStateStore store, string slug, string version, PersistedPluginState state)
    {
        Assertions.AssertBool(store.TryBeginAttempt(slug, version, state, out var id)).IsTrue();
        Assertions.AssertBool(store.TryComplete(id, [new SharedPluginState(slug, version, state)])).IsTrue();
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

    [TestCase]
    public void FieldsOfANewerVersionAreReadAndKeptWhenTheFilesAreWritten()
    {
        var (_, path) = NewStore();
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "lastCompletedAttemptId": null,
              "plugins": [ { "slug": "sample_plugin", "version": "1.0", "state": "deactivated", "pinned": true } ],
              "licenses": [ { "slug": "sample_plugin", "license": "LICENSE", "reviewer": "someone" } ],
              "future": { "kept": [1, 2] }
            }
            """);
        File.WriteAllText(path + ".user", """
            { "schemaVersion": 1, "attempts": [ { "attemptId": "2b0e4fb2-a7c4-4bd5-9c4b-7a0c7c1d3e10", "slug": "other",
              "targetState": "activated", "state": "failed", "reason": "attempt_in_progress", "host": "pc-1" } ], "note": 3 }
            """);

        var store = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(store.Load()).IsTrue();
        Assertions.AssertBool(store.IsBlocked("other")).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(store.TryComplete(id, [new("sample_plugin", "1.0", PersistedPluginState.Activated)])).IsTrue();

        var shared = File.ReadAllText(path);
        Assertions.AssertString(shared).Contains("\"state\": \"activated\"");
        Assertions.AssertString(shared).Contains("\"pinned\": true");
        Assertions.AssertString(shared).Contains("\"reviewer\": \"someone\"");
        Assertions.AssertString(shared).Contains("\"future\"");
        var local = File.ReadAllText(path + ".user");
        Assertions.AssertString(local).Contains("\"host\": \"pc-1\"");
        Assertions.AssertString(local).Contains("\"note\": 3");
    }

    [TestCase]
    public void FilesOf090AreReadAndWrittenBackInAFormItCanRead()
    {
        // 0.9.0 rejects every field it does not know, and a self-update to a broken version rolls back to it
        var (_, path) = NewStore();
        var lifecycle = Guid.NewGuid();
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "lastCompletedAttemptId": "{{lifecycle}}",
              "plugins": [
                { "slug": "a", "version": "1.0", "state": "activated" },
                { "slug": "b", "version": "2.0", "state": "deactivated" }
              ],
              "licenses": [ { "slug": "a", "license": "LICENSE", "version": "1.0", "acceptedUtc": "2026-10-01T10:00:00+00:00", "automatic": false } ],
              "welcomes": [ { "slug": "a", "version": "1.0", "shownUtc": "2026-10-01T10:00:00+00:00" } ]
            }
            """);
        File.WriteAllText(path + ".user", """
            { "schemaVersion": 1, "attempts": [ { "attemptId": "2b0e4fb2-a7c4-4bd5-9c4b-7a0c7c1d3e10", "slug": "c",
              "installedVersion": "3.0", "targetState": "activated", "state": "failed", "reason": "recipe_operation_failed" } ] }
            """);

        var store = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(store.Load()).IsTrue();
        Assertions.AssertObject(store.LastCompletedAttemptId).IsEqual(lifecycle);
        Assertions.AssertBool(store.IsLicenseAccepted("a", "LICENSE") && store.IsWelcomeShown("a") && store.IsBlocked("c")).IsTrue();
        store.TryBeginAttempt("b", "2.0", PersistedPluginState.Activated, out var id);
        Assertions.AssertBool(store.TryComplete(id, [new("b", "2.0", PersistedPluginState.Activated)])).IsTrue();

        static IEnumerable<string> Names(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
            JsonValueKind.Array => element.EnumerateArray().SelectMany(Names),
            _ => []
        };
        string[] known090 = ["schemaVersion", "lastCompletedAttemptId", "plugins", "slug", "version", "state", "licenses",
            "license", "acceptedUtc", "automatic", "welcomes", "shownUtc", "attempts", "attemptId", "installedVersion",
            "targetState", "reason"];
        foreach (var file in new[] { path, path + ".user" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assertions.AssertArray(Names(document.RootElement).Except(known090).ToArray()).IsEmpty();
        }
    }

    [TestCase]
    public void AMisspelledOrNewerSchemaMakesTheFilesReadOnly()
    {
        foreach (var content in new[] { """{ "schemaVersion": 1, "plugin": [] }""", """{ "schemaVersion": 2, "plugins": [] }""" })
        {
            var (_, path) = NewStore();
            File.WriteAllText(path, content);

            var store = new PluginStateStore(path, new NullLogger());
            Assertions.AssertBool(store.Load()).IsFalse();
            Assertions.AssertBool(store.IsReadOnly).IsTrue();
        }
    }

    [TestCase]
    public void EnablingAndDisablingWriteTheSharedFileOnlyWhenAnEntryChanges()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        var baseline = File.ReadAllBytes(path);

        // e.g. an optional recipe installed for a plugin that stays activated
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var same)).IsTrue();
        Assertions.AssertBool(store.TryComplete(same, [new("sample_plugin", "1.0", PersistedPluginState.Activated)])).IsTrue();
        Assertions.AssertBool(baseline.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
        Assertions.AssertBool(File.Exists(path + ".user")).IsFalse();

        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Deactivated, out var off)).IsTrue();
        Assertions.AssertBool(store.TryComplete(off, [new("sample_plugin", "1.0", PersistedPluginState.Deactivated)])).IsTrue();
        Assertions.AssertObject(store.LastCompletedAttemptId).IsNull();
        Assertions.AssertString(File.ReadAllText(path)).Contains("\"lastCompletedAttemptId\": null");

        // only an update's acknowledgement records its attempt, which recovery looks for
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Deactivated, out var update)).IsTrue();
        Assertions.AssertBool(store.TryAcknowledgeVersions(update, new Dictionary<string, string> { ["sample_plugin"] = "2.0" })).IsTrue();
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertObject(reloaded.LastCompletedAttemptId).IsEqual(update);
        Assertions.AssertString(reloaded.GetShared("sample_plugin")!.Version).IsEqual("2.0");
    }

    [TestCase]
    public void AnUnchangedCompletionLeavesAFileEditedOnDiskAsItIs()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated)
        ])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        File.AppendAllText(path, " ");
        var edited = File.ReadAllBytes(path);

        Assertions.AssertBool(store.TryComplete(id, [new("sample_plugin", "1.0", PersistedPluginState.Activated)])).IsTrue();
        Assertions.AssertBool(store.IsBlocked("sample_plugin")).IsFalse();
        Assertions.AssertBool(edited.SequenceEqual(File.ReadAllBytes(path))).IsTrue();
    }

    [TestCase]
    public void AnUnreadableEditOnDiskStillPreventsFinalization()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        // an unresolved merge conflict
        File.WriteAllText(path, "<<<<<<< HEAD\n{}\n=======\n{}\n>>>>>>> other\n");

        Assertions.AssertBool(store.TryComplete(id, [new("sample_plugin", "1.0", PersistedPluginState.Activated)])).IsFalse();
        Assertions.AssertBool(store.IsBlocked("sample_plugin")).IsTrue();
        Assertions.AssertString(File.ReadAllText(path)).StartsWith("<<<<<<< HEAD");
    }

    [TestCase]
    public void AnUnwritableLocalFileRefusesNewMarkersButStillCompletes()
    {
        var (_, path) = NewStore();
        var log = new List<string>();
        var store = new PluginStateStore(path, new RecordingLogger(log, "store"));
        Assertions.AssertBool(store.Load()).IsTrue();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("waiting_plugin", "1.0", PersistedPluginState.Activated, out _)).IsTrue();
        // something else took the local file's place, e.g. a folder created by a sync tool
        File.Delete(path + ".user");
        Directory.CreateDirectory(path + ".user");

        Assertions.AssertBool(store.TryBeginAttempt("other_plugin", "1.0", PersistedPluginState.Activated, out var refused)).IsFalse();
        Assertions.AssertObject(refused).IsEqual(Guid.Empty);
        Assertions.AssertBool(store.TryAddParticipant(id, "other_plugin", "1.0", PersistedPluginState.Activated)).IsFalse();
        Assertions.AssertBool(store.TryFail(id, PersistedPluginState.Failed, "solution_build_failed")).IsFalse();
        Assertions.AssertBool(log.Any(l => l.StartsWith("error: store: Cannot save local plugin state", StringComparison.Ordinal))).IsTrue();
        Assertions.AssertBool(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length == 0).IsTrue();

        // the shared file is what counts; the marker that cannot be removed is ignored once it is completed
        Assertions.AssertBool(store.TryComplete(id, [new("sample_plugin", "1.0", PersistedPluginState.Activated)])).IsTrue();
        Assertions.AssertObject(store.GetShared("sample_plugin")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertBool(store.IsBlocked("sample_plugin")).IsFalse();
        Assertions.AssertBool(store.IsBlocked("waiting_plugin")).IsTrue();
        Assertions.AssertBool(log.Any(l => l.StartsWith("warn: store: Completed plugin state was saved", StringComparison.Ordinal))).IsTrue();
    }

    [TestCase]
    public void AnUnwritableSharedFileFailsTheBaseline()
    {
        var (_, path) = NewStore();
        var log = new List<string>();
        var store = new PluginStateStore(path, new RecordingLogger(log, "store"));
        Assertions.AssertBool(store.Load()).IsTrue();
        Directory.CreateDirectory(path);

        Assertions.AssertBool(store.TryCreateBaseline([])).IsFalse();

        Assertions.AssertBool(store.HasSharedFile).IsFalse();
        Assertions.AssertBool(log.Any(l => l.StartsWith("error: store: Cannot save finalized plugin state", StringComparison.Ordinal))).IsTrue();
    }

    [TestCase]
    public void InvalidRequestsAreRefusedWithoutWriting()
    {
        var (_, path) = NewStore();
        var log = new List<string>();
        var store = new PluginStateStore(path, new RecordingLogger(log, "store"));
        Assertions.AssertBool(store.Load()).IsTrue();

        Assertions.AssertBool(store.TryCreateBaseline([
            new SharedPluginState("sample_plugin", "1.0", PersistedPluginState.Activated),
            new SharedPluginState("sample_plugin", "2.0", PersistedPluginState.Activated)
        ])).IsFalse();
        Assertions.AssertBool(log.Any(l => l.Contains("Cannot create plugin state baseline for sample_plugin"))).IsTrue();
        Assertions.AssertBool(File.Exists(path)).IsFalse();

        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryAddParticipant(Guid.NewGuid(), "sample_plugin", "1.0", PersistedPluginState.Activated)).IsFalse();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(store.TryFail(id, PersistedPluginState.Activated, "not a failure")).IsFalse();
        Assertions.AssertBool(store.TryFail(id, PersistedPluginState.Failed, " ")).IsFalse();
        Assertions.AssertBool(store.TryFail(Guid.NewGuid(), PersistedPluginState.Failed, "unknown_attempt")).IsFalse();
        Assertions.AssertString(store.GetLocal("sample_plugin")!.Reason).IsEqual("attempt_in_progress");
    }

    [TestCase]
    public void ALocalFileWithAnInvalidAttemptMakesTheStoreReadOnly()
    {
        var (store, path) = NewStore();
        Assertions.AssertBool(store.TryCreateBaseline([])).IsTrue();
        Assertions.AssertBool(store.TryBeginAttempt("sample_plugin", "1.0", PersistedPluginState.Activated, out _)).IsTrue();
        var local = File.ReadAllText(path + ".user").Replace("\"attempt_in_progress\"", "\"\"");
        File.WriteAllText(path + ".user", local);

        var reloaded = new PluginStateStore(path, new NullLogger());

        Assertions.AssertBool(reloaded.Load()).IsFalse();
        Assertions.AssertBool(reloaded.IsReadOnly).IsTrue();
        Assertions.AssertBool(reloaded.TryComplete(Guid.NewGuid(), [])).IsFalse();
        Assertions.AssertString(File.ReadAllText(path + ".user")).IsEqual(local);
    }
}
#endif
