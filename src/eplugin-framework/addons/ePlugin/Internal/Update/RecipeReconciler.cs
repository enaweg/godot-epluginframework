#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Enaweg.Plugin.Internal.Update;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RecipeOperationKind { RemoveAutoload, RemoveProject, PrepareTree, HideDirectory, ShowDirectory, RemoveNuget, AddNuget, AddProject, AddAutoload, EnsureDependency }
internal sealed record RecipeOperation(RecipeOperationKind Kind, EEditorPluginRecipe.Nuget? Nuget = null,
    EEditorPluginRecipe.Project? Project = null, EEditorPluginRecipe.Autoload? Autoload = null, string? Directory = null, EEditorPluginRecipe.Plugin? Dependency = null);

internal static class RecipeReconciler
{
    public static string VisibleDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path.TrimStart('.') : path[..(slash + 1)] + path[(slash + 1)..].TrimStart('.');
    }
    public static IReadOnlyList<RecipeOperation> Plan(RecipeSnapshot old, RecipeSnapshot next, bool prepareTree = false)
    {
        Validate(next);
        var operations = new List<RecipeOperation>();
        foreach (var item in old.Autoloads.Where(a => !next.Autoloads.Contains(a))) operations.Add(new(RecipeOperationKind.RemoveAutoload, Autoload: item));
        foreach (var item in old.Projects.Where(p => !next.Projects.Contains(p))) operations.Add(new(RecipeOperationKind.RemoveProject, Project: item));
        if (prepareTree) operations.Add(new(RecipeOperationKind.PrepareTree));
        foreach (var item in old.Directories.Where(d => !next.Directories.Any(n => VisibleDirectory(n) == VisibleDirectory(d)))) operations.Add(new(RecipeOperationKind.HideDirectory, Directory: item));
        foreach (var item in next.Directories.Where(d => prepareTree || !old.Directories.Any(n => VisibleDirectory(n) == VisibleDirectory(d))).OrderBy(d => d.Count(c => c == '/'))) operations.Add(new(RecipeOperationKind.ShowDirectory, Directory: item));
        foreach (var item in old.Nugets.Where(n => !next.Nugets.Any(p => p.Name == n.Name))) operations.Add(new(RecipeOperationKind.RemoveNuget, Nuget: item));
        foreach (var item in next.Nugets.Where(n => !old.Nugets.Contains(n))) operations.Add(new(RecipeOperationKind.AddNuget, Nuget: item));
        foreach (var item in next.Projects.Where(p => !old.Projects.Contains(p))) operations.Add(new(RecipeOperationKind.AddProject, Project: item));
        foreach (var item in next.Autoloads.Where(a => !old.Autoloads.Contains(a))) operations.Add(new(RecipeOperationKind.AddAutoload, Autoload: item));
        foreach (var item in next.PluginDependencies.Where(p => !old.PluginDependencies.Contains(p))) operations.Add(new(RecipeOperationKind.EnsureDependency, Dependency: item));
        return operations;
    }
    public static void Validate(RecipeSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.Nugets.GroupBy(n => n.Name).Any(g => g.Count() > 1) ||
            snapshot.Projects.GroupBy(p => p.Path).Any(g => g.Count() > 1) || snapshot.Autoloads.GroupBy(a => a.Name).Any(g => g.Count() > 1))
            throw new InvalidDataException("Recipe snapshot has incompatible schema or conflicting declarations.");
    }
    public static void Record(RecipeSnapshot applied, RecipeOperation op)
    {
        switch (op.Kind)
        {
            case RecipeOperationKind.RemoveAutoload: applied.Autoloads.RemoveAll(a => a.Name == op.Autoload!.Name); break;
            case RecipeOperationKind.AddAutoload: applied.Autoloads.RemoveAll(a => a.Name == op.Autoload!.Name); applied.Autoloads.Add(op.Autoload!); break;
            case RecipeOperationKind.RemoveProject: applied.Projects.RemoveAll(p => p.Path == op.Project!.Path); break;
            case RecipeOperationKind.AddProject: applied.Projects.RemoveAll(p => p.Path == op.Project!.Path); applied.Projects.Add(op.Project!); break;
            case RecipeOperationKind.HideDirectory: applied.Directories.RemoveAll(d => VisibleDirectory(d) == VisibleDirectory(op.Directory!)); break;
            case RecipeOperationKind.ShowDirectory: applied.Directories.RemoveAll(d => VisibleDirectory(d) == VisibleDirectory(op.Directory!)); applied.Directories.Add(op.Directory!); break;
            case RecipeOperationKind.RemoveNuget: applied.Nugets.RemoveAll(n => n.Name == op.Nuget!.Name); break;
            case RecipeOperationKind.AddNuget: applied.Nugets.RemoveAll(n => n.Name == op.Nuget!.Name); applied.Nugets.Add(op.Nuget!); break;
            case RecipeOperationKind.EnsureDependency: applied.PluginDependencies.RemoveAll(p => p.Slug == op.Dependency!.Slug); applied.PluginDependencies.Add(op.Dependency!); break;
        }
    }
    public static IReadOnlyList<string> DependencyOrder(IReadOnlyDictionary<string, RecipeSnapshot> snapshots)
    {
        var visiting = new HashSet<string>(); var done = new HashSet<string>(); var ordered = new List<string>();
        void Visit(string slug)
        {
            if (done.Contains(slug)) return;
            if (!visiting.Add(slug)) throw new InvalidDataException("Plugin dependency cycle in update batch.");
            foreach (var dependency in snapshots[slug].PluginDependencies.Where(d => snapshots.ContainsKey(d.Slug))) Visit(dependency.Slug);
            visiting.Remove(slug); done.Add(slug); ordered.Add(slug);
        }
        foreach (var slug in snapshots.Keys.Order(StringComparer.Ordinal)) Visit(slug);
        return ordered;
    }
}
#endif
