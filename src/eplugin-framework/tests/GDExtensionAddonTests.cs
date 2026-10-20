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
/// GDExtensions as the editor runs them: GDExtension add-ons without plugin.cfg in the ePlugin Manager, as update
/// targets and as dependencies, the load state of extensions in the manager, and GDExtensions in managed directories.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class GDExtensionAddonTests : IDisposable
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

    private static string Extension(bool reloadable = true, string slug = "servo") => $"""
        [configuration]
        entry_symbol = "{slug}_init"
        compatibility_minimum = "4.4"
        reloadable = {(reloadable ? "true" : "false")}

        [libraries]
        windows = "bin/{slug}.dll"
        linux = "bin/lib{slug}.so"
        macos = "bin/lib{slug}.dylib"
        """;

    /// <summary>Writes a GDExtension with its libraries to <paramref name="directory"/>; returns its .gdextension file.</summary>
    private static string WriteExtension(string directory, string slug = "servo", bool reloadable = true)
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        var file = Path.Combine(directory, slug + ".gdextension");
        File.WriteAllText(file, Extension(reloadable, slug));
        foreach (var name in new[] { $"{slug}.dll", $"lib{slug}.so", $"lib{slug}.dylib" }) File.WriteAllText(Path.Combine(directory, "bin", name), "library");
        return file;
    }

    private void Record(string slug, string version)
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, null, PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryComplete(id, [new SharedPluginState(slug, version, PersistedPluginState.Activated)])).IsTrue();
    }

    [TestCase]
    public void TheManagerListsAGDExtensionWithoutPluginCfgAsAlwaysLoaded()
    {
        var file = WriteExtension(_fixture.Addon("servo"));
        // a folder whose only GDExtension is hidden is no add-on
        WriteExtension(Path.Combine(_fixture.Addon("inert"), ".native"), "inert");

        var servo = _fixture.Global.CollectPlugins().Single(p => p.Slug == "servo");
        Assertions.AssertBool(_fixture.Global.CollectPlugins().Any(p => p.Slug == "inert")).IsFalse();
        Assertions.AssertObject(servo.Kind).IsEqual(PluginKind.GDExtensionAddon);
        Assertions.AssertBool(servo.Enabled).IsTrue();
        Assertions.AssertString(servo.Version).IsEmpty();
        var row = new PluginRow(servo, null);
        Assertions.AssertBool(row.CanToggle).IsFalse();
        Assertions.AssertString(PluginManagerViewModel.VersionText(servo)).IsEqual("unknown");
        Assertions.AssertString(PluginManagerViewModel.StatusText(servo)).StartsWith("Always loaded");
        Assertions.AssertString(servo.Extensions.Single().File).IsEqual("servo.gdextension");
        Assertions.AssertBool(servo.Extensions.Single().Config!.Reloadable).IsTrue();
        // its library is there, but Godot has not loaded it
        Assertions.AssertBool(row.NeedsAttention).IsTrue();
        Assertions.AssertString(PluginManagerViewModel.Describe(row)).Contains("not loaded");

        _fixture.Host.LoadExtension(file);
        servo = _fixture.Global.CollectPlugins().Single(p => p.Slug == "servo");
        Assertions.AssertBool(new PluginRow(servo, null).NeedsAttention).IsFalse();
        Assertions.AssertString(PluginManagerViewModel.Describe(new PluginRow(servo, null))).Contains("servo.gdextension: loaded, reloadable");
    }

    [TestCase]
    public void AGDExtensionWithoutPluginCfgIsAnUpdateTargetWithItsRecordedVersion()
    {
        WriteExtension(_fixture.Addon("servo"));

        var target = _fixture.Global.CollectUpdateTargets().Single(t => t.Slug == "servo");
        Assertions.AssertObject(target.Kind).IsEqual(PluginKind.GDExtensionAddon);
        Assertions.AssertString(target.InstalledVersion).IsEmpty();
        Assertions.AssertObject(target.UpdateUrl).IsNull();

        Record("servo", "1.2.0");
        target = _fixture.Global.CollectUpdateTargets().Single(t => t.Slug == "servo");
        Assertions.AssertString(target.InstalledVersion).IsEqual("1.2.0");
        Assertions.AssertString(_fixture.Global.CollectPlugins().Single(p => p.Slug == "servo").Version).IsEqual("1.2.0");
    }

    [TestCase]
    public void TheToolbarTurnsRedWhileGodotDoesNotLoadAGDExtensionItShould()
    {
        _fixture.AddPlainPlugin("native");
        var file = WriteExtension(_fixture.Addon("native"), "native");
        _fixture.Global.InvalidateManagerStatus();
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.NeedsAttention);

        _fixture.Host.LoadExtension(file);
        _fixture.Global.InvalidateManagerStatus();
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.Normal);

        // an extension without a library for this platform is meant for others
        _fixture.Host.UnloadExtension(file);
        File.WriteAllText(file, "[configuration]\ncompatibility_minimum = \"4.4\"\n[libraries]\nweb = \"bin/native.wasm\"\n");
        _fixture.Global.InvalidateManagerStatus();
        Assertions.AssertObject(_fixture.Global.CurrentManagerStatus()).IsEqual(ManagerStatus.Normal);
    }

    [TestCase]
    public void AnEPluginCanDependOnAGDExtensionWithoutPluginCfg()
    {
        WriteExtension(_fixture.Addon("servo"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("servo"));
        _fixture.AddEPlugin("glue", b => b.AddOptionalPluginDependency("servo", null, r => r.AddNuget("Servo.Glue")));

        _fixture.Enable("app");
        _fixture.Enable("glue");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.State("glue")).IsEqual(EEditorPluginState.Activated);
        // Godot runs it anyway: nothing tried to enable it
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("servo")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).Contains("add-nuget Servo.Glue");
    }

    [TestCase]
    public void AVersionConstraintOnAGDExtensionWithoutPluginCfgNeedsItsRecordedVersion()
    {
        WriteExtension(_fixture.Addon("servo"));
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("servo", ">=1.0"));

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("The version of the GDExtension servo is unknown"))).IsTrue();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_resolution_failed");
    }

    [TestCase]
    public void AVersionConstraintOnAGDExtensionWithoutPluginCfgIsCheckedAgainstItsRecordedVersion()
    {
        WriteExtension(_fixture.Addon("servo"));
        Record("servo", "1.2.0");
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("servo", ">=1.0"));
        _fixture.AddEPlugin("strict", b => b.AddPluginDependency("servo", ">=2.0"));

        _fixture.Enable("app");
        _fixture.Enable("strict");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.State("strict")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Dependency servo 1.2.0 does not match needed >=2.0"))).IsTrue();
    }

    [TestCase]
    public void AManagedDirectoryLoadsItsReloadableGDExtensionWhenShownAndUnloadsItBeforeHiding()
    {
        var hidden = Path.Combine(_fixture.Addon("a"), ".native");
        WriteExtension(hidden, "native");
        _fixture.AddEPlugin("a", b => b.AddDirectory(hidden));

        _fixture.Enable("a");

        var shown = Path.Combine(_fixture.Addon("a"), "native", "native.gdextension");
        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Host.IsExtensionLoaded(shown)).IsTrue();

        _fixture.Disable("a");

        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertBool(_fixture.Host.IsExtensionLoaded(shown)).IsFalse();
        Assertions.AssertBool(Directory.Exists(hidden)).IsTrue();
    }

    [TestCase]
    public void AManagedDirectoryWithAGDExtensionThatIsNotReloadableIsNotShown()
    {
        var hidden = Path.Combine(_fixture.Addon("a"), ".native");
        WriteExtension(hidden, "native", reloadable: false);
        _fixture.AddEPlugin("a", b => b.AddDirectory(hidden));

        _fixture.Enable("a");

        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Context("a")!.ErrorDetail!.Message).Contains("not reloadable");
        Assertions.AssertBool(Directory.Exists(hidden)).IsTrue();
        Assertions.AssertInt(_fixture.Host.Extensions.Count).IsEqual(0);
    }
}
#endif
