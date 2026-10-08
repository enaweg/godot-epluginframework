using System;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class PluginManagerViewModelTests
{
    [TestCase]
    public void BlockedAndFailedVersionsAreNotPreselected()
    {
        var cache = new UpdateCache(); cache.FailedUpdates["failed"] = [new("2.0", "build failed", DateTimeOffset.UtcNow)];
        var candidates = new[] { Candidate("blocked"), Candidate("failed"), Candidate("healthy") };
        var targets = candidates.Select(c => new PluginUpdateTarget(c.Slug, c.PluginName, c.InstalledVersion, c.SourceUrl, "/unused", IsBlocked: c.Slug == "blocked")).ToArray();
        var model = new PluginManagerViewModel([], candidates, targets, cache, _ => []);
        Assertions.AssertInt(model.Selected.Count).IsEqual(1);
        Assertions.AssertString(model.Selected[0].Slug).IsEqual("healthy");
        Assertions.AssertBool(model.Updates.Single(r => r.Candidate.Slug == "blocked").HasError).IsTrue();
        Assertions.AssertObject(model.Updates.Single(r => r.Candidate.Slug == "failed").Failed!).IsNotNull();
    }
    [TestCase]
    public void SourceTrustIsRequiredOnlyForSelectedRows()
    {
        var candidate = Candidate("plugin");
        var model = new PluginManagerViewModel([], [candidate], [new("plugin", "Plugin", "1.0", candidate.SourceUrl, "/unused")], null,
            _ => [new("R9", FindingSeverity.Warning, "Source host changed", true)]);
        Assertions.AssertBool(model.CanApply).IsFalse();
        model.TrustChangedSource = true;
        Assertions.AssertBool(model.CanApply).IsTrue();
        model.Updates[0].Selected = false;
        Assertions.AssertBool(model.RequiresTrust).IsFalse();
        Assertions.AssertBool(model.CanApply).IsFalse();
    }
    [TestCase]
    public void MissingTargetCannotBeAppliedAndFrameworkAnnouncesRestart()
    {
        var model = new PluginManagerViewModel([], [Candidate("missing"), Candidate("ePlugin")],
            [new("ePlugin", "Framework", "1.0", "https://example.org/releases", "/unused", RecordedVersion: "0.9")], null, _ => []);
        Assertions.AssertBool(model.Updates[0].HasError).IsTrue();
        Assertions.AssertBool(model.Updates[1].Findings.Any(f => f.Code == "restart")).IsTrue();
        Assertions.AssertBool(model.Updates[1].Findings.Any(f => f.Code == "recorded_version" && f.Severity == FindingSeverity.Info)).IsTrue();
    }
    [TestCase]
    public void ListsAllPluginsFrameworkFirstAndJoinsUpdates()
    {
        var candidate = Candidate("beta");
        var model = new PluginManagerViewModel(
            [Plugin("gamma", PluginKind.GDScript, enabled: false), Plugin("beta", PluginKind.EPlugin, "https://example.org/releases"),
             Plugin("ePlugin", PluginKind.Framework), Plugin("Alpha", PluginKind.CSharp)],
            [candidate, Candidate("orphan")], [new("beta", "beta", "1.0.0", candidate.SourceUrl, "/unused")], null, _ => []);
        Assertions.AssertArray(model.Plugins.Select(p => p.Plugin.Slug).ToArray()).IsEqual(new[] { "ePlugin", "Alpha", "beta", "gamma", "orphan" });
        var beta = model.Find("beta")!;
        Assertions.AssertBool(beta.HasUpdate && beta.IsUpdatable && beta.IsEPlugin).IsTrue();
        Assertions.AssertBool(model.Find("gamma")!.IsUpdatable || model.Find("gamma")!.IsEPlugin).IsFalse();
        Assertions.AssertBool(model.Find("orphan")!.Plugin.Missing).IsTrue();
        Assertions.AssertBool(model.CanRetry).IsFalse();
    }
    [TestCase]
    public void FailedAttemptEnablesRetryAndIsShownInStatus()
    {
        var attempt = new LocalPluginAttempt(Guid.NewGuid(), "broken", "1.0", PersistedPluginState.Activated, PersistedPluginState.Failed, "build_failed");
        var model = new PluginManagerViewModel([Plugin("broken", PluginKind.EPlugin) with { FailedAttempt = attempt }], [], [], null, _ => []);
        Assertions.AssertBool(model.CanRetry).IsTrue();
        Assertions.AssertString(PluginManagerViewModel.StatusText(model.Plugins[0].Plugin)).Contains("needs retry: build_failed");
    }
    [TestCase]
    public void DetailsShowVersionChangeAndEscapePluginText()
    {
        var candidate = Candidate("beta");
        var recipe = new EEditorPluginRecipe();
        recipe.PluginDependencies.Add(new("ePlugin", null)); recipe.PluginDependencies.Add(new("other", "1.2"));
        recipe.Nugets.Add(new("Newtonsoft.Json", "13.0.3", null));
        var model = new PluginManagerViewModel([Plugin("beta", PluginKind.EPlugin) with { Description = "[b]bold[/b]", Recipe = recipe }],
            [candidate], [new("beta", "beta", "1.0.0", candidate.SourceUrl, "/unused")], null, _ => []);
        var details = PluginManagerViewModel.Describe(model.Plugins[0]);
        Assertions.AssertString(details).Contains("1.0.0 → [color=#70e070]2.0.0[/color]");
        Assertions.AssertString(details).Contains("[lb]b]bold[lb]/b]");
        Assertions.AssertString(details).Contains("• other 1.2");
        Assertions.AssertString(details).Contains("• Newtonsoft.Json 13.0.3");
        Assertions.AssertString(details).NotContains("• ePlugin");
        Assertions.AssertString(PluginManagerViewModel.Describe(new(Plugin("plain", PluginKind.GDScript), null))).Contains("no update_url");
    }
    [TestCase]
    public void ClassifiesPluginScripts()
    {
        var types = typeof(PluginCatalog).Assembly.GetTypes();
        Assertions.AssertObject(PluginCatalog.Classify("res://addons/sample_plugin/SamplePlugin.cs", false, types)).IsEqual(PluginKind.EPlugin);
        Assertions.AssertObject(PluginCatalog.Classify("res://addons/ePlugin/EPluginPlugin.cs", true, types)).IsEqual(PluginKind.Framework);
        Assertions.AssertObject(PluginCatalog.Classify("res://addons/gdUnit4/plugin.gd", false, types)).IsEqual(PluginKind.GDScript);
        Assertions.AssertObject(PluginCatalog.Classify("res://addons/none/Missing.cs", false, types)).IsEqual(PluginKind.CSharp);
        Assertions.AssertObject(PluginCatalog.Classify(null, false, types)).IsEqual(PluginKind.Unknown);
        Assertions.AssertString(PluginCatalog.ResolveScript("res://addons/x", "Plugin.cs")).IsEqual("res://addons/x/Plugin.cs");
        Assertions.AssertString(PluginCatalog.ResolveScript("res://addons/x", "res://other/Plugin.cs")).IsEqual("res://other/Plugin.cs");
    }
    private static PluginInfo Plugin(string slug, PluginKind kind, string? updateUrl = null, bool enabled = true) =>
        new(slug, slug, kind, enabled) { Version = "1.0.0", UpdateUrl = updateUrl };
    private static UpdateCandidate Candidate(string slug) => new(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));
}
