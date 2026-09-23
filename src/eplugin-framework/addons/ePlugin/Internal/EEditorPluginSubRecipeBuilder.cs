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
        PluginRecipe.Autoloads.Add(new EEditorPluginRecipe.Autoload(name, path));
        return this;
    }

    public IEEditorPluginRecipeBuilder AddProject(string path, bool addReference = true)
    {
        PluginRecipe.Projects.Add(new EEditorPluginRecipe.Project(path, null, addReference));
        return this;
    }

    public IEEditorPluginRecipeBuilder AddProject(string path, string? virtualFolderName = null,
        bool addReference = true)
    {
        PluginRecipe.Projects.Add(new EEditorPluginRecipe.Project(path, virtualFolderName, addReference));
        return this;
    }

    public IEEditorPluginRecipeBuilder AddNugets(params string[] nugetNames)
    {
        foreach (var nugetName in nugetNames)
        {
            PluginRecipe.Nugets.Add(new EEditorPluginRecipe.Nuget(nugetName, null, null));
        }

        return this;
    }

    public IEEditorPluginRecipeBuilder AddNuget(string nugetName, string? version = null, string? source = null)
    {
        PluginRecipe.Nugets.Add(new EEditorPluginRecipe.Nuget(nugetName, version, source));
        return this;
    }

    public IEEditorPluginRecipeBuilder AddDirectory(string path)
    {
        PluginRecipe.Directories.Add(path);
        return this;
    }
}
#endif
