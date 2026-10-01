#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

internal sealed record OptionalRecipeSnapshot(string Slug, string? Version, RecipeSnapshot Recipe);
internal sealed class RecipeSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public List<EEditorPluginRecipe.Nuget> Nugets { get; set; } = [];
    public List<EEditorPluginRecipe.Project> Projects { get; set; } = [];
    public List<EEditorPluginRecipe.Autoload> Autoloads { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public List<EEditorPluginRecipe.Plugin> PluginDependencies { get; set; } = [];
    public List<OptionalRecipeSnapshot> AppliedOptionals { get; set; } = [];

    public static RecipeSnapshot Capture(EEditorPluginRecipe root, IEnumerable<EEditorPluginRecipe.OptionalPlugin> optionals)
    {
        var applied = optionals.ToArray();
        var resources = new[] { root }.Concat(applied.Select(p => p.Recipe)).ToArray();
        return new()
        {
            Nugets = resources.SelectMany(r => r.Nugets).Distinct().ToList(),
            Projects = resources.SelectMany(r => r.Projects).Distinct().ToList(),
            Autoloads = resources.SelectMany(r => r.Autoloads).Distinct().ToList(),
            Directories = resources.SelectMany(r => r.Directories).Distinct(StringComparer.Ordinal).ToList(),
            PluginDependencies = root.PluginDependencies.ToList(),
            AppliedOptionals = applied.Select(p => new OptionalRecipeSnapshot(p.Slug, p.Version, Capture(p.Recipe, []))).ToList()
        };
    }
    public EEditorPluginRecipe ToRecipe() => new() { Nugets = Nugets.ToList(), Projects = Projects.ToList(), Autoloads = Autoloads.ToList(), Directories = Directories.ToList(), PluginDependencies = PluginDependencies.ToList() };
    public RecipeSnapshot Clone() => new() { Nugets = Nugets.ToList(), Projects = Projects.ToList(), Autoloads = Autoloads.ToList(), Directories = Directories.ToList(), PluginDependencies = PluginDependencies.ToList(), AppliedOptionals = AppliedOptionals.ToList() };
}
internal sealed class RecipeJournal
{
    public bool WasEnabled { get; set; } = true;
    public RecipeSnapshot Old { get; set; } = new();
    public RecipeSnapshot Applied { get; set; } = new();
    public RecipeSnapshot? Target { get; set; }
    public RecipeOperation? Pending { get; set; }
    public bool TreePrepared { get; set; }
}
#endif
