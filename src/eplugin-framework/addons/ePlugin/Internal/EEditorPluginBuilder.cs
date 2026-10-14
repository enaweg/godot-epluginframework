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
        RecipeEntries.AddAutoload(PluginRecipe, name, path);
        return this;
    }

    public IEEditorPluginBuilder SetLicense(string license)
    {
        if (string.IsNullOrWhiteSpace(license)) throw new ArgumentException("The license must not be empty.", nameof(license));
        var path = license.Trim();
        PluginRecipe.PluginLicense = path.StartsWith("res://", StringComparison.Ordinal) && !path.Contains('\n')
            ? new EEditorPluginRecipe.License(null, path)
            : new EEditorPluginRecipe.License(license, null);
        return this;
    }

    public IEEditorPluginBuilder SetWelcome(string welcome)
    {
        if (string.IsNullOrWhiteSpace(welcome)) throw new ArgumentException("The welcome page must not be empty.", nameof(welcome));
        var path = welcome.Trim();
        PluginRecipe.PluginWelcome = path.StartsWith("res://", StringComparison.Ordinal) && !path.Contains('\n')
            ? new EEditorPluginRecipe.Welcome(null, path)
            : new EEditorPluginRecipe.Welcome(welcome, null);
        return this;
    }

    public IEEditorPluginBuilder AddPluginDependency(string pluginSlug, string? version = null)
    {
        RecipeEntries.AddPluginDependency(PluginRecipe, pluginSlug, version);
        return this;
    }

    public IEEditorPluginBuilder AddOptionalPluginDependency(string pluginSlug, string? version,
        Action<IEEditorPluginRecipeBuilder> optionalRecipe)
    {
        ArgumentNullException.ThrowIfNull(optionalRecipe);

        var subBuilder = EEditorPluginSubRecipeBuilder.Create();
        optionalRecipe(subBuilder);

        RecipeEntries.AddOptionalPluginDependency(PluginRecipe, pluginSlug, version, subBuilder.PluginRecipe);
        return this;
    }

    public IEEditorPluginBuilder AddProject(string path, bool addReference = true)
    {
        RecipeEntries.AddProject(PluginRecipe, path, null, addReference);
        return this;
    }

    public IEEditorPluginBuilder AddProject(string path, string? virtualFolderName,
        bool addReference = true)
    {
        RecipeEntries.AddProject(PluginRecipe, path, virtualFolderName, addReference);
        return this;
    }

    public IEEditorPluginBuilder AddNugets(params string[] nugetNames)
    {
        ArgumentNullException.ThrowIfNull(nugetNames);
        foreach (var nugetName in nugetNames)
        {
            RecipeEntries.AddNuget(PluginRecipe, nugetName, null, null);
        }

        return this;
    }

    public IEEditorPluginBuilder AddNuget(string nugetName, string? version = null, string? source = null)
    {
        RecipeEntries.AddNuget(PluginRecipe, nugetName, version, source);
        return this;
    }

    public IEEditorPluginBuilder AddDirectory(string path)
    {
        RecipeEntries.AddDirectory(PluginRecipe, path);
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
