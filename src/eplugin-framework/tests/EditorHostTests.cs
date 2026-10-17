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
}
#endif
