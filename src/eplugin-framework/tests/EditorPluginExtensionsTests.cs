#if TOOLS
using System;
using System.IO;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EditorPluginExtensionsTests
{
    [TestCase]
    public void APluginWithoutScriptHasNoDirectoryAndIsNamedByItsNode()
    {
        var plugin = new EditorPlugin();
        try
        {
            Assertions.AssertObject(plugin.GetPluginDirectory()).IsNull();
            Assertions.AssertString(plugin.GetPluginSlug()).IsEqual($"plugin-{plugin.GetInstanceId()}");
            plugin.Name = "Named";
            Assertions.AssertString(plugin.GetPluginSlug()).IsEqual("Named");
            Assertions.AssertObject(plugin.ReadMetadata()).IsNull();
        }
        finally
        {
            plugin.Free();
        }
    }

    [TestCase]
    public void TheFrameworkFindsItsDirectoryAndMetadataFromItsScript()
    {
        var framework = new EPluginPlugin();
        try
        {
            Assertions.AssertString(framework.GetPluginDirectory()).IsEqual("res://addons/ePlugin");
            Assertions.AssertString(framework.GetPluginSlug()).IsEqual("ePlugin");
            Assertions.AssertString(framework.ReadMetadata()!.Name).IsEqual("ePlugin");
        }
        finally
        {
            framework.Free();
        }
    }

    [TestCase]
    public void MetadataReadsEveryKeyAndLeavesOptionalOnesNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var full = Path.Combine(directory, "full.cfg");
            File.WriteAllText(full, """
                [plugin]
                name="My Plugin"
                description="Does things"
                author="Someone"
                version="1.2.3"
                script="Plugin.cs"
                update_url="https://github.com/owner/repo/releases"
                documentation_url="https://example.org/docs"
                source_url="git@host:owner/repo.git"
                """);
            var minimal = Path.Combine(directory, "minimal.cfg");
            File.WriteAllText(minimal, "[plugin]\nname=\"Minimal\"\nupdate_url=\"  \"\n");

            var metadata = EditorPluginExtensions.ReadMetadata(full)!;
            Assertions.AssertString(metadata.Name).IsEqual("My Plugin");
            Assertions.AssertString(metadata.Description).IsEqual("Does things");
            Assertions.AssertString(metadata.Author).IsEqual("Someone");
            Assertions.AssertString(metadata.Version).IsEqual("1.2.3");
            Assertions.AssertString(metadata.UpdateUrl).IsEqual("https://github.com/owner/repo/releases");
            Assertions.AssertString(metadata.DocumentationUrl).IsEqual("https://example.org/docs");
            Assertions.AssertString(metadata.SourceUrl).IsEqual("git@host:owner/repo.git");

            var sparse = EditorPluginExtensions.ReadMetadata(minimal)!;
            Assertions.AssertString(sparse.Version).IsEqual("");
            Assertions.AssertObject(sparse.UpdateUrl).IsNull();
            Assertions.AssertObject(sparse.DocumentationUrl).IsNull();
            Assertions.AssertObject(EditorPluginExtensions.ReadMetadata(Path.Combine(directory, "missing.cfg"))).IsNull();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
#endif
