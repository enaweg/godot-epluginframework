#if TOOLS
using System.Collections.Generic;

namespace Enaweg.Plugin.Internal;

internal sealed class EEditorPluginRecipe
{
    internal record Plugin(string Slug, string? Version);

    internal record Project(string Path, string? FolderName, bool Reference);

    internal record Nuget(string Name, string? Version, string? Source);

    internal record Autoload(string Name, string Path);

    public List<Plugin> PluginDependencies { get; init; } = [];
    public List<Project> Projects { get; init; } = [];
    public List<Nuget> Nugets { get; init; } = [];

    public List<Autoload> Autoloads { get; init; } = [];
    
    public List<string> Directories { get; init; } = [];
}
#endif