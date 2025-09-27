using System.Collections.Generic;

namespace GContainer.addons.ePlugin.Internal;

internal sealed class EEditorPluginRecipe
{
    internal record Plugin(string slug, string? version);

    internal record Project(string path, string? folderName, bool reference);

    internal record Nuget(string name, string? version, string? source);

    internal record Autoload(string name, string path);

    public List<Plugin> PluginDependencies { get; init; } = [];
    public List<Project> Projects { get; init; } = [];
    public List<Nuget> Nugets { get; init; } = [];

    public List<Autoload> Autoloads { get; init; } = [];
    
    public List<string> Directories { get; init; } = [];
}