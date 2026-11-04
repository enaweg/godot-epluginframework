#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Plugins in a subfolder of the addons folder (<c>addons/acme/tool</c>, slug <c>acme/tool</c>), which Godot lists in
/// its Plugins list, and sub-plugins inside another plugin's folder, which it does not.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class NestedPluginTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_fixture.Root, "addons", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Extension(string relative) =>
        Write(relative, "[configuration]\nentry_symbol=\"init\"\ncompatibility_minimum=\"4.4\"\n[libraries]\nweb=\"bin/x.wasm\"\n");

    private void Config(string slug, string script) =>
        Write(slug + "/plugin.cfg", $"[plugin]\nname=\"{slug}\"\nversion=\"1.0.0\"\nscript=\"{script}\"\n");

    [TestCase]
    public void TheManagerListsPluginsOfEveryKindInSubfolders()
    {
        _fixture.AddEPlugin("acme/app", b => b.AddNuget("App.Package"));
        _fixture.AddPlainPlugin("acme/tool");
        Config("acme/csharp", "Plugin.cs");
        Config("acme/native", "plugin.gd");
        Extension("acme/native/native.gdextension");
        Extension("acme/servo/servo.gdextension");
        Config("vendor/deep/tool", "plugin.gd");
        _fixture.Enable("acme/app");

        var plugins = _fixture.Global.CollectPlugins();

        Assertions.AssertThat(plugins.Select(p => p.Slug).Order(StringComparer.Ordinal).ToArray()).ContainsExactly(
            "acme/app", "acme/csharp", "acme/native", "acme/servo", "acme/tool", "vendor/deep/tool");
        PluginKind Kind(string slug) => plugins.Single(p => p.Slug == slug).Kind;
        Assertions.AssertObject(Kind("acme/tool")).IsEqual(PluginKind.GDScript);
        Assertions.AssertObject(Kind("acme/csharp")).IsEqual(PluginKind.CSharp);
        Assertions.AssertObject(Kind("acme/native")).IsEqual(PluginKind.GDExtension);
        Assertions.AssertObject(Kind("acme/servo")).IsEqual(PluginKind.GDExtensionAddon);
        var app = plugins.Single(p => p.Slug == "acme/app");
        Assertions.AssertBool(app.Enabled).IsTrue();
        Assertions.AssertObject(app.State).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(app.PartOf).IsNull();
        Assertions.AssertString(plugins.Single(p => p.Slug == "acme/native").Extensions.Single().File).IsEqual("native.gdextension");
    }

    [TestCase]
    public void PluginsInSubfoldersAreNeitherUpdatedNorOfferedUpdates()
    {
        _fixture.AddPlainPlugin("acme/tool", extraConfig: "update_url=\"https://github.com/acme/tool/releases\"");
        _fixture.AddPlainPlugin("top", extraConfig: "update_url=\"https://github.com/acme/top/releases\"");
        Extension("acme/servo/servo.gdextension");
        _fixture.Enable("acme/tool");
        _fixture.Enable("top");

        // updates find and install a package's folder by its slug directly below the addons folder
        Assertions.AssertThat(_fixture.Global.CollectUpdateTargets().Select(t => t.Slug).ToArray()).ContainsExactly("top");
        var tool = new PluginRow(_fixture.Global.CollectPlugins().Single(p => p.Slug == "acme/tool"), null);
        Assertions.AssertBool(tool.IsUpdatable).IsFalse();
        Assertions.AssertString(PluginManagerViewModel.Describe(tool)).Contains("Not updatable: ePlugin updates only plugins directly in res://addons");
    }

    [TestCase]
    public void AnEPluginInASubfolderDependsOnPluginsInSubfolders()
    {
        _fixture.AddPlainPlugin("acme/lib", "1.2.0");
        _fixture.AddPlainPlugin("acme/extra");
        Extension("acme/servo/servo.gdextension");
        _fixture.AddEPlugin("acme/app", b => b.AddNuget("App.Package").AddPluginDependency("acme/lib", ">=1.0")
            .AddPluginDependency("acme/servo").AddOptionalPluginDependency("acme/extra", null, r => r.AddNuget("Extra.Glue")));

        _fixture.Enable("acme/app");
        _fixture.Global.InitializePluginToggles();

        Assertions.AssertObject(_fixture.State("acme/app")).IsEqual(EEditorPluginState.Activated);
        // the plain dependency was enabled by its slug, which Godot reads as res://addons/acme/lib/plugin.cfg
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("acme/lib")).IsTrue();
        // a GDExtension add-on in a subfolder is always there
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("acme/servo")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");

        // the optional plugin, enabled later in Project Settings, gets its nested recipe installed
        _fixture.Host.Enabled.Add("acme/extra");
        Assertions.AssertBool(_fixture.Global.ObservePluginToggles()).IsTrue();
        Assertions.AssertThat(_fixture.Operations).Contains("add-nuget Extra.Glue");

        // disabling the hard dependency in Project Settings disables its dependant
        _fixture.Host.Enabled.Remove("acme/lib");
        Assertions.AssertBool(_fixture.Global.ObservePluginToggles()).IsTrue();
        Assertions.AssertObject(_fixture.State("acme/app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("acme/app")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).Contains("remove-nuget App.Package");
    }

    [TestCase]
    public void AVersionConstraintOnAPluginInASubfolderIsCheckedAgainstItsPluginCfg()
    {
        _fixture.AddPlainPlugin("acme/lib", "1.2.0");
        _fixture.AddEPlugin("strict", b => b.AddPluginDependency("acme/lib", ">=2.0"));

        _fixture.Enable("strict");

        Assertions.AssertObject(_fixture.State("strict")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("acme/lib") && l.Contains(">=2.0"))).IsTrue();
    }

    [TestCase]
    public void TheStateOfPluginsInSubfoldersIsRecordedAndReadAgain()
    {
        _fixture.AddEPlugin("acme/app", b => b.AddNuget("App.Package").AddPluginDependency("acme/lib"));
        _fixture.AddPlainPlugin("acme/lib");

        _fixture.Enable("acme/app");

        var store = new PluginStateStore(Path.Combine(_fixture.Root, "addons", "eplugin-state.json"), null);
        Assertions.AssertBool(store.Load()).IsTrue();
        Assertions.AssertObject(store.GetShared("acme/app")!.State).IsEqual(PersistedPluginState.Activated);
        _fixture.Disable("acme/app");
        Assertions.AssertBool(store.Load()).IsTrue();
        Assertions.AssertObject(store.GetShared("acme/app")!.State).IsEqual(PersistedPluginState.Deactivated);
        // in the list older versions skip, which refused a slug with '/'
        var json = File.ReadAllText(Path.Combine(_fixture.Root, "addons", "eplugin-state.json"));
        Assertions.AssertString(json).Contains("\"nestedPlugins\"");
    }

    [TestCase]
    public void APluginInASubfolderMissingAtAStartIsMarkedAndThenForgotten()
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("acme/tool", null, PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryComplete(id, [new SharedPluginState("acme/tool", "1.0.0", PersistedPluginState.Activated)])).IsTrue();
        // a plugin next to it is still there
        Directory.CreateDirectory(_fixture.Addon("acme/other"));

        _fixture.Global.ReviewMissingPlugins();
        Assertions.AssertBool(_fixture.Store.GetShared("acme/tool")!.Missing).IsTrue();
        _fixture.Global.ReviewMissingPlugins();
        Assertions.AssertObject(_fixture.Store.GetShared("acme/tool")).IsNull();
    }

    [TestCase]
    public void TheLicenseOfAPluginInASubfolderIsRecordedByItsSlug()
    {
        _fixture.AddEPlugin("acme/app", b => b.AddNuget("App.Package"), extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("acme/app"), "LICENSE"), "Terms");
        _fixture.Host.Dialogs = true;

        _fixture.Enable("acme/app");
        EGlobal.RunPosted();
        var review = _fixture.Global.PendingLicenseReview!;
        Assertions.AssertThat(review.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("acme/app");
        review.Accept("acme/app");
        _fixture.Global.CompleteLicenseReview(review);
        EGlobal.RunPosted();

        Assertions.AssertObject(_fixture.State("acme/app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("acme/app", "LICENSE")).IsTrue();
    }

    [TestCase]
    public void TheWelcomePageOfAPluginInASubfolderIsShownOnceByItsSlug()
    {
        _fixture.AddEPlugin("acme/app", b => b.SetWelcome("[b]Hello[/b]"));
        _fixture.AddPlainPlugin("vendor/tool");
        File.WriteAllText(Path.Combine(_fixture.Addon("vendor/tool"), "README.md"), "# Tool");
        _fixture.Host.Dialogs = true;
        _fixture.Enable("acme/app");
        _fixture.Enable("vendor/tool");

        var welcomes = _fixture.Global.TakeWelcomes()!;
        Assertions.AssertThat(welcomes.Select(w => w.Slug).ToArray()).ContainsExactly("acme/app", "vendor/tool");
        _fixture.Global.CompleteWelcomes(welcomes);
        Assertions.AssertBool(_fixture.Store.IsWelcomeShown("acme/app") && _fixture.Store.IsWelcomeShown("vendor/tool")).IsTrue();
    }

    [TestCase]
    public void ASubPluginIsListedOnlyWhileItsPluginEnabledIt()
    {
        _fixture.AddPlainPlugin("outer");
        _fixture.AddPlainPlugin("outer/sub");

        Assertions.AssertThat(_fixture.Global.CollectPlugins().Select(p => p.Slug).ToArray()).ContainsExactly("outer");

        // Godot does not list it, but a plugin may enable its sub-plugin by its slug
        _fixture.Enable("outer");
        _fixture.Enable("outer/sub");
        var sub = _fixture.Global.CollectPlugins().Single(p => p.Slug == "outer/sub");
        Assertions.AssertString(sub.PartOf).IsEqual("outer");
        Assertions.AssertBool(new PluginRow(sub, null).IsUpdatable).IsFalse();
        Assertions.AssertString(PluginManagerViewModel.Describe(new PluginRow(sub, null))).Contains("sub-plugin in the folder of outer");
        // only the plugin Godot lists is updated, and its update installs the sub-plugin with it
        var target = _fixture.Global.CollectUpdateTargets().Single();
        Assertions.AssertString(target.Slug).IsEqual("outer");
        Assertions.AssertThat(target.EnabledSubPlugins).ContainsExactly("sub");
    }
}
#endif
