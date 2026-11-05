#if TOOLS
using System;

namespace Enaweg.Plugin.Internal;

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
        PluginRecipe.PluginDependencies.Add(new EEditorPluginRecipe.Plugin(Framework.Slug, null));
    }

    public IEEditorPluginBuilder AddAutoload(string name, string path)
    {
        RecipeEntries.AddAutoload(PluginRecipe, name, path);
        return this;
    }

    public IEEditorPluginBuilder SetLicense(string license)
    {
        var (text, path) = TextOrPath(license, "license", nameof(license));
        PluginRecipe.PluginLicense = new EEditorPluginRecipe.License(text, path);
        return this;
    }

    public IEEditorPluginBuilder SetWelcome(string welcome)
    {
        var (text, path) = TextOrPath(welcome, "welcome page", nameof(welcome));
        PluginRecipe.PluginWelcome = new EEditorPluginRecipe.Welcome(text, path);
        return this;
    }

    /// <summary>
    /// A single-line <c>res://</c> path (trimmed) as the path, anything else as the text itself.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    private static (string? Text, string? Path) TextOrPath(string value, string what, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"The {what} must not be empty.", parameterName);
        var path = value.Trim();
        return path.StartsWith("res://", StringComparison.Ordinal) && !path.Contains('\n') ? (null, path) : (value, null);
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
