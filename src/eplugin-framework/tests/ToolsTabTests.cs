#if TOOLS
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The Tools tab of the settings on a fixture's framework, which cleans up a local directory in the fixture's folder,
/// and the cleanup suggestion for a directory above the user's size.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ToolsTabTests : IDisposable
{
    private LifecycleFixture _fixture = null!;
    private SettingsDialog _dialog = null!;
    private string _packages = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        // installed in the project at 1.0.0, a version every cleanup keeps
        _fixture.AddPlainPlugin("tool");
        _fixture.Global.InitializeUpdateSites();
        _fixture.Global.InitializeLocalSources();
        _packages = Path.Combine(_fixture.Root, "packages");
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0-beta" }) Zip("tool", version);
        foreach (var version in new[] { "1.0.0", "2.0.0" }) Zip("other", version);
        _dialog = GD.Load<PackedScene>("res://addons/ePlugin/Internal/Manager/SettingsDialog.tscn").Instantiate<SettingsDialog>();
        _dialog.Initialize(_fixture.Global);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_dialog);
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (_dialog is not null && GodotObject.IsInstanceValid(_dialog)) _dialog.Free();
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Zip(string slug, string version)
    {
        var path = Path.Combine(_packages, slug, $"{slug}-{version}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry($"addons/{slug}/plugin.cfg").Open());
        writer.Write($"[plugin]\nname=\"{slug}\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n");
    }

    private static async Task Until(Func<bool> done)
    {
        for (var frame = 0; frame < 500 && !done(); frame++)
        {
            EGlobal.RunPosted();
            await Task.Delay(10);
        }
        Assertions.AssertBool(done()).IsTrue();
    }

    private async Task Indexed() => await Until(() => !_fixture.Global.IsIndexingLocalSources);

    private T Node<T>(string name) where T : Node => _dialog.GetNode<T>(name);

    [TestCase]
    public async Task TheTabShowsTheDirectoryAndDeletesWhatTheChosenRuleRemoves()
    {
        _fixture.Global.AddLocalDirectory(_packages);
        await Indexed();

        _dialog.Open(null, _packages);

        Assertions.AssertString(Node<TabContainer>("%SettingsTabs").GetCurrentTabControl().Name).IsEqual("Tools");
        Assertions.AssertString(Node<OptionButton>("%CleanupDirectory").Text).IsEqual(_packages);
        Assertions.AssertString(Node<Label>("%CleanupStatistics").Text).StartsWith("5 plugin packages of 2 plugins (5 versions, 1 prerelease), ");
        var tree = Node<Tree>("%CleanupTree");
        Assertions.AssertThat(tree.GetRoot().GetChildren().Select(i => (i.GetText(0), i.GetText(1), i.GetText(4))).OrderBy(r => r.Item1).ToArray())
            .ContainsExactly(("other", "2", "2.0.0"), ("tool", "3", "1.2.0-beta"));
        // the latest version is kept even when it is a prerelease, so only the stable versions remain
        Assertions.AssertString(Node<Label>("%CleanupVersionPreview").Text).IsEqual("Nothing to delete");
        Assertions.AssertBool(Node<Button>("%CleanupVersionButton").Disabled).IsTrue();
        // everything is from today
        Assertions.AssertString(Node<Label>("%CleanupDatePreview").Text).IsEqual("Nothing to delete");

        var rule = Node<OptionButton>("%CleanupVersionRule");
        rule.Select(rule.GetItemIndex((int)CleanupRule.LatestVersion));
        rule.EmitSignal(OptionButton.SignalName.ItemSelected, rule.Selected);
        // the installed tool 1.0.0 is kept
        Assertions.AssertString(Node<Label>("%CleanupVersionPreview").Text).StartsWith("Deletes 2 files, ");
        Assertions.AssertString(Node<Label>("%CleanupVersionPreview").TooltipText)
            .IsEqual(string.Join("\n", Path.Combine("other", "other-1.0.0.zip"), Path.Combine("tool", "tool-1.1.0.zip")));

        Node<Button>("%CleanupVersionButton").EmitSignal(BaseButton.SignalName.Pressed);
        var confirm = Node<ConfirmationDialog>("%CleanupConfirm");
        Assertions.AssertBool(confirm.Visible).IsTrue();
        Assertions.AssertString(confirm.DialogText).StartsWith($"Delete 2 plugin packages (");
        Assertions.AssertString(confirm.DialogText).Contains("only the latest version");
        confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
        confirm.Hide();

        await Until(() => Node<Label>("%CleanupStatus").Text.StartsWith("Deleted", StringComparison.Ordinal));
        await Indexed();
        Assertions.AssertString(Node<Label>("%CleanupStatus").Text).StartsWith("Deleted 2 plugin packages, ");
        Assertions.AssertBool(File.Exists(Path.Combine(_packages, "tool", "tool-1.1.0.zip"))).IsFalse();
        Assertions.AssertBool(File.Exists(Path.Combine(_packages, "other", "other-1.0.0.zip"))).IsFalse();
        Assertions.AssertThat(Directory.GetFiles(_packages, "*.zip", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray())
            .ContainsExactly("other-2.0.0.zip", "tool-1.0.0.zip", "tool-1.2.0-beta.zip");
        Assertions.AssertString(Node<Label>("%CleanupStatistics").Text).StartsWith("3 plugin packages of 2 plugins");
    }

    [TestCase]
    public void WithoutLocalDirectoriesNothingCanBeCleanedUp()
    {
        _dialog.Open(null);

        Assertions.AssertInt(Node<TabContainer>("%SettingsTabs").CurrentTab).IsEqual(0);
        Assertions.AssertBool(Node<OptionButton>("%CleanupDirectory").Disabled).IsTrue();
        Assertions.AssertString(Node<Label>("%CleanupStatistics").Text).StartsWith("No local directory yet.");
        Assertions.AssertBool(Node<Button>("%CleanupVersionButton").Disabled).IsTrue();
        Assertions.AssertBool(Node<Button>("%CleanupDateButton").Disabled).IsTrue();
        Assertions.AssertFloat(Node<SpinBox>("%CleanupWarningSpin").Value).IsEqual(LocalSourceSettings.DefaultCleanupWarningGigabytes);
    }

    [TestCase]
    public async Task TheSizeIsTheUsersAndADirectoryAboveItIsSuggestedOncePerCheck()
    {
        _fixture.Host.Dialogs = true;
        _fixture.Global.AddLocalDirectory(_packages);
        await Indexed();
        _dialog.Open(null, _packages);

        Node<SpinBox>("%CleanupWarningSpin").Value = 2.5;
        Assertions.AssertString(File.ReadAllText(Path.Combine(_fixture.Host.ConfigDirectory, "eplugin", "local-sources.json")))
            .Contains("\"cleanupWarningGigabytes\": 2.5");
        Assertions.AssertFloat(_fixture.Global.CleanupWarningGigabytes).IsEqual(2.5);
        // only checked when the scheduled update check ran, and never for a directory within the size
        Assertions.AssertObject(_fixture.Global.TakeCleanupSuggestion()).IsNull();
        _fixture.Global.ScheduleCleanupCheck();
        EGlobal.RunPosted();
        Assertions.AssertObject(_fixture.Global.TakeCleanupSuggestion()).IsNull();

        // a few bytes
        _fixture.Global.SetCleanupWarning(1e-9);
        _fixture.Global.ScheduleCleanupCheck();
        EGlobal.RunPosted();
        var suggestion = _fixture.Global.TakeCleanupSuggestion();
        Assertions.AssertString(suggestion!.Directory).IsEqual(_packages);
        Assertions.AssertBool(suggestion.Bytes > suggestion.Limit).IsTrue();
        Assertions.AssertObject(_fixture.Global.TakeCleanupSuggestion()).IsNull();

        // the status says so while the directory is shown
        _dialog.Open(null, _packages);
        Assertions.AssertString(Node<Label>("%CleanupStatus").Text).StartsWith("This directory holds more than");

        // not while dialogs cannot be shown, e.g. in a headless editor
        _fixture.Host.Dialogs = false;
        _fixture.Global.ScheduleCleanupCheck();
        EGlobal.RunPosted();
        Assertions.AssertObject(_fixture.Global.TakeCleanupSuggestion()).IsNull();
    }
}
#endif
