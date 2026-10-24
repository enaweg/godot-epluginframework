#if TOOLS
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The ePlugin Manager on a fixture's framework, whose editor enables and disables the plugins it lists. A click on a
/// check box is <see cref="EPluginManagerDialog.CellEdited"/>; dialogs the manager opens appear in the scene tree's root.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PluginManagerDialogTests : IDisposable
{
    private const int EnabledColumn = 0, NameColumn = 1, VersionColumn = 2;
    private LifecycleFixture _fixture = null!;
    private EPluginManagerDialog _dialog = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _fixture.AddPlainPlugin("ePlugin", "0.12.0");
        _fixture.AddPlainPlugin("tool", "2.1.0", "author=\"Someone\"\ndescription=\"Does things.\"");
        _fixture.AddPlainPlugin("other");
        _fixture.Host.Enabled.Add("tool");
        _fixture.Global.InitializeUpdateSites();
        _fixture.Global.InitializeLocalSources();
        _dialog = EPluginManagerDialog.Create(_fixture.Global, CancellationToken.None);
        Root.AddChild(_dialog);
        _dialog.Refresh();
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        foreach (var popped in Root.GetChildren().Where(c => c is LicenseDialog or WelcomeDialog or UpdateReviewDialog))
            if (!popped.IsQueuedForDeletion()) popped.Free();
        if (_dialog is not null && GodotObject.IsInstanceValid(_dialog)) _dialog.Free();
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    private static Window Root => Tree.Root;

    private Tree PluginTree => _dialog.GetNode<Tree>("%PluginTree");

    private string Status => _dialog.GetNode<Label>("%Status").Text;

    private TreeItem Row(string slug) => PluginTree.GetRoot().GetChildren().Single(i => i.GetMetadata(NameColumn).AsString() == slug);

    /// <summary>A click on a check box of the list, which Godot reports once the box changed.</summary>
    private void Click(string slug, int column, bool check)
    {
        var item = Row(slug);
        item.SetChecked(column, check);
        _dialog.CellEdited(item, column);
    }

    private void Select(string slug)
    {
        Row(slug).Select(NameColumn);
        PluginTree.EmitSignal(Godot.Tree.SignalName.ItemSelected);
    }

    private static async Task Frames(int count = 2)
    {
        for (var i = 0; i < count; i++) await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private static T? Popped<T>() where T : Window =>
        Root.GetChildren().OfType<T>().LastOrDefault(w => !w.IsQueuedForDeletion() && w.Visible);

    [TestCase]
    public void EveryPluginIsListedWithWhetherTheEditorHasItEnabled()
    {
        Assertions.AssertThat(PluginTree.GetRoot().GetChildren().Select(i => (i.GetMetadata(NameColumn).AsString(), i.IsChecked(EnabledColumn))).ToArray())
            .ContainsExactlyInAnyOrder(("ePlugin", true), ("tool", true), ("other", false));
        Assertions.AssertString(Row("tool").GetText(VersionColumn)).IsEqual("2.1.0");

        Select("tool");

        Assertions.AssertString(_dialog.GetNode<Label>("%DetailsName").Text).Contains("tool");
        Assertions.AssertString(_dialog.GetNode<RichTextLabel>("%DetailsText").Text).StartsWith("Does things.");
        // without an update source there is no version to choose
        Assertions.AssertBool(_dialog.GetNode<Control>("%VersionRow").Visible).IsFalse();
    }

    [TestCase]
    public async Task APluginIsEnabledAndDisabledFromItsCheckBoxAfterTheClick()
    {
        Click("other", EnabledColumn, true);

        Assertions.AssertString(Status).IsEqual("Enabling other...");
        // not inside the tree's edit signal
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("other")).IsFalse();
        await Frames();
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("other")).IsTrue();
        Assertions.AssertBool(Row("other").IsChecked(EnabledColumn)).IsTrue();

        Click("tool", EnabledColumn, false);
        await Frames();
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("tool")).IsFalse();
        Assertions.AssertBool(Row("tool").IsChecked(EnabledColumn)).IsFalse();
    }

    [TestCase]
    public async Task DisablingTheFrameworkAsksFirstAndGoesThroughTheEditor()
    {
        Click("ePlugin", EnabledColumn, false);

        // the box stays checked until the confirmation
        Assertions.AssertBool(Row("ePlugin").IsChecked(EnabledColumn)).IsTrue();
        var confirm = _dialog.GetNode<ConfirmationDialog>("%DisableFrameworkConfirm");
        Assertions.AssertBool(confirm.Visible).IsTrue();

        confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
        // not inside the confirmation's signal, which disabling the framework would free
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("ePlugin")).IsTrue();
        Assertions.AssertBool(_dialog.Visible).IsFalse();
        await Frames();

        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("ePlugin")).IsFalse();
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("tool")).IsTrue();
    }

    [TestCase]
    public async Task APluginThatRequiresALicenseIsEnabledOnlyOnceItIsAccepted()
    {
        _fixture.AddPlainPlugin("licensed", extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("licensed"), "LICENSE"), "Terms of use");
        _dialog.Refresh();

        Click("licensed", EnabledColumn, true);
        await Frames();
        var license = Popped<LicenseDialog>()!;
        Assertions.AssertString(Status).IsEqual("Review the license to enable licensed.");
        Assertions.AssertString(license.GetNode<RichTextLabel>("%LicenseText").Text).IsEqual("Terms of use");

        license.EmitSignal(AcceptDialog.SignalName.CustomAction, "decline");
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("licensed")).IsFalse();
        Assertions.AssertString(Status).IsEqual("licensed was not enabled: a license it needs was declined.");

        Click("licensed", EnabledColumn, true);
        await Frames();
        Popped<LicenseDialog>()!.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames();

        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("licensed")).IsTrue();
        // accepted once, so the details only show it
        Select("licensed");
        _dialog.GetNode<RichTextLabel>("%DetailsText").EmitSignal(RichTextLabel.SignalName.MetaClicked, PluginManagerViewModel.LicenseMeta);
        Assertions.AssertString(Popped<LicenseDialog>()!.Title).IsEqual("ePlugin — License");
    }

    [TestCase]
    public void TheWelcomePageIsShownAgainFromTheDetails()
    {
        File.WriteAllText(Path.Combine(_fixture.Addon("tool"), "README.md"), "# Tool\nHello");
        _dialog.Refresh();
        Select("tool");

        _dialog.GetNode<RichTextLabel>("%DetailsText").EmitSignal(RichTextLabel.SignalName.MetaClicked, PluginManagerViewModel.WelcomeMeta);

        var welcome = Popped<WelcomeDialog>()!;
        Assertions.AssertString(welcome.Title).IsEqual("ePlugin — Welcome page");
        Assertions.AssertString(welcome.GetNode<Label>("%WelcomeName").Text).IsEqual("tool 2.1.0");
    }

    [TestCase]
    public async Task AFailedPluginIsRetriedOrDisabledFromNeedsAttention()
    {
        _fixture.AddEPlugin("broken", b => b.AddNuget("Bad.Package"));
        _fixture.Cli.FailingPackages.Add("Bad.Package");
        _fixture.Enable("broken");
        Assertions.AssertObject(_fixture.State("broken")).IsEqual(EEditorPluginState.Error);

        // a problem the session has not shown yet opens the list by itself
        _dialog.Open();
        await Frames();
        var attention = _dialog.GetNode<AttentionDialog>("%AttentionDialog");
        Assertions.AssertBool(attention.Visible).IsTrue();
        Assertions.AssertString(_dialog.GetNode<Button>("%AttentionButton").Text).IsEqual("1 plugin needs attention");

        _fixture.Cli.FailingPackages.Clear();
        attention.GetNode<Button>("%RetryPluginButton").EmitSignal(BaseButton.SignalName.Pressed);

        Assertions.AssertObject(_fixture.State("broken")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.Store.GetLocal("broken")).IsNull();
        // nothing is left to decide
        Assertions.AssertBool(attention.Visible).IsFalse();
    }

    [TestCase]
    public async Task GivingUpOnAFailedActivationDisablesThePlugin()
    {
        _fixture.AddEPlugin("broken", b => b.AddNuget("Bad.Package"));
        _fixture.Cli.FailingPackages.Add("Bad.Package");
        _fixture.Enable("broken");
        _dialog.Open();
        await Frames();

        _dialog.GetNode<AttentionDialog>("%AttentionDialog").GetNode<Button>("%DisablePluginButton").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames();

        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("broken")).IsFalse();
        Assertions.AssertObject(_fixture.Store.GetLocal("broken")).IsNull();
        Assertions.AssertBool(Row("broken").IsChecked(EnabledColumn)).IsFalse();
    }

    [TestCase]
    public async Task AnUpdateFromALocalDirectoryIsListedAndCanBeLeftOutOrChosenAsAVersion()
    {
        var packages = Path.Combine(_fixture.Root, "packages");
        Directory.CreateDirectory(packages);
        using (var zip = ZipFile.Open(Path.Combine(packages, "tool-3.0.0.zip"), ZipArchiveMode.Create))
        {
            foreach (var (path, content) in new[] { ("addons/tool/plugin.cfg", "[plugin]\nname=\"tool\"\nversion=\"3.0.0\"\nscript=\"plugin.gd\"\n"), ("addons/tool/plugin.gd", "extends EditorPlugin") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        _fixture.Global.AddLocalDirectory(packages);
        for (var frame = 0; frame < 500 && _fixture.Global.IsIndexingLocalSources; frame++)
        {
            EGlobal.RunPosted();
            await Task.Delay(10);
        }
        _dialog.Refresh();

        Assertions.AssertString(Row("tool").GetText(VersionColumn)).IsEqual("2.1.0 → 3.0.0");
        Assertions.AssertBool(Row("tool").IsChecked(VersionColumn)).IsTrue();
        Assertions.AssertString(Status).StartsWith("1 update available");
        Assertions.AssertBool(_dialog.GetOkButton().Disabled).IsFalse();

        Click("tool", VersionColumn, false);
        Assertions.AssertBool(_dialog.GetOkButton().Disabled).IsTrue();

        Select("tool");
        var versions = _dialog.GetNode<OptionButton>("%VersionSelect");
        Assertions.AssertBool(_dialog.GetNode<Control>("%VersionRow").Visible).IsTrue();
        Assertions.AssertInt(versions.ItemCount).IsEqual(2);
        Assertions.AssertString(versions.GetItemText(versions.Selected)).Contains("2.1.0");
        var newer = Enumerable.Range(0, versions.ItemCount).Single(i => versions.GetItemText(i).Contains("3.0.0"));
        versions.Select(newer);
        versions.EmitSignal(OptionButton.SignalName.ItemSelected, newer);
        var install = _dialog.GetNode<Button>("%InstallVersionButton");
        Assertions.AssertBool(install.Disabled).IsFalse();

        install.EmitSignal(BaseButton.SignalName.Pressed);

        var confirm = _dialog.GetNode<ConfirmationDialog>("%VersionConfirm");
        Assertions.AssertBool(confirm.Visible).IsTrue();
        Assertions.AssertString(confirm.DialogText).StartsWith("Replace tool 2.1.0 with version 3.0.0?");
        // without dialogs, a build failure restores the previous version
        Assertions.AssertString(confirm.DialogText).EndsWith(" and restored if installation fails.");
    }

    [TestCase]
    public void ACheckForUpdatesWithoutAnUpdateSystemFindsNothingAndClosingHidesTheManager()
    {
        _dialog.Open();

        _dialog.GetNode<Button>("%CheckButton").EmitSignal(BaseButton.SignalName.Pressed);

        Assertions.AssertString(Status).NotContains("Checking for updates");
        Assertions.AssertBool(_dialog.GetNode<Button>("%CheckButton").Disabled).IsFalse();

        _dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
        Assertions.AssertBool(_dialog.Visible).IsFalse();
    }

    /// <summary>A ZIP file of one plugin, as the Godot Asset Library has them.</summary>
    private string Zip(string slug, string version)
    {
        var path = Path.Combine(_fixture.Root, $"{slug}-{version}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in new[]
                 {
                     ($"{slug}-main/addons/{slug}/plugin.cfg", $"[plugin]\nname=\"{slug}\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n"),
                     ($"{slug}-main/addons/{slug}/plugin.gd", "extends EditorPlugin"),
                     ($"{slug}-main/README.md", "read me")
                 })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    [TestCase]
    public async Task AZipFileOfANewPluginIsInstalledDisabledOnceConfirmed()
    {
        _fixture.Global.InitializeUpdateJournals();
        _dialog.Open();

        await _dialog.InstallZipAsync(Zip("fresh", "1.2.0"));

        var confirm = _dialog.GetNode<ConfirmationDialog>("%InstallConfirm");
        Assertions.AssertBool(confirm.Visible).IsTrue();
        Assertions.AssertString(_dialog.GetNode<RichTextLabel>("%InstallText").Text).StartsWith("Install [b]fresh 1.2.0[/b] into res://addons/fresh?");
        // nothing is installed before the confirmation
        Assertions.AssertBool(Directory.Exists(_fixture.Addon("fresh"))).IsFalse();

        confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames();

        Assertions.AssertBool(File.Exists(Path.Combine(_fixture.Addon("fresh"), "plugin.gd"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(_fixture.Addon("fresh"), "README.md"))).IsFalse();
        Assertions.AssertBool(Row("fresh").IsChecked(EnabledColumn)).IsFalse();
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("fresh")).IsFalse();
        Assertions.AssertString(Status).StartsWith("Installed fresh 1.2.0 into res://addons/fresh.");
        Assertions.AssertString(_dialog.GetNode<Label>("%DetailsName").Text).Contains("fresh");
        Assertions.AssertThat(_fixture.Operations).NotContains("build");
    }

    [TestCase]
    public async Task CancelingTheConfirmationInstallsNothing()
    {
        _fixture.Global.InitializeUpdateJournals();
        _dialog.Open();
        await _dialog.InstallZipAsync(Zip("fresh", "1.2.0"));

        _dialog.GetNode<ConfirmationDialog>("%InstallConfirm").EmitSignal(AcceptDialog.SignalName.Canceled);

        Assertions.AssertBool(Directory.Exists(_fixture.Addon("fresh"))).IsFalse();
        Assertions.AssertString(Status).StartsWith("Installation canceled.");
        Assertions.AssertBool(Directory.EnumerateFileSystemEntries(Path.Combine(_fixture.Root, ".godot", "eplugin", "updates")).Any()).IsFalse();
    }

    [TestCase]
    public async Task AZipFileOfAnInstalledPluginIsInstalledLikeAVersionOfIt()
    {
        _fixture.Global.InitializeUpdateJournals();
        _dialog.Open();

        await _dialog.InstallZipAsync(Zip("tool", "3.0.0"));

        var confirm = _dialog.GetNode<ConfirmationDialog>("%VersionConfirm");
        Assertions.AssertBool(confirm.Visible).IsTrue();
        Assertions.AssertString(confirm.DialogText).StartsWith("Replace tool 2.1.0 with version 3.0.0?");
        Assertions.AssertString(_dialog.GetNode<Label>("%DetailsName").Text).Contains("tool");
        Assertions.AssertBool(_dialog.GetNode<ConfirmationDialog>("%InstallConfirm").Visible).IsFalse();
    }

    [TestCase]
    public async Task AZipFileOfADisabledPluginOrWithoutAPluginIsRefused()
    {
        _fixture.Global.InitializeUpdateJournals();
        _dialog.Open();

        await _dialog.InstallZipAsync(Zip("other", "3.0.0"));
        Assertions.AssertString(Status).StartsWith("Cannot install other-3.0.0.zip: other is installed in res://addons/other");
        Assertions.AssertString(Status).Contains("Enable the plugin");

        var empty = Path.Combine(_fixture.Root, "empty.zip");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) writer.Write("nothing");
        await _dialog.InstallZipAsync(empty);
        Assertions.AssertString(Status).StartsWith("Cannot install empty.zip: The ZIP file contains neither a plugin.cfg nor a GDExtension");
        Assertions.AssertBool(_dialog.GetNode<Button>("%InstallZipButton").Disabled).IsFalse();
    }
}
#endif
