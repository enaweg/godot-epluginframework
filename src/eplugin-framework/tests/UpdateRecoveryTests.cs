using System;
using System.IO;
using System.Text.Json.Nodes;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateRecoveryTests
{
    private string _root = null!;
    private string _folder = null!;
    private PluginStateStore _store = null!;
    private UpdateJournal _journal = null!;
    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "self-update-recovery-" + Guid.NewGuid().ToString("N"));
        _folder = Path.Combine(_root, ".godot/eplugin/updates/test");
        Directory.CreateDirectory(Path.Combine(_root, "addons/ePlugin"));
        Directory.CreateDirectory(Path.Combine(_folder, "backup/ePlugin"));
        Directory.CreateDirectory(Path.Combine(_folder, "backup-project"));
        File.WriteAllText(Path.Combine(_root, "addons/ePlugin/plugin.cfg"), "[plugin]\nversion=\"2.0.0\"");
        File.WriteAllText(Path.Combine(_folder, "backup/ePlugin/plugin.cfg"), "[plugin]\nversion=\"1.0.0\"");
        File.WriteAllText(Path.Combine(_root, "Game.csproj"), "broken new project");
        File.WriteAllText(Path.Combine(_folder, "backup-project/Game.csproj"), "working project");
        _store = new(Path.Combine(_root, "addons/eplugin-state.json"), new NullLogger()); _store.Load();
        _store.TryCreateBaseline([new("ePlugin", "1.0.0", PersistedPluginState.Activated)]);
        _store.TryBeginAttempt("ePlugin", "1.0.0", PersistedPluginState.Activated, out var id);
        _journal = new() { Id = "test", Directory = _folder, AttemptId = id, State = UpdatePhase.InterimBuilt,
            ProjectFiles = ["Game.csproj"], Plugins = [new() { Slug = "ePlugin", OldVersion = "1.0.0", NewVersion = "2.0.0", SwapStarted = true }] };
        _journal.Save();
    }
    [AfterTest] public void Cleanup() => Directory.Delete(_root, true);
    private UpdateRecovery.Outcome Recover(bool build = true) => UpdateRecovery.RunIfNeeded(_root, new NullLogger(), () => build, () => { });
    [TestCase]
    public void TwoUnhealthyStartsRestoreAndClearMarkerWithoutChangingSharedIndex()
    {
        var shared = File.ReadAllText(Path.Combine(_root, "addons/eplugin-state.json"));
        var path = Path.Combine(_folder, "journal.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!; json["futureField"] = 42; File.WriteAllText(path, json.ToJsonString());
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.Continue);
        Assertions.AssertInt(UpdateJournal.Load(_folder).StartCount).IsEqual(1);
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.Restart);
        _store.Load();
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "addons/eplugin-state.json"))).IsEqual(shared);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_root, "Game.csproj"))).IsEqual("working project");
        Assertions.AssertBool(File.ReadAllText(Path.Combine(_root, "addons/ePlugin/plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertInt(JsonNode.Parse(File.ReadAllText(path))!["futureField"]!.GetValue<int>()).IsEqual(42);
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.Continue);
    }
    [TestCase]
    public void HealthyMarkerPreventsEarlyRollback()
    {
        _journal.StartCount = 9; _journal.Save();
        File.WriteAllText(Path.Combine(_folder, "healthy.marker"), "verified");
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.Continue);
        Assertions.AssertInt(UpdateJournal.Load(_folder).StartCount).IsEqual(9);
    }
    [TestCase]
    public void MissingBackupAndFutureSchemaRequireManualRepairWithoutClearingMarker()
    {
        _journal.StartCount = 1; _journal.Save(); Directory.Delete(_journal.Backup("ePlugin"), true);
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.ManualRepair);
        _store.Load(); Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(1);
        _journal.Schema = 2; _journal.Save();
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.ManualRepair);
    }
    [TestCase]
    public void FailedRecoveryBuildRetainsMarkerAndBackupsForRepeatableRetry()
    {
        _journal.StartCount = 1; _journal.Save();
        Assertions.AssertObject(Recover(false)).IsEqual(UpdateRecovery.Outcome.ManualRepair);
        Assertions.AssertBool(Directory.Exists(_journal.Backup("ePlugin"))).IsTrue();
        _store.Load(); Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(1);
        Assertions.AssertObject(Recover()).IsEqual(UpdateRecovery.Outcome.Restart);
    }
}
