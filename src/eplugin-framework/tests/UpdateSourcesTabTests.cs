#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The Update sources tab of the settings on a fixture's framework: update sites are written to the fixture's project
/// and local directories to its user configuration folder.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class UpdateSourcesTabTests : IDisposable
{
    private const string Site = "https://github.com/fork/tool/releases";
    private LifecycleFixture _fixture = null!;
    private SettingsDialog _dialog = null!;
    private int _changes;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _fixture.AddPlainPlugin("tool", extraConfig: "update_url=\"https://github.com/owner/tool/releases\"");
        _fixture.AddPlainPlugin("other");
        _fixture.Global.InitializeUpdateSites();
        _fixture.Global.InitializeLocalSources();
        _dialog = GD.Load<PackedScene>("res://addons/ePlugin/Internal/Manager/SettingsDialog.tscn").Instantiate<SettingsDialog>();
        _dialog.Initialize(_fixture.Global);
        _changes = 0;
        _dialog.Changed += sourcesChanged => { if (sourcesChanged) _changes++; };
        // in the tree, so the tab follows the local index and its windows can open
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_dialog);
        Tab.Open();
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (_dialog is not null && GodotObject.IsInstanceValid(_dialog)) _dialog.Free();
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private UpdateSourcesTab Tab => _dialog.GetNode<UpdateSourcesTab>("%SettingsTabs/Update sources");

    private Tree SourceTree => _dialog.GetNode<Tree>("%SourceTree");

    private TreeItem Group(int index) => SourceTree.GetRoot().GetChild(index);

    private string Status => _dialog.GetNode<Label>("%SourceStatus").Text;

    private string SitesFile => Path.Combine(_fixture.Root, "addons", "eplugin-update-sites.json");

    private void Press(string button) => _dialog.GetNode<Button>(button).EmitSignal(BaseButton.SignalName.Pressed);

    private void Select(TreeItem item, int column)
    {
        item.Select(column);
        SourceTree.EmitSignal(Tree.SignalName.ItemSelected);
    }

    [TestCase]
    public void AnEmptyProjectListsItsGroupsWithHowToAddSources()
    {
        Assertions.AssertThat(SourceTree.GetRoot().GetChildren().Select(g => g.GetText(0)).ToArray())
            .ContainsExactly("Set by this project", "Local directories", "Built-in list");
        Assertions.AssertString(Group(0).GetText(1)).StartsWith("No update sites set yet.");
        Assertions.AssertString(Group(1).GetText(1)).StartsWith("No local directory yet.");
        Assertions.AssertString(Status).IsEqual("No update sites set for this project.");
        Assertions.AssertBool(_dialog.GetNode<Button>("%RescanLocalSourcesButton").Disabled).IsTrue();
        Assertions.AssertBool(_dialog.GetNode<Button>("%RemoveSourceButton").Disabled).IsTrue();
        Assertions.AssertBool(_dialog.GetNode<Button>("%EditSiteButton").Disabled).IsTrue();
    }

    [TestCase]
    public void AnUpdateSiteIsAddedForAPluginOfTheProjectAndRemovedAgain()
    {
        Press("%AddSiteButton");
        var slug = _dialog.GetNode<OptionButton>("%SiteSlug");
        Assertions.AssertThat(Enumerable.Range(0, slug.ItemCount).Select(slug.GetItemText).ToArray()).ContainsExactly("other", "tool");
        slug.Select(1);
        slug.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        // the plugin.cfg update_url stays the fallback
        Assertions.AssertString(_dialog.GetNode<Label>("%SiteHint").Text).EndsWith("plugin.cfg's update_url is used: https://github.com/owner/tool/releases");

        var url = _dialog.GetNode<LineEdit>("%SiteUrl");
        url.Text = "https://example.org/tool.zip";
        _dialog.GetNode<ConfirmationDialog>("%SiteEditor").EmitSignal(AcceptDialog.SignalName.Confirmed);
        Assertions.AssertBool(File.Exists(SitesFile)).IsFalse();
        Assertions.AssertString(_dialog.GetNode<Label>("%SiteHint").Text).NotContains("(Parameter");
        Assertions.AssertInt(_changes).IsEqual(0);

        url.Text = Site;
        url.EmitSignal(LineEdit.SignalName.TextSubmitted, Site);
        Assertions.AssertString(File.ReadAllText(SitesFile)).Contains(Site);
        Assertions.AssertString(_fixture.Global.UpdateSiteOf("tool")).IsEqual(Site);
        Assertions.AssertInt(_changes).IsEqual(1);
        Assertions.AssertString(Status).IsEqual("1 update site set for this project.");
        var row = Group(0).GetChild(0);
        Assertions.AssertString(row.GetText(0)).IsEqual("tool");
        Assertions.AssertString(row.GetText(1)).IsEqual(Site);
        Assertions.AssertString(row.GetTooltipText(1)).Contains("plugin.cfg's update_url is used: https://github.com/owner/tool/releases");

        // a plugin with a site is edited, not added again
        Press("%AddSiteButton");
        Assertions.AssertThat(Enumerable.Range(0, slug.ItemCount).Select(slug.GetItemText).ToArray()).ContainsExactly("other");

        Select(Group(0).GetChild(0), 0);
        Assertions.AssertBool(_dialog.GetNode<Button>("%EditSiteButton").Disabled).IsFalse();
        Press("%RemoveSourceButton");
        Assertions.AssertObject(_fixture.Global.UpdateSiteOf("tool")).IsNull();
        Assertions.AssertBool(File.Exists(SitesFile)).IsFalse();
        Assertions.AssertInt(_changes).IsEqual(2);
        Assertions.AssertString(Group(0).GetText(1)).StartsWith("No update sites set yet.");
    }

    [TestCase]
    public void ASiteOfAPluginThatWasRemovedIsStillListed()
    {
        Assertions.AssertBool(_fixture.Global.SetUpdateSite("gone", Site)).IsTrue();

        Tab.Open();

        var row = Group(0).GetChild(0);
        Assertions.AssertString(row.GetText(0)).IsEqual("gone");
        Assertions.AssertString(row.GetTooltipText(0)).IsEqual("gone is not in res://addons.");
    }

    [TestCase]
    public async Task ALocalDirectoryIsListedWithThePackagesItsIndexFound()
    {
        var directory = Path.Combine(_fixture.Root, "packages");
        Directory.CreateDirectory(directory);
        var folders = _dialog.GetNode<FileDialog>("%LocalSourceFolderDialog");

        folders.EmitSignal(FileDialog.SignalName.DirSelected, directory);
        // the list is the user's, not the project's
        Assertions.AssertString(File.ReadAllText(Path.Combine(_fixture.Host.ConfigDirectory, "eplugin", "local-sources.json")))
            .Contains(directory.Replace("\\", "\\\\"));
        Assertions.AssertString(Group(1).GetChild(0).GetText(1)).IsEqual(directory);
        for (var frame = 0; frame < 500 && _fixture.Global.IsIndexingLocalSources; frame++)
        {
            EGlobal.RunPosted();
            await Task.Delay(10);
        }

        Assertions.AssertBool(_fixture.Global.IsIndexingLocalSources).IsFalse();
        Assertions.AssertString(Group(1).GetChild(0).GetText(0)).IsEqual("0 plugin packages");
        Assertions.AssertString(Status).IsEqual("No update sites set for this project · 0 plugin packages in 0 ZIP files");
        Assertions.AssertBool(_dialog.GetNode<Button>("%RescanLocalSourcesButton").Disabled).IsFalse();

        folders.EmitSignal(FileDialog.SignalName.DirSelected, directory);
        Assertions.AssertString(Status).IsEqual("This directory is already listed.");

        Select(Group(1).GetChild(0), 1);
        Press("%RemoveSourceButton");
        Assertions.AssertThat(_fixture.Global.LocalDirectories).IsEmpty();
        Assertions.AssertString(Group(1).GetText(1)).StartsWith("No local directory yet.");
        for (var frame = 0; frame < 500 && _fixture.Global.IsIndexingLocalSources; frame++)
        {
            EGlobal.RunPosted();
            await Task.Delay(10);
        }
    }
}
#endif
