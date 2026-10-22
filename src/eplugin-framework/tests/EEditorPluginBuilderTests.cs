#if TOOLS
using System;
using System.Diagnostics.CodeAnalysis;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EEditorPluginBuilderTests
{
    [TestCase]
    public static void EveryResourceIsRecordedOnTheRecipeInOrder()
    {
        var builder = EEditorPluginBuilder.Create();

        var result = builder
            .AddAutoload("MyGlobal", "res://addons/my-plugin/MyGlobal.cs")
            .AddNugets("Newtonsoft.Json", "ZLogger")
            .AddNuget("Serilog", "4.0.0", "https://example.com/feed/index.json")
            .AddProject("addons/other/Other.csproj", false)
            .AddDirectory("res://addons/my-plugin/.src")
            .AddPluginDependency("some-plugin", ">=1.2.0");

        var recipe = builder.PluginRecipe;
        Assertions.AssertObject(result).IsSame(builder);
        Assertions.AssertThat(recipe.Autoloads).ContainsExactly(new EEditorPluginRecipe.Autoload("MyGlobal", "res://addons/my-plugin/MyGlobal.cs"));
        Assertions.AssertThat(recipe.Nugets).ContainsExactly(
            new EEditorPluginRecipe.Nuget("Newtonsoft.Json", null, null),
            new EEditorPluginRecipe.Nuget("ZLogger", null, null),
            new EEditorPluginRecipe.Nuget("Serilog", "4.0.0", "https://example.com/feed/index.json"));
        Assertions.AssertThat(recipe.Projects).ContainsExactly(new EEditorPluginRecipe.Project("addons/other/Other.csproj", null, false));
        Assertions.AssertThat(recipe.Directories).ContainsExactly("res://addons/my-plugin/.src");
        // the framework itself is an implicit dependency of every root recipe
        Assertions.AssertThat(recipe.PluginDependencies).ContainsExactly(
            new EEditorPluginRecipe.Plugin("ePlugin", null), new EEditorPluginRecipe.Plugin("some-plugin", ">=1.2.0"));
    }

    [TestCase]
    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is what plugins call, so its overloads are what must compile.")]
    public static void EveryDocumentedAddProjectFormCompiles()
    {
        // Compiling this test is the check: one argument was ambiguous between both overloads (CS0121).
        IEEditorPluginBuilder root = EEditorPluginBuilder.Create();
        root.AddProject("a.csproj")
            .AddProject("b.csproj", false)
            .AddProject("c.csproj", "Folder")
            .AddProject("d.csproj", null)
            .AddProject("e.csproj", "Folder", false)
            .AddOptionalPluginDependency("other", null, optional => optional
                .AddProject("f.csproj")
                .AddProject("g.csproj", false)
                .AddProject("h.csproj", "Folder"));

        var recipe = ((EEditorPluginBuilder)root).PluginRecipe;
        Assertions.AssertThat(recipe.Projects).ContainsExactly(
            new EEditorPluginRecipe.Project("a.csproj", null, true),
            new EEditorPluginRecipe.Project("b.csproj", null, false),
            new EEditorPluginRecipe.Project("c.csproj", "Folder", true),
            new EEditorPluginRecipe.Project("d.csproj", null, true),
            new EEditorPluginRecipe.Project("e.csproj", "Folder", false));
        Assertions.AssertThat(recipe.OptionalPluginDependencies[0].Recipe.Projects).ContainsExactly(
            new EEditorPluginRecipe.Project("f.csproj", null, true),
            new EEditorPluginRecipe.Project("g.csproj", null, false),
            new EEditorPluginRecipe.Project("h.csproj", "Folder", true));
    }

    [TestCase]
    public static void AHelperDeclaringResourcesWorksForTheRootAndANestedRecipe()
    {
        // A plugin may share declarations between its root recipe and an optional dependency's through the base interface.
        static IEEditorPluginRecipeBuilder Shared(IEEditorPluginRecipeBuilder recipe) => recipe
            .AddAutoload("Shared", "res://addons/my-plugin/Shared.cs")
            .AddProject("a.csproj", false)
            .AddProject("b.csproj", "Folder", true)
            .AddNugets("Newtonsoft.Json")
            .AddNuget("Serilog", "4.0.0")
            .AddDirectory("res://addons/my-plugin/.src");

        var builder = EEditorPluginBuilder.Create();
        Assertions.AssertObject(Shared(builder)).IsSame(builder);
        builder.AddOptionalPluginDependency("other", null, optional => Shared(optional));

        foreach (var recipe in new[] { builder.PluginRecipe, builder.PluginRecipe.OptionalPluginDependencies[0].Recipe })
        {
            Assertions.AssertThat(recipe.Autoloads).ContainsExactly(new EEditorPluginRecipe.Autoload("Shared", "res://addons/my-plugin/Shared.cs"));
            Assertions.AssertThat(recipe.Projects).ContainsExactly(
                new EEditorPluginRecipe.Project("a.csproj", null, false), new EEditorPluginRecipe.Project("b.csproj", "Folder", true));
            Assertions.AssertThat(recipe.Nugets).ContainsExactly(
                new EEditorPluginRecipe.Nuget("Newtonsoft.Json", null, null), new EEditorPluginRecipe.Nuget("Serilog", "4.0.0", null));
            Assertions.AssertThat(recipe.Directories).ContainsExactly("res://addons/my-plugin/.src");
        }
    }

    [TestCase]
    public static void AnOptionalDependencyKeepsItsResourcesInItsOwnRecipe()
    {
        var builder = EEditorPluginBuilder.Create();

        var result = builder
            .AddNuget("Newtonsoft.Json")
            .AddOptionalPluginDependency("some-plugin", ">1.2.0", optional => optional
                .AddAutoload("OptionalGlobal", "res://addons/my-plugin/OptionalGlobal.cs")
                .AddNuget("ZLogger", "2.0.0")
                .AddDirectory("res://addons/my-plugin/optional_src"))
            .AddPluginDependency("required-plugin");

        Assertions.AssertObject(result).IsSame(builder);
        var optional = builder.PluginRecipe.OptionalPluginDependencies[0];
        Assertions.AssertString(optional.Slug).IsEqual("some-plugin");
        Assertions.AssertString(optional.Version).IsEqual(">1.2.0");
        Assertions.AssertThat(optional.Recipe.Nugets).ContainsExactly(new EEditorPluginRecipe.Nuget("ZLogger", "2.0.0", null));
        Assertions.AssertInt(optional.Recipe.Autoloads.Count).IsEqual(1);
        Assertions.AssertInt(optional.Recipe.Directories.Count).IsEqual(1);
        // a nested recipe has no dependencies of its own, not even the implicit framework one
        Assertions.AssertInt(optional.Recipe.PluginDependencies.Count).IsEqual(0);
        Assertions.AssertThat(builder.PluginRecipe.Nugets).ContainsExactly(new EEditorPluginRecipe.Nuget("Newtonsoft.Json", null, null));
        Assertions.AssertInt(builder.PluginRecipe.Autoloads.Count).IsEqual(0);
    }

    [TestCase]
    public static void InvalidDeclarationsThrowAndSayWhatIsWrong()
    {
        (string What, Action<IEEditorPluginBuilder> Declare)[] cases =
        [
            ("empty slug", b => b.AddPluginDependency(" ")),
            ("path as slug", b => b.AddPluginDependency("res://addons/other")),
            ("nested slug", b => b.AddOptionalPluginDependency("addons/other", null, _ => { })),
            ("constraint", b => b.AddPluginDependency("other", "latest")),
            ("optional constraint", b => b.AddOptionalPluginDependency("other", ">=one", _ => { })),
            ("package id", b => b.AddNuget("not a package")),
            ("empty version", b => b.AddNuget("Some.Package", "")),
            ("empty source", b => b.AddNuget("Some.Package", null, " ")),
            ("project path", b => b.AddProject("addons/other/Other.txt")),
            ("empty folder", b => b.AddProject("addons/other/Other.csproj", "", true)),
            ("autoload name", b => b.AddAutoload("", "res://addons/a/a.tscn")),
            ("autoload path", b => b.AddAutoload("Name", "")),
            ("directory", b => b.AddDirectory("")),
            ("nested package id", b => b.AddOptionalPluginDependency("other", null, o => o.AddNugets("ok.Package", "bad id"))),
        ];

        foreach (var (what, declare) in cases)
        {
            Assertions.AssertThrown(() => declare(EEditorPluginBuilder.Create()))
                .IsInstanceOf<ArgumentException>();
        }
    }

    [TestCase]
    public static void TheSameDeclarationTwiceCountsOnceAndContradictionsThrow()
    {
        var builder = EEditorPluginBuilder.Create();
        builder.AddNuget("ZLogger", "2.0.0").AddNuget("zlogger", "2.0.0")
            .AddProject("a.csproj").AddProject("a.csproj")
            .AddAutoload("A", "res://a.tscn").AddAutoload("A", "res://a.tscn")
            .AddDirectory("res://src").AddDirectory("res://src")
            .AddPluginDependency("other", ">=1.0").AddPluginDependency("other", ">=1.0");

        Assertions.AssertInt(builder.PluginRecipe.Nugets.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.Projects.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.Autoloads.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.Directories.Count).IsEqual(1);
        Assertions.AssertInt(builder.PluginRecipe.PluginDependencies.Count).IsEqual(2);

        Assertions.AssertThrown(() => builder.AddNuget("ZLogger", "2.1.0")).IsInstanceOf<ArgumentException>().HasMessage(
            "NuGet ZLogger is declared twice, differently (Nuget { Name = ZLogger, Version = 2.0.0, Source =  } and Nuget { Name = ZLogger, Version = 2.1.0, Source =  }). (Parameter 'name')");
        Assertions.AssertThrown(() => builder.AddProject("a.csproj", false)).IsInstanceOf<ArgumentException>();
        Assertions.AssertThrown(() => builder.AddAutoload("A", "res://b.tscn")).IsInstanceOf<ArgumentException>();
        Assertions.AssertThrown(() => builder.AddPluginDependency("other", ">=2.0")).IsInstanceOf<ArgumentException>();
        Assertions.AssertThrown(() => builder.AddOptionalPluginDependency("other", null, _ => { })).IsInstanceOf<ArgumentException>();
        builder.AddOptionalPluginDependency("extra", null, _ => { });
        Assertions.AssertThrown(() => builder.AddPluginDependency("extra")).IsInstanceOf<ArgumentException>();
        Assertions.AssertThrown(() => builder.AddOptionalPluginDependency("extra", null, _ => { })).IsInstanceOf<ArgumentException>();
    }

    [TestCase]
    public static void AConstraintOnTheFrameworkReplacesItsImplicitDependency()
    {
        var builder = EEditorPluginBuilder.Create();

        builder.AddPluginDependency("ePlugin", ">=0.9.1");

        Assertions.AssertThat(builder.PluginRecipe.PluginDependencies).ContainsExactly(new EEditorPluginRecipe.Plugin("ePlugin", ">=0.9.1"));
    }
}
#endif
