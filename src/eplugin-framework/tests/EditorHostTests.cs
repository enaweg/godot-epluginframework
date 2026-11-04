#if TOOLS
using System.Linq;
using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The parts of the real editor host that need no editor: recipes of disabled plugins (read for license reviews) come
/// from the compiled script types of the bundled sample plugins.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EditorHostTests
{
    [TestCase]
    public static void TheRecipeOfADisabledEPluginComesFromItsCompiledType()
    {
        var host = new EditorHost();

        var sample = EEditorPluginBuilder.Create();
        Assertions.AssertBool(host.CreateRecipeOfDisabledPlugin("sample_plugin", sample)).IsTrue();
        Assertions.AssertThat(sample.PluginRecipe.Nugets.Select(n => n.Name).ToArray()).ContainsExactly("ZLogger");

        var dependant = EEditorPluginBuilder.Create();
        Assertions.AssertBool(host.CreateRecipeOfDisabledPlugin("sample_dependant_plugin", dependant)).IsTrue();
        Assertions.AssertThat(dependant.PluginRecipe.PluginDependencies.Select(d => d.Slug).ToArray())
            .ContainsExactly("ePlugin", "sample_plugin");
    }

    [TestCase]
    public static void PluginsWithoutACompiledEPluginHaveNoRecipe()
    {
        var host = new EditorHost();

        // gdUnit4 is a GDScript plugin; the other one does not exist
        foreach (var slug in new[] { "gdUnit4", "missing_plugin" })
        {
            var builder = EEditorPluginBuilder.Create();
            Assertions.AssertBool(host.CreateRecipeOfDisabledPlugin(slug, builder)).IsFalse();
            Assertions.AssertThat(builder.PluginRecipe.PluginDependencies.Select(d => d.Slug).ToArray()).ContainsExactly("ePlugin");
        }

        Assertions.AssertBool(host.IsEPlugin("sample_plugin")).IsTrue();
        Assertions.AssertBool(host.IsEPlugin("gdUnit4")).IsFalse();
        Assertions.AssertBool(host.IsEPlugin("missing_plugin")).IsFalse();
        // an ePlugin script belongs to the plugin whose plugin.cfg is closest to it, not to a folder or plugin holding that
        Assertions.AssertBool(PluginTypes.HasEPluginBelow("res://addons", _ => false)).IsFalse();
        Assertions.AssertBool(PluginTypes.HasEPluginBelow("res://addons", directory => directory == "res://addons")).IsFalse();
        Assertions.AssertBool(PluginTypes.HasEPluginBelow("res://addons/sample_plugin", directory => directory == "res://addons/sample_plugin")).IsTrue();
    }

    [TestCase]
    public static void EnabledPluginsAreNamedByTheirFolderBelowTheAddonsFolder()
    {
        var slugs = EditorHost.ParseEnabledPlugins([
            "res://addons/tool/plugin.cfg", "res://addons/acme/native_tool/plugin.cfg", "res://addons/outer/sub/plugin.cfg",
            // outside the addons folder, which ePlugin does not support
            "res://tools/plugin.cfg", "res://addons/plugin.cfg", "res://addons/tool/other.cfg"
        ]);

        Assertions.AssertThat(slugs.Order(System.StringComparer.Ordinal).ToArray()).ContainsExactly("acme/native_tool", "outer/sub", "tool");
    }

    [TestCase]
    public static void AnAutoloadIsReadWithoutItsSingletonMarker()
    {
        var host = new EditorHost();
        const string name = "EPluginHostTestAutoload";
        ProjectSettings.SetSetting("autoload/" + name, "*res://addons/sample_plugin/service.gd");
        try
        {
            Assertions.AssertString(host.GetAutoload(name)).IsEqual("res://addons/sample_plugin/service.gd");
        }
        finally
        {
            ProjectSettings.Clear("autoload/" + name);
        }

        Assertions.AssertObject(host.GetAutoload(name)).IsNull();
    }

    [TestCase]
    public static void ARestartFreesTheWatcherThatWouldReloadTheAssemblyWhileTheEditorShutsDown()
    {
        // GodotTools adds the watcher as a child of its editor plugin; the other plugin has a node of its own
        var godotTools = new Node { Name = "GodotSharpEditor" };
        var watcher = new Node { Name = "HotReloadAssemblyWatcher" };
        var timer = new Timer();
        godotTools.AddChild(watcher);
        watcher.AddChild(timer);
        var other = new Node { Name = "OtherPlugin" };
        var kept = new Node { Name = "Watcher" };
        other.AddChild(kept);
        try
        {
            Assertions.AssertBool(EditorHost.StopAssemblyWatcher([godotTools, other])).IsTrue();

            Assertions.AssertBool(GodotObject.IsInstanceValid(watcher)).IsFalse();
            Assertions.AssertBool(GodotObject.IsInstanceValid(timer)).IsFalse();
            Assertions.AssertInt(godotTools.GetChildCount()).IsEqual(0);
            Assertions.AssertObject(other.GetNodeOrNull("Watcher")).IsSame(kept);
            // a second restart request finds nothing left to stop
            Assertions.AssertBool(EditorHost.StopAssemblyWatcher([godotTools, other])).IsFalse();
        }
        finally
        {
            godotTools.Free();
            other.Free();
        }
    }
}
#endif
