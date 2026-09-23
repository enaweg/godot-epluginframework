#if TOOLS
using System;
using Godot;

namespace Enaweg.Plugin.Internal;

[Tool]
internal sealed class EEditorPluginBuilder : IEEditorPluginBuilder
{
    public static EEditorPluginBuilder Create()
    {
        return new EEditorPluginBuilder();
    }

    internal EEditorPluginRecipe PluginRecipe { get; set; }

    private EEditorPluginBuilder()
    {
        PluginRecipe = new EEditorPluginRecipe();
        PluginRecipe.PluginDependencies.Add(new EEditorPluginRecipe.Plugin("ePlugin", null));
    }

    public IEEditorPluginBuilder AddAutoload(string name, string path)
    {
        PluginRecipe.Autoloads.Add(new EEditorPluginRecipe.Autoload(name, path));
        return this;
    }

    public IEEditorPluginBuilder AddPluginDependency(string pluginSlug, string? version = null)
    {
        PluginRecipe.PluginDependencies.Add(new EEditorPluginRecipe.Plugin(pluginSlug, version));
        return this;
    }

    public IEEditorPluginBuilder AddOptionalPluginDependency(string pluginSlug, string? version,
        Action<IEEditorPluginRecipeBuilder> optionalRecipe)
    {
        ArgumentNullException.ThrowIfNull(optionalRecipe);

        var subBuilder = EEditorPluginSubRecipeBuilder.Create();
        optionalRecipe(subBuilder);

        PluginRecipe.OptionalPluginDependencies.Add(
            new EEditorPluginRecipe.OptionalPlugin(pluginSlug, version, subBuilder.PluginRecipe));
        return this;
    }

    public IEEditorPluginBuilder AddProject(string path, bool addReference = true)
    {
        PluginRecipe.Projects.Add(new EEditorPluginRecipe.Project(path, null, addReference));
        return this;
    }

    public IEEditorPluginBuilder AddProject(string path, string? virtualFolderName = null,
        bool addReference = true)
    {
        PluginRecipe.Projects.Add(new EEditorPluginRecipe.Project(path, virtualFolderName, addReference));
        return this;
    }

    public IEEditorPluginBuilder AddNugets(params string[] nugetNames)
    {
        foreach (var nugetName in nugetNames)
        {
            PluginRecipe.Nugets.Add(new EEditorPluginRecipe.Nuget(nugetName, null, null));
        }

        return this;
    }

    public IEEditorPluginBuilder AddNuget(string nugetName, string? version = null, string? source = null)
    {
        PluginRecipe.Nugets.Add(new EEditorPluginRecipe.Nuget(nugetName, version, source));
        return this;
    }

    public IEEditorPluginBuilder AddDirectory(string path)
    {
        PluginRecipe.Directories.Add(path);
        return this;
    }

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddAutoload(string name, string path)
        => AddAutoload(name, path);

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddProject(string path, bool addReference)
        => AddProject(path, addReference);

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddProject(string path, string? virtualFolderName,
        bool addReference)
        => AddProject(path, virtualFolderName, addReference);

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddNugets(params string[] nugetNames)
        => AddNugets(nugetNames);

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddNuget(string nugetName, string? version,
        string? source)
        => AddNuget(nugetName, version, source);

    IEEditorPluginRecipeBuilder IEEditorPluginRecipeBuilder.AddDirectory(string path)
        => AddDirectory(path);
}
#endif
