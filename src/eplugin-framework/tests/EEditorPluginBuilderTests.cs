using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class EEditorPluginBuilderTests
{
    [TestCase]
    [RequireGodotRuntime]
    public void CreateAddsImplicitEPluginSelfDependency()
    {
        var builder = EEditorPluginBuilder.Create();

        Assertions.AssertThat(builder.PluginRecipe.PluginDependencies)
            .ContainsExactly(new EEditorPluginRecipe.Plugin("ePlugin", null));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddAutoloadAddsEntryAndReturnsSameBuilder()
    {
        var builder = EEditorPluginBuilder.Create();

        var result = builder.AddAutoload("MyGlobal", "res://addons/my-plugin/MyGlobal.cs");

        Assertions.AssertObject(result).IsSame(builder);
        Assertions.AssertThat(builder.PluginRecipe.Autoloads)
            .Contains(new EEditorPluginRecipe.Autoload("MyGlobal", "res://addons/my-plugin/MyGlobal.cs"));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddPluginDependencyWithoutVersionAddsUnconstrainedDependency()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddPluginDependency("some-plugin");

        Assertions.AssertThat(builder.PluginRecipe.PluginDependencies)
            .Contains(new EEditorPluginRecipe.Plugin("some-plugin", null));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddPluginDependencyWithVersionAddsConstrainedDependency()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddPluginDependency("some-plugin", ">1.2.0");

        Assertions.AssertThat(builder.PluginRecipe.PluginDependencies)
            .Contains(new EEditorPluginRecipe.Plugin("some-plugin", ">1.2.0"));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddProjectTwoArgOverloadDefaultsFolderToNull()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddProject("addons/other/Other.csproj", false);

        Assertions.AssertThat(builder.PluginRecipe.Projects)
            .Contains(new EEditorPluginRecipe.Project("addons/other/Other.csproj", null, false));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddProjectWithVirtualFolderAddsFolderedEntry()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddProject("addons/other/Other.csproj", "MyFolder", true);

        Assertions.AssertThat(builder.PluginRecipe.Projects)
            .Contains(new EEditorPluginRecipe.Project("addons/other/Other.csproj", "MyFolder", true));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddNugetParamsAddsEachPackageWithoutVersionOrSource()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddNuget("Newtonsoft.Json", "ZLogger");

        Assertions.AssertThat(builder.PluginRecipe.Nugets)
            .ContainsExactly(
                new EEditorPluginRecipe.Nuget("Newtonsoft.Json", null, null),
                new EEditorPluginRecipe.Nuget("ZLogger", null, null));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddNugetWithVersionAndSourceAddsPinnedEntry()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddNuget("Newtonsoft.Json", "13.0.3", "https://example.com/feed/index.json");

        Assertions.AssertThat(builder.PluginRecipe.Nugets)
            .Contains(new EEditorPluginRecipe.Nuget("Newtonsoft.Json", "13.0.3",
                "https://example.com/feed/index.json"));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddDirectoryAddsPath()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddDirectory("res://addons/my-plugin/hidden_src");

        Assertions.AssertThat(builder.PluginRecipe.Directories).Contains("res://addons/my-plugin/hidden_src");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void FluentChainAccumulatesAllEntriesOnOneRecipe()
    {
        var builder = EEditorPluginBuilder.Create();

        builder
            .AddAutoload("MyGlobal", "res://addons/my-plugin/MyGlobal.cs")
            .AddNuget("ZLogger")
            .AddPluginDependency("some-plugin")
            .AddDirectory("res://addons/my-plugin/hidden_src");

        Assertions.AssertInt(builder.PluginRecipe.Autoloads.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.Nugets.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.Directories.Count).IsEqual(1);
        // one implicit "ePlugin" dependency (added by Create()) plus the one just declared
        Assertions.AssertInt(builder.PluginRecipe.PluginDependencies.Count).IsEqual(2);
    }
}
