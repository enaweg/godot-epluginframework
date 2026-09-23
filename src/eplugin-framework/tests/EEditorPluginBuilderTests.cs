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

        builder.AddNugets("Newtonsoft.Json", "ZLogger");

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

    [TestCase]
    [RequireGodotRuntime]
    public void AddOptionalPluginDependencyRecordsSlugVersionAndReturnsRootBuilder()
    {
        var builder = EEditorPluginBuilder.Create();

        var result = builder.AddOptionalPluginDependency("some-plugin", ">1.2.0", _ => { });

        Assertions.AssertObject(result).IsSame(builder);
        Assertions.AssertInt(builder.PluginRecipe.OptionalPluginDependencies.Count).IsEqual(1);

        var optional = builder.PluginRecipe.OptionalPluginDependencies[0];
        Assertions.AssertString(optional.Slug).IsEqual("some-plugin");
        Assertions.AssertString(optional.Version).IsEqual(">1.2.0");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddOptionalPluginDependencyKeepsSubRecipeSeparateFromRootRecipe()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional
            .AddAutoload("OptionalGlobal", "res://addons/my-plugin/OptionalGlobal.cs")
            .AddNuget("ZLogger", "2.0.0")
            .AddProject("addons/my-plugin/Optional.csproj", "MyFolder", true)
            .AddDirectory("res://addons/my-plugin/optional_src"));

        var subRecipe = builder.PluginRecipe.OptionalPluginDependencies[0].Recipe;

        Assertions.AssertThat(subRecipe.Autoloads)
            .ContainsExactly(new EEditorPluginRecipe.Autoload("OptionalGlobal",
                "res://addons/my-plugin/OptionalGlobal.cs"));
        Assertions.AssertThat(subRecipe.Nugets)
            .ContainsExactly(new EEditorPluginRecipe.Nuget("ZLogger", "2.0.0", null));
        Assertions.AssertThat(subRecipe.Projects)
            .ContainsExactly(new EEditorPluginRecipe.Project("addons/my-plugin/Optional.csproj", "MyFolder", true));
        Assertions.AssertThat(subRecipe.Directories).ContainsExactly("res://addons/my-plugin/optional_src");

        // nothing of the sub-recipe leaked into the root recipe
        Assertions.AssertInt(builder.PluginRecipe.Autoloads.Count).IsEqual(0);
        Assertions.AssertInt(builder.PluginRecipe.Nugets.Count).IsEqual(0);
        Assertions.AssertInt(builder.PluginRecipe.Projects.Count).IsEqual(0);
        Assertions.AssertInt(builder.PluginRecipe.Directories.Count).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddOptionalPluginDependencySubRecipeHasNoImplicitEPluginDependency()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"));

        var subRecipe = builder.PluginRecipe.OptionalPluginDependencies[0].Recipe;

        Assertions.AssertInt(subRecipe.PluginDependencies.Count).IsEqual(0);
        Assertions.AssertInt(subRecipe.OptionalPluginDependencies.Count).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AddOptionalPluginDependencyChainsBackIntoRootBuilder()
    {
        var builder = EEditorPluginBuilder.Create();

        builder
            .AddNuget("Newtonsoft.Json")
            .AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"))
            .AddPluginDependency("required-plugin");

        Assertions.AssertInt(builder.PluginRecipe.Nugets.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.OptionalPluginDependencies.Count).IsEqual(1);
        Assertions.AssertThat(builder.PluginRecipe.PluginDependencies)
            .Contains(new EEditorPluginRecipe.Plugin("required-plugin", null));
    }
}
