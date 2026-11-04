#if TOOLS
using System;
using System.IO;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>Plugins and GDExtension add-ons in subfolders of the addons folder, found like Godot's Plugins list.</summary>
[TestSuite]
public class AddonLayoutTests : IDisposable
{
    private string _addons = null!;

    [BeforeTest]
    public void Setup()
    {
        _addons = Path.Combine(Path.GetTempPath(), "eplugin-layout-" + Guid.NewGuid().ToString("N"), "addons");
        Directory.CreateDirectory(_addons);
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_addons);
        if (root is not null && Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }

    private void Plugin(string slug) => Write(slug + "/plugin.cfg", "[plugin]\nname=\"x\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n");

    private void Extension(string file) => Write(file, "[configuration]\nentry_symbol=\"init\"\n");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_addons, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [TestCase]
    public void PluginsAreFoundInSubfoldersButNotInsideAnotherPlugin()
    {
        Plugin("top");
        Plugin("acme/tool");
        Plugin("acme/deep/er/tool");
        // Godot does not look inside a plugin's folder: a sub-plugin belongs to its plugin
        Plugin("top/sub");
        // nor into hidden folders
        Plugin(".hidden");
        Plugin("acme/.inert/tool");
        Directory.CreateDirectory(Path.Combine(_addons, "empty", "folder"));

        var layout = AddonLayout.Scan(_addons);

        Assertions.AssertThat(layout.Plugins).ContainsExactly("acme/deep/er/tool", "acme/tool", "top");
        Assertions.AssertThat(layout.ExtensionAddons).IsEmpty();
        Assertions.AssertThat(AddonLayout.SubPlugins(Path.Combine(_addons, "top"))).ContainsExactly("sub");
        Assertions.AssertThat(AddonLayout.SubPlugins(Path.Combine(_addons, "acme", "tool"))).IsEmpty();
    }

    [TestCase]
    public void AFolderHoldingAGDExtensionPluginIsNoGDExtensionAddon()
    {
        // the GDExtension belongs to the plugin below acme, which Godot lists as acme/native_tool
        Plugin("acme/native_tool");
        Extension("acme/native_tool/native_tool.gdextension");

        var layout = AddonLayout.Scan(_addons);

        Assertions.AssertThat(layout.Plugins).ContainsExactly("acme/native_tool");
        Assertions.AssertThat(layout.ExtensionAddons).IsEmpty();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "acme")).IsFalse();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "acme/native_tool")).IsFalse();
    }

    [TestCase]
    public void AGDExtensionAddonCanBeNextToPluginsInASubfolder()
    {
        Plugin("acme/tool");
        Extension("acme/servo/bin/servo.gdextension");
        // a GDExtension directly in a folder that holds plugins belongs to neither
        Extension("acme/loose.gdextension");

        var layout = AddonLayout.Scan(_addons);

        Assertions.AssertThat(layout.Plugins).ContainsExactly("acme/tool");
        Assertions.AssertThat(layout.ExtensionAddons).ContainsExactly("acme/servo");
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "acme/servo")).IsTrue();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "acme")).IsFalse();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "acme/servo/bin")).IsFalse();
    }

    [TestCase]
    public void AGDExtensionAddonIsItsOutermostFolder()
    {
        Extension("servo/native/servo.gdextension");
        // a plugin.cfg in a hidden folder is not seen by Godot, so it does not make the folder hold a plugin
        Plugin("servo/.demo");
        // a folder whose only GDExtension is hidden is no add-on
        Extension("inert/.native/inert.gdextension");

        var layout = AddonLayout.Scan(_addons);

        Assertions.AssertThat(layout.Plugins).IsEmpty();
        Assertions.AssertThat(layout.ExtensionAddons).ContainsExactly("servo");
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "servo")).IsTrue();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "servo/native")).IsFalse();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "inert")).IsFalse();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "missing")).IsFalse();
        Assertions.AssertBool(AddonLayout.IsExtensionAddon(_addons, "../servo")).IsFalse();
    }

    [TestCase]
    public static void SlugsArePathsBelowTheAddonsFolder()
    {
        Assertions.AssertBool(AddonLayout.IsValidSlug("tool")).IsTrue();
        Assertions.AssertBool(AddonLayout.IsValidSlug("acme/tool")).IsTrue();
        foreach (var invalid in new[] { "", " ", ".", "..", "acme/", "/tool", "acme//tool", "acme/../tool", "acme\\tool", "c:tool" })
            Assertions.AssertBool(AddonLayout.IsValidSlug(invalid)).OverrideFailureMessage($"'{invalid}' is no slug").IsFalse();
        // the state files have always accepted ':' in a slug
        Assertions.AssertBool(AddonLayout.IsStoredSlug("a:b/tool")).IsTrue();
        Assertions.AssertBool(AddonLayout.IsStoredSlug("acme/../tool")).IsFalse();

        Assertions.AssertBool(AddonLayout.IsNested("acme/tool")).IsTrue();
        Assertions.AssertBool(AddonLayout.IsNested("tool")).IsFalse();
        Assertions.AssertString(AddonLayout.Leaf("acme/tool")).IsEqual("tool");
        Assertions.AssertString(AddonLayout.Leaf("tool")).IsEqual("tool");

        Assertions.AssertString(AddonLayout.SlugOf("res://addons/acme/tool")).IsEqual("acme/tool");
        Assertions.AssertString(AddonLayout.SlugOf("res://addons/tool/")).IsEqual("tool");
        Assertions.AssertObject(AddonLayout.SlugOf("res://addons")).IsNull();
        Assertions.AssertObject(AddonLayout.SlugOf("res://tools/tool")).IsNull();

        Assertions.AssertString(AddonLayout.SlugOfConfig("res://addons/acme/tool/plugin.cfg")).IsEqual("acme/tool");
        Assertions.AssertString(AddonLayout.SlugOfConfig("res://addons/tool/plugin.cfg")).IsEqual("tool");
        Assertions.AssertObject(AddonLayout.SlugOfConfig("res://tools/tool/plugin.cfg")).IsNull();
        Assertions.AssertObject(AddonLayout.SlugOfConfig("res://addons/plugin.cfg")).IsNull();
        Assertions.AssertObject(AddonLayout.SlugOfConfig("res://addons/tool/other.cfg")).IsNull();
    }
}
#endif
