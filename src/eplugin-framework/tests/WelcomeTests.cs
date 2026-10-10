using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Welcomes;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class WelcomeTests
{
    private readonly List<string> _directories = [];

    [AfterTest]
    public void Cleanup()
    {
        foreach (var directory in _directories.Where(Directory.Exists)) Directory.Delete(directory, true);
        _directories.Clear();
    }

    private string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-welcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static ShownWelcome Shown(string slug) => new(slug, "1.0.0", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private PluginStateStore StoreWithBaseline(out string path)
    {
        path = Path.Combine(TempDirectory(), "eplugin-state.json");
        var store = new PluginStateStore(path, new NullLogger());
        store.Load();
        store.TryCreateBaseline([new SharedPluginState("plugin", "1.0.0", PersistedPluginState.Activated)]);
        return store;
    }

    [TestCase]
    public void ReadmeIsTheDefaultAndAPluginWithoutOneHasNoWelcome()
    {
        var directory = TempDirectory();
        Assertions.AssertObject(PluginWelcome.FromConfig("plugin", "Plugin", "1.0.0", null, directory)).IsNull();
        File.WriteAllText(Path.Combine(directory, "readme"), "plain");
        Assertions.AssertString(PluginWelcome.FromConfig("plugin", "Plugin", "1.0.0", "", directory)!.Text).IsEqual("plain");
        // README.md wins over README.txt and README, whatever the case of the file name
        File.WriteAllText(Path.Combine(directory, "README.txt"), "text");
        File.WriteAllText(Path.Combine(directory, "Readme.md"), "[b]Hello[/b]");
        var welcome = PluginWelcome.FromConfig("plugin", "", "1.0.0", null, directory)!;
        Assertions.AssertString(welcome.Source).IsEqual("Readme.md");
        Assertions.AssertString(welcome.Text).IsEqual("[b]Hello[/b]");
        Assertions.AssertString(welcome.Name).IsEqual("plugin");
        Assertions.AssertString(welcome.Problem).IsNull();
    }

    [TestCase]
    public void ConfiguredFileIsReadAndAMissingOneIsReported()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "README.md"), "not the configured file");
        Directory.CreateDirectory(Path.Combine(directory, "docs"));
        File.WriteAllText(Path.Combine(directory, "docs", "WELCOME.txt"), "[i]Welcome[/i]");
        var welcome = PluginWelcome.FromConfig("plugin", "Plugin", "1.0.0", "res://addons/plugin/docs/WELCOME.txt", directory)!;
        Assertions.AssertString(welcome.Source).IsEqual("docs/WELCOME.txt");
        Assertions.AssertString(welcome.Text).IsEqual("[i]Welcome[/i]");
        var missing = PluginWelcome.FromConfig("plugin", "Plugin", "1.0.0", "docs/GONE.txt", directory)!;
        Assertions.AssertString(missing.Problem).Contains("missing");
        foreach (var path in new[] { "../README.md", "/etc/passwd", "res://addons/other/README.md" })
        {
            var outside = PluginWelcome.FromConfig("plugin", "Plugin", "1.0.0", path, directory)!;
            Assertions.AssertString(outside.Problem).IsNotNull();
            Assertions.AssertString(outside.Text).IsEmpty();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void BuilderTellsWelcomePathsFromWelcomeTexts()
    {
        var builder = EEditorPluginBuilder.Create();
        builder.SetWelcome("res://addons/plugin/WELCOME.txt");
        Assertions.AssertString(builder.PluginRecipe.PluginWelcome!.Path).IsEqual("res://addons/plugin/WELCOME.txt");
        Assertions.AssertString(builder.PluginRecipe.PluginWelcome.Text).IsNull();
        builder.SetWelcome("[b]Thanks for installing[/b]\nres://not/a/path");
        Assertions.AssertString(builder.PluginRecipe.PluginWelcome!.Text).IsEqual("[b]Thanks for installing[/b]\nres://not/a/path");
        Assertions.AssertString(builder.PluginRecipe.PluginWelcome.Path).IsNull();
        Assertions.AssertThrown(() => builder.SetWelcome(" ")).IsInstanceOf<ArgumentException>();
        var text = PluginWelcome.FromText("plugin", "Plugin", "1.0.0", "Hello");
        Assertions.AssertString(PluginManagerViewModel.WelcomeLine(text)).Contains("Set by the plugin");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ShownWelcomesAreSavedInThePluginStateFile()
    {
        var store = StoreWithBaseline(out var path);
        Assertions.AssertString(File.ReadAllText(path)).NotContains("welcomes");
        Assertions.AssertBool(store.TryRecordLicenses([new AcceptedLicense("plugin", "LICENSE", "1.0.0", DateTimeOffset.UtcNow, false)])).IsTrue();
        Assertions.AssertBool(store.TryRecordWelcomes([Shown("plugin")])).IsTrue();
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.IsWelcomeShown("plugin")).IsTrue();
        Assertions.AssertBool(reloaded.IsWelcomeShown("other")).IsFalse();
        // licenses and welcome pages are written together, neither replaces the other
        Assertions.AssertBool(reloaded.IsLicenseAccepted("plugin", "LICENSE")).IsTrue();
        Assertions.AssertBool(reloaded.TryBeginAttempt("plugin", "1.0.0", PersistedPluginState.Deactivated, out var id)).IsTrue();
        Assertions.AssertBool(reloaded.TryComplete(id, [new("plugin", "1.0.0", PersistedPluginState.Deactivated)])).IsTrue();
        var again = new PluginStateStore(path, new NullLogger());
        again.Load();
        Assertions.AssertBool(again.IsWelcomeShown("plugin")).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void WelcomesCountForTheSessionWhenTheyCannotBeSaved()
    {
        var path = Path.Combine(TempDirectory(), "eplugin-state.json");
        var store = new PluginStateStore(path, new NullLogger());
        store.Load();
        // no baseline yet: recording a welcome page must not create the shared file in its place
        Assertions.AssertBool(store.TryRecordWelcomes([Shown("plugin")])).IsFalse();
        Assertions.AssertBool(store.IsWelcomeShown("plugin")).IsTrue();
        Assertions.AssertBool(File.Exists(path)).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void InvalidWelcomeEntriesMakeTheStateReadOnly()
    {
        var path = Path.Combine(TempDirectory(), "eplugin-state.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "plugins": [], "welcomes": [ { "slug": "plugin" }, { "slug": "plugin" } ] }
            """);
        var store = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(store.Load()).IsFalse();
        Assertions.AssertBool(store.IsReadOnly).IsTrue();
    }

    [TestCase]
    public void DetailsLinkTheWelcomePage()
    {
        Assertions.AssertString(PluginManagerViewModel.WelcomeLine(null)).IsEmpty();
        var welcome = new WelcomeEntry("plugin", "Plugin", "1.0.0", "README.md", "text");
        Assertions.AssertString(PluginManagerViewModel.WelcomeLine(welcome))
            .Contains($"[url={PluginManagerViewModel.WelcomeMeta}]res://addons/plugin/README.md[/url]");
        var problem = welcome with { Problem = "The welcome file [x] is missing." };
        Assertions.AssertString(PluginManagerViewModel.WelcomeLine(problem)).Contains("[lb]x]");
        Assertions.AssertString(PluginManagerViewModel.WelcomeLine(problem)).NotContains("[url");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DialogSceneHasTheNodesItsScriptBinds()
    {
        var dialog = Godot.GD.Load<Godot.PackedScene>(WelcomeDialog.ScenePath).Instantiate<WelcomeDialog>();
        try
        {
            foreach (var name in new[] { "%Summary", "%WelcomeList", "%WelcomeName", "%WelcomeSource", "%WelcomeText" })
                Assertions.AssertObject(dialog.GetNodeOrNull(name)).IsNotNull();
            Assertions.AssertBool(dialog.GetNode<Godot.RichTextLabel>("%WelcomeText").BbcodeEnabled).IsTrue();
            Assertions.AssertInt((int)dialog.AutoTranslateMode).IsEqual((int)Godot.Node.AutoTranslateModeEnum.Disabled);
        }
        finally { dialog.Free(); }
    }
}
