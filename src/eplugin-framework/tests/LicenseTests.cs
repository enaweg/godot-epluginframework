using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class LicenseTests
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
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-license-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static LicenseEntry Entry(string slug, string source = "LICENSE") => new(slug, slug, "1.0.0", source, "text");

    private static AcceptedLicense Accepted(string slug, string license = "LICENSE", bool automatic = false) =>
        new(slug, license, "1.0.0", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), automatic);

    private PluginStateStore StoreWithBaseline(out string path)
    {
        path = Path.Combine(TempDirectory(), "eplugin-state.json");
        var store = new PluginStateStore(path, new NullLogger());
        store.Load();
        store.TryCreateBaseline([new SharedPluginState("plugin", "1.0.0", PersistedPluginState.Activated)]);
        return store;
    }

    [TestCase]
    public void ReadsConfiguredFileWithBbCode()
    {
        var directory = TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "docs"));
        File.WriteAllText(Path.Combine(directory, "docs", "EULA.txt"), "[b]Terms[/b]");
        var entry = PluginLicense.FromConfig("plugin", "Plugin", "1.0.0", "docs\\EULA.txt", directory);
        Assertions.AssertString(entry.Source).IsEqual("docs/EULA.txt");
        Assertions.AssertString(entry.Text).IsEqual("[b]Terms[/b]");
        Assertions.AssertString(entry.Problem).IsNull();
        // a res:// path into the plugin's own directory names the same file
        Assertions.AssertString(PluginLicense.FromConfig("plugin", "Plugin", "1.0.0", "res://addons/plugin/docs/EULA.txt", directory).Source)
            .IsEqual("docs/EULA.txt");
    }

    [TestCase]
    public void FallsBackToTheLicenseFileOnly()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "LICENSE.md"), "not the fallback");
        var missing = PluginLicense.FromConfig("plugin", "", "1.0.0", "", directory);
        Assertions.AssertString(missing.Source).IsEqual("LICENSE");
        Assertions.AssertString(missing.Name).IsEqual("plugin");
        Assertions.AssertString(missing.Problem).Contains("missing");
        File.WriteAllText(Path.Combine(directory, "LICENSE"), "MIT");
        var found = PluginLicense.FromConfig("plugin", "Plugin", "1.0.0", null, directory);
        Assertions.AssertString(found.Text).IsEqual("MIT");
        Assertions.AssertString(found.Problem).IsNull();
    }

    [TestCase]
    public void RejectsLicenseFilesOutsideThePluginDirectory()
    {
        var directory = TempDirectory();
        foreach (var path in new[] { "../LICENSE", "/etc/passwd", "res://addons/other/LICENSE", "C:/LICENSE" })
        {
            var entry = PluginLicense.FromConfig("plugin", "Plugin", "1.0.0", path, directory);
            Assertions.AssertString(entry.Problem).IsNotNull();
            Assertions.AssertString(entry.Text).IsEmpty();
        }
    }

    [TestCase]
    public void RecipeTextIsIdentifiedByItsContentsAndPathsByThePath()
    {
        var first = PluginLicense.FromText("plugin", "Plugin", "1.0.0", "Terms\r\nv1");
        Assertions.AssertString(first.Source).StartsWith(PluginLicense.TextPrefix);
        Assertions.AssertString(PluginLicense.FromText("plugin", "Plugin", "2.0.0", "Terms\nv1").Source).IsEqual(first.Source);
        Assertions.AssertString(PluginLicense.FromText("plugin", "Plugin", "1.0.0", "Terms\nv2").Source).IsNotEqual(first.Source);
        Assertions.AssertString(PluginLicense.Display("plugin", first.Source)).IsEqual("Set by the plugin");
        Assertions.AssertString(PluginLicense.SourceOfPath("plugin", "res://addons/plugin/legal/EULA.txt")).IsEqual("legal/EULA.txt");
        Assertions.AssertString(PluginLicense.SourceOfPath("plugin", "res://legal/EULA.txt")).IsEqual("res://legal/EULA.txt");
        Assertions.AssertString(PluginLicense.Display("plugin", "legal/EULA.txt")).IsEqual("res://addons/plugin/legal/EULA.txt");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void BuilderTellsLicensePathsFromLicenseTexts()
    {
        var builder = EEditorPluginBuilder.Create();
        builder.SetLicense("res://addons/plugin/EULA.txt");
        Assertions.AssertString(builder.PluginRecipe.PluginLicense!.Path).IsEqual("res://addons/plugin/EULA.txt");
        Assertions.AssertString(builder.PluginRecipe.PluginLicense.Text).IsNull();
        builder.SetLicense("[b]MIT License[/b]\nres://not/a/path");
        Assertions.AssertString(builder.PluginRecipe.PluginLicense!.Text).IsEqual("[b]MIT License[/b]\nres://not/a/path");
        Assertions.AssertString(builder.PluginRecipe.PluginLicense.Path).IsNull();
        Assertions.AssertThrown(() => builder.SetLicense(" ")).IsInstanceOf<ArgumentException>();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AcceptedLicensesAreSavedInThePluginStateFile()
    {
        var store = StoreWithBaseline(out var path);
        Assertions.AssertString(File.ReadAllText(path)).NotContains("licenses");
        Assertions.AssertBool(store.TryBeginAttempt("plugin", "1.0.0", PersistedPluginState.Deactivated, out var id)).IsTrue();
        Assertions.AssertBool(store.TryComplete(id, [new("plugin", "1.0.0", PersistedPluginState.Deactivated)])).IsTrue();

        Assertions.AssertBool(store.TryRecordLicenses([Accepted("plugin"), Accepted("other", "text:abc", automatic: true)])).IsTrue();
        var reloaded = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(reloaded.Load()).IsTrue();
        Assertions.AssertBool(reloaded.IsLicenseAccepted("plugin", "LICENSE")).IsTrue();
        Assertions.AssertBool(reloaded.IsLicenseAccepted("plugin", "LICENSE-2.md")).IsFalse();
        Assertions.AssertBool(reloaded.GetLicense("other")!.Automatic).IsTrue();
        // recording a license keeps the completed attempt and plugin states, and completing keeps the licenses
        Assertions.AssertObject(reloaded.LastCompletedAttemptId).IsEqual(id);
        Assertions.AssertObject(reloaded.GetShared("plugin")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertBool(reloaded.TryBeginAttempt("plugin", "1.0.0", PersistedPluginState.Activated, out var next)).IsTrue();
        Assertions.AssertBool(reloaded.TryComplete(next, [new("plugin", "1.0.0", PersistedPluginState.Activated)])).IsTrue();
        var again = new PluginStateStore(path, new NullLogger());
        again.Load();
        Assertions.AssertBool(again.IsLicenseAccepted("plugin", "LICENSE")).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void LicensesCountForTheSessionWhenTheyCannotBeSaved()
    {
        var path = Path.Combine(TempDirectory(), "eplugin-state.json");
        var store = new PluginStateStore(path, new NullLogger());
        store.Load();
        // no baseline yet: writing a license must not create the shared file in its place
        Assertions.AssertBool(store.TryRecordLicenses([Accepted("plugin")])).IsFalse();
        Assertions.AssertBool(store.IsLicenseAccepted("plugin", "LICENSE")).IsTrue();
        Assertions.AssertBool(File.Exists(path)).IsFalse();

        var changed = StoreWithBaseline(out var shared);
        File.AppendAllText(shared, " ");
        Assertions.AssertBool(changed.TryRecordLicenses([Accepted("plugin")])).IsFalse();
        Assertions.AssertBool(changed.IsLicenseAccepted("plugin", "LICENSE")).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void InvalidLicenseEntriesMakeTheStateReadOnly()
    {
        var path = Path.Combine(TempDirectory(), "eplugin-state.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "plugins": [], "licenses": [ { "slug": "../x", "license": "LICENSE" } ] }
            """);
        var store = new PluginStateStore(path, new NullLogger());
        Assertions.AssertBool(store.Load()).IsFalse();
        Assertions.AssertBool(store.IsReadOnly).IsTrue();
    }

    [TestCase]
    public void DetailsLinkTheLicenseOrShowItMissing()
    {
        var missing = new LicenseInfo(Entry("plugin") with { Problem = "missing" }, false, null);
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(missing)).Contains("missing (no LICENSE file");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(missing)).NotContains("[url");
        var plain = new LicenseInfo(Entry("plugin"), false, null);
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(plain))
            .Contains($"[url={PluginManagerViewModel.LicenseMeta}]res://addons/plugin/LICENSE[/url]");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(plain)).NotContains("accepted");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(plain with { Required = true })).Contains("(not accepted yet)");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(plain with { Required = true, Accepted = Accepted("plugin", "OLD") }))
            .Contains("an earlier license was");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(plain with { Required = true, Accepted = Accepted("plugin", automatic: true) }))
            .Contains("automatically");
        var problem = new LicenseInfo(Entry("plugin", "docs/EULA") with { Problem = "Cannot read it" }, true, null);
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(problem)).Contains("Cannot read it");
        Assertions.AssertString(PluginManagerViewModel.LicenseLine(null)).IsEmpty();
    }

    [TestCase]
    public void DecliningADependencyLicenseCancelsAndSkipsItsDependant()
    {
        var review = new LicenseReview([Entry("dependency"), Entry("plugin")],
            [new LicenseActivation("plugin", "Plugin", ["dependency", "plugin"])]);
        Assertions.AssertString(review.NextPending()!.Slug).IsEqual("dependency");
        Assertions.AssertArray(review.NeededBy("dependency").Select(a => a.Slug).ToArray()).IsEqual(new[] { "plugin" });
        review.Decline("dependency");
        Assertions.AssertBool(review.IsComplete).IsTrue();
        Assertions.AssertObject(review.DecisionOf("plugin")).IsEqual(LicenseDecision.Skipped);
        Assertions.AssertInt(review.Approved.Count()).IsEqual(0);
        Assertions.AssertInt(review.Accepted.Count()).IsEqual(0);
        Assertions.AssertArray(review.Canceled.Select(a => a.Slug).ToArray()).IsEqual(new[] { "plugin" });
    }

    [TestCase]
    public void SeparateActivationsAreDecidedOnTheirOwn()
    {
        var review = new LicenseReview([Entry("one"), Entry("two"), Entry("three")],
            [new LicenseActivation("one", "One", ["one"]), new LicenseActivation("two", "Two", ["two"], Enabled: true),
             new LicenseActivation("three", "Three", ["three"])], isUpdate: true);
        review.Accept("one");
        Assertions.AssertString(review.NextPending("one")!.Slug).IsEqual("two");
        review.Decline("two");
        Assertions.AssertInt(review.PendingCount).IsEqual(1);
        review.AcceptAll();
        Assertions.AssertBool(review.IsComplete).IsTrue();
        Assertions.AssertArray(review.Approved.Select(a => a.Slug).ToArray()).IsEqual(new[] { "one", "three" });
        Assertions.AssertArray(review.Accepted.Select(e => e.Slug).ToArray()).IsEqual(new[] { "one", "three" });
        Assertions.AssertBool(review.Canceled.Single().Enabled).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DialogSceneHasTheNodesItsScriptBinds()
    {
        var dialog = Godot.GD.Load<Godot.PackedScene>(LicenseDialog.ScenePath).Instantiate<LicenseDialog>();
        try
        {
            foreach (var name in new[] { "%Summary", "%LicenseList", "%LicenseName", "%LicenseSource", "%LicenseText", "%NeededBy" })
                Assertions.AssertObject(dialog.GetNodeOrNull(name)).IsNotNull();
            Assertions.AssertBool(dialog.GetNode<Godot.RichTextLabel>("%LicenseText").BbcodeEnabled).IsTrue();
        }
        finally { dialog.Free(); }
    }

    [TestCase]
    public void ClosingDeclinesWhatIsLeftAndMergingKeepsDecisions()
    {
        var first = new LicenseReview([Entry("one")], [new LicenseActivation("one", "One", ["one"])]);
        first.Accept("one");
        var merged = first.Merge(new LicenseReview([Entry("one"), Entry("two")],
            [new LicenseActivation("two", "Two", ["one", "two"])]));
        Assertions.AssertInt(merged.Entries.Count).IsEqual(2);
        Assertions.AssertObject(merged.DecisionOf("one")).IsEqual(LicenseDecision.Accepted);
        merged.DeclineRemaining();
        Assertions.AssertArray(merged.Approved.Select(a => a.Slug).ToArray()).IsEqual(new[] { "one" });
        Assertions.AssertArray(merged.Canceled.Select(a => a.Slug).ToArray()).IsEqual(new[] { "two" });
    }
}
