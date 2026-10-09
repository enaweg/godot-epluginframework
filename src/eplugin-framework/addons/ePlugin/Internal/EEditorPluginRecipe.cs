#if TOOLS
using System.Collections.Generic;

namespace Enaweg.Plugin.Internal;

internal sealed class EEditorPluginRecipe
{
    internal record Plugin(string Slug, string? Version);

    internal record OptionalPlugin(string Slug, string? Version, EEditorPluginRecipe Recipe);

    internal record Project(string Path, string? FolderName, bool Reference);

    internal record Nuget(string Name, string? Version, string? Source);

    internal record Autoload(string Name, string Path);

    /// <summary>The plugin's license: its text, or the res:// path of the file holding it.</summary>
    internal record License(string? Text, string? Path);

    public List<Plugin> PluginDependencies { get; init; } = [];
    public List<OptionalPlugin> OptionalPluginDependencies { get; init; } = [];
    public List<Project> Projects { get; init; } = [];
    public List<Nuget> Nugets { get; init; } = [];

    public List<Autoload> Autoloads { get; init; } = [];
    
    public List<string> Directories { get; init; } = [];

    /// <summary>Set only on the root recipe, by <see cref="IEEditorPluginBuilder.SetLicense"/>.</summary>
    public License? PluginLicense { get; set; }
}
#endif