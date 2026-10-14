#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Checks what a recipe declares and adds it, for the root and the nested builders alike. A mistake throws an
/// <see cref="ArgumentException"/> from <see cref="IEEditorPlugin.CreateRecipe"/>, which fails the activation with that
/// message, instead of surfacing later as a failing dotnet command or a dependency that never matches.
/// </summary>
/// <remarks>
/// Declaring the same entry twice is ignored, so a recipe that did that before still uninstalls; only declarations that
/// contradict each other throw, such as one package with two versions.
/// </remarks>
internal static class RecipeEntries
{
    public static void AddAutoload(EEditorPluginRecipe recipe, string name, string path)
    {
        Require(name, nameof(name), "autoload name");
        Require(path, nameof(path), "autoload path");
        Add(recipe.Autoloads, new EEditorPluginRecipe.Autoload(name, path), a => a.Name == name, $"Autoload {name}", nameof(name));
    }

    public static void AddProject(EEditorPluginRecipe recipe, string path, string? virtualFolderName, bool addReference)
    {
        if (!ProjectReferences.IsValidProjectPath(path))
            throw new ArgumentException($"'{path}' is not the path of a project file (.csproj).", nameof(path));
        if (virtualFolderName is not null && string.IsNullOrWhiteSpace(virtualFolderName))
            throw new ArgumentException("The solution folder must not be empty; pass null for none.", nameof(virtualFolderName));
        Add(recipe.Projects, new EEditorPluginRecipe.Project(path, virtualFolderName, addReference), p => p.Path == path,
            $"Project {path}", nameof(path));
    }

    public static void AddNuget(EEditorPluginRecipe recipe, string name, string? version, string? source)
    {
        if (!ProjectReferences.IsValidPackageId(name))
            throw new ArgumentException($"'{name}' is not a NuGet package id.", nameof(name));
        if (version is not null && string.IsNullOrWhiteSpace(version))
            throw new ArgumentException($"The version of NuGet {name} must not be empty; pass null for the latest.", nameof(version));
        if (source is not null && string.IsNullOrWhiteSpace(source))
            throw new ArgumentException($"The source of NuGet {name} must not be empty; pass null for the configured feeds.", nameof(source));
        // package ids are case-insensitive
        Add(recipe.Nugets, new EEditorPluginRecipe.Nuget(name, version, source),
            n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase), $"NuGet {name}", nameof(name),
            n => n.Version == version && n.Source == source);
    }

    public static void AddDirectory(EEditorPluginRecipe recipe, string path)
    {
        Require(path, nameof(path), "directory");
        if (!recipe.Directories.Contains(path)) recipe.Directories.Add(path);
    }

    /// <summary>Adds a hard dependency. Naming the framework replaces its implicit, unconstrained dependency.</summary>
    public static void AddPluginDependency(EEditorPluginRecipe recipe, string slug, string? version)
    {
        RequireSlug(slug);
        RequireConstraint(version);
        if (slug == "ePlugin") recipe.PluginDependencies.RemoveAll(d => d.Slug == slug && d.Version is null);
        if (recipe.OptionalPluginDependencies.Any(d => d.Slug == slug))
            throw new ArgumentException($"Plugin {slug} cannot be both a required and an optional dependency.", "pluginSlug");
        Add(recipe.PluginDependencies, new EEditorPluginRecipe.Plugin(slug, version), d => d.Slug == slug,
            $"Plugin dependency {slug}", "pluginSlug");
    }

    public static void AddOptionalPluginDependency(EEditorPluginRecipe recipe, string slug, string? version,
        EEditorPluginRecipe optionalRecipe)
    {
        RequireSlug(slug);
        RequireConstraint(version);
        if (recipe.PluginDependencies.Any(d => d.Slug == slug))
            throw new ArgumentException($"Plugin {slug} cannot be both a required and an optional dependency.", "pluginSlug");
        if (recipe.OptionalPluginDependencies.Any(d => d.Slug == slug))
            throw new ArgumentException($"Optional dependency {slug} is declared twice; declare its resources in one recipe.", "pluginSlug");
        recipe.OptionalPluginDependencies.Add(new EEditorPluginRecipe.OptionalPlugin(slug, version, optionalRecipe));
    }

    /// <summary>Adds an entry once; an entry for the same thing that differs from it is a contradiction.</summary>
    private static void Add<T>(List<T> entries, T entry, Func<T, bool> same, string what, string parameter,
        Func<T, bool>? identical = null)
    {
        if (entries.FirstOrDefault(same) is not { } existing)
        {
            entries.Add(entry);
        }
        else if (!(identical?.Invoke(existing) ?? Equals(existing, entry)))
        {
            throw new ArgumentException($"{what} is declared twice, differently ({existing} and {entry}).", parameter);
        }
    }

    private static void Require(string? value, string parameter, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"The {what} must not be empty.", parameter);
    }

    private static void RequireSlug(string? slug)
    {
        Require(slug, "pluginSlug", "plugin slug");
        if (slug is "." or ".." || slug!.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException($"'{slug}' is not a plugin slug: use its folder name below addons.", "pluginSlug");
    }

    private static void RequireConstraint(string? version)
    {
        if (version is not null && !VersionConstraint.IsValid(version))
            throw new ArgumentException(
                $"'{version}' is not a version constraint: use a version such as 1.2.3, or >=1.2.3 for that version or higher.",
                nameof(version));
    }
}
#endif
