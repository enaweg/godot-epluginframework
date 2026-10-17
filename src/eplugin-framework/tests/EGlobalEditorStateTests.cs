#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Sample;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// What <see cref="EGlobal"/> reads from the editor with a fake one: the plugins the ePlugin Manager lists and the
/// status of its toolbar button, the update targets, the contexts rebuilt after an assembly reload, and the public
/// <see cref="IEEditorPluginExtensions"/> a plugin calls from its own callbacks.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalEditorStateTests
{
    private LifecycleFixture _fixture = null!;
    private readonly List<GodotObject> _objects = [];

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup()
    {
        foreach (var obj in _objects.Where(GodotObject.IsInstanceValid)) obj.Free();
        _objects.Clear();
        _fixture.Dispose();
    }

    private T Track<T>(T obj) where T : GodotObject
    {
        _objects.Add(obj);
        return obj;
    }

    private void FailAttempt(string slug, PersistedPluginState target = PersistedPluginState.Activated)
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", target, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
    }

    [TestCase]
    public void TheManagerListsEveryAddonWithAPluginCfgAndFailedAttemptsOfMissingOnes()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), "1.2.0",
            "author=\"Someone\"\ndescription=\"Does things\"\nupdate_url=\"https://example.org/app/releases\"\ndocumentation_url=\"https://example.org/docs\"");
        _fixture.AddPlainPlugin("tool", "0.5");
        Directory.CreateDirectory(Path.Combine(_fixture.Root, "addons", "assets"));
        _fixture.Enable("app");
        FailAttempt("gone");

        var plugins = _fixture.Global.CollectPlugins().ToDictionary(p => p.Slug);

        Assertions.AssertThat(plugins.Keys.Order(StringComparer.Ordinal).ToArray()).ContainsExactly("app", "gone", "tool");
        var app = plugins["app"];
        Assertions.AssertBool(app.Enabled).IsTrue();
        Assertions.AssertString(app.Version).IsEqual("1.2.0");
        Assertions.AssertString(app.Author).IsEqual("Someone");
        Assertions.AssertString(app.Description).IsEqual("Does things");
        Assertions.AssertString(app.UpdateUrl).IsEqual("https://example.org/app/releases");
        Assertions.AssertString(app.DocumentationUrl).IsEqual("https://example.org/docs");
        Assertions.AssertObject(app.SourceUrl).IsNull();
        Assertions.AssertObject(app.State).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertString(app.Recipe!.Nugets.Single().Name).IsEqual("App.Package");
        var tool = plugins["tool"];
        Assertions.AssertBool(tool.Enabled).IsFalse();
        Assertions.AssertString(tool.Version).IsEqual("0.5");
        Assertions.AssertObject(tool.State).IsNull();
        Assertions.AssertObject(tool.UpdateUrl).IsNull();
        var gone = plugins["gone"];
        Assertions.AssertBool(gone.Missing).IsTrue();
        Assertions.AssertObject(gone.Kind).IsEqual(PluginKind.Unknown);
        Assertions.AssertString(gone.Version).IsEqual("1.0.0");
        Assertions.AssertString(gone.FailedAttempt!.Reason).IsEqual("recipe_operation_failed");
    }

    [TestCase]
    public void TheToolbarStatusShowsAFailedPluginAndIsReadAgainOnlyWhenInvalidated()
    {
        _fixture.AddEPlugin("app", null);
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.Normal);

        _fixture.Enable("app");
        // looked at in this frame already
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.Normal);
        _fixture.Global.InvalidateManagerStatus();
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.NeedsAttention);

        _fixture.SetRecipe("app", b => b.AddNuget("App.Package"));
        _fixture.Global.RetryFailedPlugins();
        _fixture.Global.InvalidateManagerStatus();
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.Normal);
    }

    [TestCase]
    public void UpdateTargetsAreTheEnabledPluginsWithTheirRecordedState()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), "1.2.0", "update_url=\"https://example.org/app/releases\"");
        _fixture.AddPlainPlugin("tool", "0.5");
        _fixture.AddPlainPlugin("disabled");
        _fixture.Enable("app");
        _fixture.Host.Enabled.Add("tool");
        FailAttempt("tool");

        var targets = _fixture.Global.CollectUpdateTargets().ToDictionary(t => t.Slug);

        // the framework itself has no plugin.cfg in the fixture's project
        Assertions.AssertThat(targets.Keys.Order(StringComparer.Ordinal).ToArray()).ContainsExactly("app", "tool");
        var app = targets["app"];
        Assertions.AssertString(app.Name).IsEqual("app");
        Assertions.AssertString(app.InstalledVersion).IsEqual("1.2.0");
        Assertions.AssertString(app.RecordedVersion).IsEqual("1.2.0");
        Assertions.AssertString(app.UpdateUrl).IsEqual("https://example.org/app/releases");
        Assertions.AssertString(Path.GetFullPath(app.Directory)).IsEqual(Path.GetFullPath(_fixture.Addon("app")));
        Assertions.AssertBool(app.IsBlocked).IsFalse();
        Assertions.AssertBool(app.StoreReadOnly).IsFalse();
        Assertions.AssertBool(targets["tool"].IsBlocked).IsTrue();
    }

    [TestCase]
    public void AnAssemblyReloadRebuildsTheContextsOfThePluginNodes()
    {
        var sample = Track(new SamplePlugin());
        _fixture.Host.Nodes.AddRange([_fixture.Framework, sample, Track(new EditorPlugin()), Track(new Node())]);

        _fixture.Global.ReloadContexts(null, false);

        var context = _fixture.Global.Contexts.Single(c => c.PluginBase == sample);
        Assertions.AssertString(context.Slug).IsEqual("sample_plugin");
        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertInt(_fixture.Global.Contexts.Count).IsEqual(2);
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Found 2 active plugins."))).IsTrue();
    }

    [TestCase]
    public void AReloadComparesThePluginsWithTheirRecordedState()
    {
        var sample = Track(new SamplePlugin());
        _fixture.Host.Nodes.Add(sample);
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("sample_plugin", "0.9", PersistedPluginState.Deactivated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryComplete(id, [new SharedPluginState("sample_plugin", "0.9", PersistedPluginState.Deactivated)])).IsTrue();

        _fixture.Global.ReloadContexts(null, false);

        Assertions.AssertObject(_fixture.Global.Contexts.Single(c => c.PluginBase == sample).State).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("sample_plugin differs from recorded state (Deactivated, version 0.9)"))).IsTrue();
    }

    [TestCase]
    public void AReloadLeavesAPluginWithAFailedAttemptInError()
    {
        var sample = Track(new SamplePlugin());
        _fixture.Host.Nodes.Add(sample);
        FailAttempt("sample_plugin");

        _fixture.Global.ReloadContexts(null, false);

        Assertions.AssertObject(_fixture.Global.Contexts.Single(c => c.PluginBase == sample).State).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("sample_plugin has unresolved local state"))).IsTrue();
    }

    [TestCase]
    public void EnablingTheFrameworkIsRecordedOnce()
    {
        _fixture.Global.RecordFrameworkEnabled(_fixture.Framework);

        var slug = _fixture.Framework.GetPluginSlug();
        var framework = _fixture.Store.GetShared(slug)!;
        Assertions.AssertObject(framework.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertString(framework.Version).IsNotEmpty();

        var file = File.ReadAllText(Path.Combine(_fixture.Root, "addons", "eplugin-state.json"));
        _fixture.Global.RecordFrameworkEnabled(_fixture.Framework);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_fixture.Root, "addons", "eplugin-state.json"))).IsEqual(file);

        _fixture.Global.RecordFrameworkDisabled(_fixture.Framework);
        _fixture.Global.RecordFrameworkEnabled(_fixture.Framework);
        Assertions.AssertObject(_fixture.Store.GetShared(slug)!.State).IsEqual(PersistedPluginState.Activated);
    }

    [TestCase]
    public void APluginsOwnCallbacksInstallAndRemoveItsRecipeThroughThePublicApi()
    {
        // Godot enables the plugin, then calls its _EnablePlugin, which calls this.EnableEPlugin()
        _fixture.AddEPlugin("sample_plugin", b => b.AddNuget("ZLogger"));
        _fixture.Host.Enabled.Add("sample_plugin");
        var sample = Track(new SamplePlugin());
        _fixture.Global.GetOrCreateContext(sample).Cli = _fixture.Cli;

        using (EGlobal.UseInstance(_fixture.Global))
        {
            sample.EnableEPlugin();
            Assertions.AssertObject(_fixture.Global.GetOrCreateContext(sample).State).IsEqual(EEditorPluginState.Activated);
            Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget ZLogger", "build");

            sample.DisableEPlugin();
            Assertions.AssertObject(_fixture.Global.GetOrCreateContext(sample).State).IsEqual(EEditorPluginState.Deactivated);
            // disabled again, e.g. while it waits for its license: nothing is installed, so nothing is removed
            sample.DisableEPlugin();
            Assertions.AssertObject(sample.Cli()).IsSame(_fixture.Cli);
        }

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget ZLogger", "build", "remove-nuget ZLogger", "build");
        Assertions.AssertObject(EGlobal.Instance).IsNotSame(_fixture.Global);
    }
}
#endif
