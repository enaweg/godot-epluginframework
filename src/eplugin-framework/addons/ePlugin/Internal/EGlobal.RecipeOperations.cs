#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private UpdateJournal? _recipeUpdateJournal;
    private int _updateRefreshSuppression;

    private void ApplyNuget(PluginContext context, EEditorPluginRecipe.Nuget nuget)
    {
        var previous = _recipeUpdateJournal?.Recipes.GetValueOrDefault(context.Slug)?.Applied.Nugets.FirstOrDefault(n => n.Name == nuget.Name);
        if (context.Cli?.AddNugetToProject(nuget.Name, nuget.Version, nuget.Source) != true)
            throw new InvalidOperationException($"Adding NuGet {nuget.Name} to the project failed.");
        if (nuget.Source is not null && !NugetConfigManager.RegisterSource(_host.ProjectRoot, context.Slug, nuget.Source, context.Logger))
            throw new InvalidOperationException($"Registering NuGet source for {nuget.Name} failed.");
        if (previous?.Source is not null && previous.Source != nuget.Source &&
            _recipeUpdateJournal?.Recipes[context.Slug].Target?.Nugets.Any(n => n.Source == previous.Source) != true &&
            !NugetConfigManager.UnregisterSource(_host.ProjectRoot, context.Slug, previous.Source, context.Logger))
            throw new InvalidOperationException($"Unregistering previous source for {nuget.Name} failed.");
    }
    /// <param name="remaining">The recipes that stay installed (<see cref="RemainingRecipes"/>); a package one of them declares is kept.</param>
    private void ReverseNuget(PluginContext context, EEditorPluginRecipe.Nuget nuget, IReadOnlyList<EEditorPluginRecipe>? remaining = null)
    {
        var shared = _recipeUpdateJournal?.Recipes.Any(r => r.Key != context.Slug &&
            (r.Value.Target ?? r.Value.Applied).Nugets.Any(n => n.Name == nuget.Name)) == true;
        if (remaining?.Any(r => r.Nugets.Any(n => string.Equals(n.Name, nuget.Name, StringComparison.OrdinalIgnoreCase))) == true)
        {
            context.Logger?.Log($"Keeping NuGet {nuget.Name}, another installed recipe still uses it.");
            shared = true;
        }
        if (!shared && (context.Cli is not ICheckedDotnetCli cli || !cli.TryRemoveNugetFromProject(nuget.Name)))
            throw new InvalidOperationException($"Removing NuGet {nuget.Name} failed.");
        var keepsSource = _recipeUpdateJournal?.Recipes.GetValueOrDefault(context.Slug)?.Target?.Nugets.Any(n => n.Source == nuget.Source) == true;
        if (nuget.Source is not null && !keepsSource && !NugetConfigManager.UnregisterSource(_host.ProjectRoot, context.Slug, nuget.Source, context.Logger))
            throw new InvalidOperationException($"Unregistering NuGet source for {nuget.Name} failed.");
    }
    private static void ApplyProject(PluginContext context, EEditorPluginRecipe.Project project)
    {
        if (context.Cli is not ICheckedDotnetCli cli || !cli.TryAddProjectToSolution(project.Path, project.FolderName))
            throw new InvalidOperationException($"Adding project {project.Path} to the solution failed.");
        if (project.Reference && !cli.TryAddProjectReference(project.Path)) throw new InvalidOperationException($"Adding project reference {project.Path} failed.");
    }
    /// <param name="remaining">The recipes that stay installed (<see cref="RemainingRecipes"/>); the solution entry or reference one of them declares is kept.</param>
    private void ReverseProject(PluginContext context, EEditorPluginRecipe.Project project, IReadOnlyList<EEditorPluginRecipe>? remaining = null)
    {
        if (context.Cli is not ICheckedDotnetCli cli) throw new InvalidOperationException("Checked dotnet CLI is unavailable.");
        var path = ProjectPath(project.Path);
        var kept = remaining?.SelectMany(r => r.Projects).Where(p => ProjectPath(p.Path) == path).ToArray() ?? [];
        if (kept.Length > 0) context.Logger?.Log($"Keeping project {project.Path}, another installed recipe still uses it.");
        if (project.Reference && !kept.Any(p => p.Reference) && !cli.TryRemoveProjectReference(project.Path))
            throw new InvalidOperationException($"Removing project reference {project.Path} failed.");
        if (kept.Length == 0 && !cli.TryRemoveProjectFromSolution(project.Path)) throw new InvalidOperationException($"Removing project {project.Path} from the solution failed.");
    }

    /// <summary>A recipe's project path made absolute, like <see cref="DotnetCliBase.ProjectFile"/>, so recipes naming it differently compare equal.</summary>
    private string ProjectPath(string path) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(_host.ProjectRoot, path.StartsWith("res://", StringComparison.Ordinal) ? path["res://".Length..] : path));

    /// <summary>
    /// The recipes that stay installed while <paramref name="reversed"/>, one of <paramref name="context"/>'s recipes, is
    /// reversed: the recipes and applied optional recipes of every other activated plugin, and the context's own main
    /// recipe and applied optional recipes besides <paramref name="reversed"/>. Two plugins may declare the same NuGet
    /// package or project, and removing it with one of them would break the other's code.
    /// </summary>
    private List<EEditorPluginRecipe> RemainingRecipes(PluginContext context, EEditorPluginRecipe reversed)
    {
        var remaining = new List<EEditorPluginRecipe>();
        foreach (var other in _contexts.Where(c => c.Plugin is not null && (c == context || c.State == EEditorPluginState.Activated)).ToArray())
        {
            if (other != context && !TryEnsureRecipe(other, scan: true)) continue;
            var recipe = other.Builder.PluginRecipe;
            remaining.Add(recipe);
            remaining.AddRange((other.AppliedOptionalDependencies ?? (other == context ? [] : ResolveOptionalRecipes(other, recipe))).Select(o => o.Recipe));
        }
        remaining.RemoveAll(r => ReferenceEquals(r, reversed));
        return remaining;
    }
    private void ApplyAutoload(PluginContext context, EEditorPluginRecipe.Autoload autoload)
    {
        if (_host.GetAutoload(autoload.Name) != autoload.Path) _host.AddAutoload(context.PluginBase, autoload.Name, autoload.Path);
        if (_host.GetAutoload(autoload.Name) is null) throw new InvalidOperationException($"Adding autoload {autoload.Name} failed.");
    }
    private void ReverseAutoload(PluginContext context, EEditorPluginRecipe.Autoload autoload)
    {
        if (_host.GetAutoload(autoload.Name) is not null) _host.RemoveAutoload(context.PluginBase, autoload.Name);
        if (_host.GetAutoload(autoload.Name) is not null) throw new InvalidOperationException($"Removing autoload {autoload.Name} failed.");
    }
    private void TrackRecipeOperation(PluginContext context, RecipeOperation operation, Action execute)
    {
        var journal = _recipeUpdateJournal;
        if (journal is null) { execute(); return; }
        RegisterUpdateParticipant(context.Slug, context.Metadata?.Version);
        var record = journal.Recipes[context.Slug];
        record.Pending = operation; journal.Save();
        execute();
        RecipeReconciler.Record(record.Applied, operation);
        record.Pending = null; journal.Save();
    }
    private void RegisterUpdateParticipant(string slug, string? version)
    {
        var journal = _recipeUpdateJournal ?? throw new InvalidOperationException("No recipe update is active.");
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException($"Plugin {slug} has no usable version.");
        if (_stateStore is null || !_stateStore.TryAddParticipant(journal.AttemptId, slug, version, PersistedPluginState.Activated))
            throw new InvalidOperationException($"Cannot record update participant {slug}.");
        if (!journal.Plugins.Any(p => p.Slug == slug)) journal.AdditionalVersions[slug] = _stateStore.GetShared(slug)?.Version ?? version;
        if (!journal.Recipes.ContainsKey(slug)) journal.Recipes[slug] = new() { WasEnabled = false };
        journal.Save();
    }
    private void EnsureUpdateDependency(EEditorPluginRecipe.Plugin dependency)
    {
        // Godot always runs a GDExtension add-on; only its recorded version can fail the dependency.
        if (IsExtensionAddon(dependency.Slug))
        {
            if (dependency.Version is not null && (ExtensionAddonVersion(dependency.Slug) is not { } version || !MatchesVersion(version, dependency.Version, _ePluginContext?.Logger)))
                throw new InvalidOperationException($"Dependency {dependency.Slug} {ExtensionAddonVersion(dependency.Slug) ?? "(unknown version)"} does not match {dependency.Version}.");
            return;
        }
        var metadata = ReadInstalledMetadata(dependency.Slug) ?? throw new InvalidOperationException($"Plugin now requires missing dependency {dependency.Slug}.");
        if (dependency.Version is not null && !MatchesVersion(metadata.Version, dependency.Version, _ePluginContext?.Logger))
            throw new InvalidOperationException($"Dependency {dependency.Slug} {metadata.Version} does not match {dependency.Version}.");
        if (_host.IsPluginEnabled(dependency.Slug)) return;
        RegisterUpdateParticipant(dependency.Slug, metadata.Version);
        _host.SetPluginEnabled(dependency.Slug, true);
        var context = ManagedContext(dependency.Slug);
        if (context is not null && context.State != EEditorPluginState.Activated) EnableEPlugin(context, false, true);
        if (!_host.IsPluginEnabled(dependency.Slug) || context?.State == EEditorPluginState.Error || _toCheckEnable.Count > 0)
            throw new InvalidOperationException($"Dependency {dependency.Slug} could not be activated.");
    }
}
#endif
