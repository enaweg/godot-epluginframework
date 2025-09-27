using Enaweg.Plugin;
using GContainer.addons.ePlugin.Internal;

namespace GContainer.addons.ePlugin;

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

    public IEEditorPluginBuilder AddNuget(params string[] nugetNames)
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
}