#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Builder for the nested recipe of an optional plugin dependency. Unlike <see cref="EEditorPluginBuilder"/>
/// it starts from a bare recipe (no implicit ePlugin self-dependency) and cannot declare dependencies.
/// </summary>
[Tool]
internal sealed class EEditorPluginSubRecipeBuilder : IEEditorPluginRecipeBuilder
{
    public static EEditorPluginSubRecipeBuilder Create()
    {
        return new EEditorPluginSubRecipeBuilder();
    }

    internal EEditorPluginRecipe PluginRecipe { get; set; }

    private EEditorPluginSubRecipeBuilder()
    {
        PluginRecipe = new EEditorPluginRecipe();
    }

    public IEEditorPluginRecipeBuilder AddAutoload(string name, string path)
    {
        RecipeEntries.AddAutoload(PluginRecipe, name, path);
        return this;
    }

    public IEEditorPluginRecipeBuilder AddProject(string path, bool addReference = true)
    {
        RecipeEntries.AddProject(PluginRecipe, path, null, addReference);
        return this;
    }

    public IEEditorPluginRecipeBuilder AddProject(string path, string? virtualFolderName,
        bool addReference = true)
    {
        RecipeEntries.AddProject(PluginRecipe, path, virtualFolderName, addReference);
        return this;
    }

    public IEEditorPluginRecipeBuilder AddNugets(params string[] nugetNames)
    {
        System.ArgumentNullException.ThrowIfNull(nugetNames);
        foreach (var nugetName in nugetNames)
        {
            RecipeEntries.AddNuget(PluginRecipe, nugetName, null, null);
        }

        return this;
    }

    public IEEditorPluginRecipeBuilder AddNuget(string nugetName, string? version = null, string? source = null)
    {
        RecipeEntries.AddNuget(PluginRecipe, nugetName, version, source);
        return this;
    }

    public IEEditorPluginRecipeBuilder AddDirectory(string path)
    {
        RecipeEntries.AddDirectory(PluginRecipe, path);
        return this;
    }
}
#endif
