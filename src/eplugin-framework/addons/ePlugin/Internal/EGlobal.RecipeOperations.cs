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
        if (nuget.Source is not null && !NugetConfigManager.RegisterSource(context.Slug, nuget.Source, context.Logger))
            throw new InvalidOperationException($"Registering NuGet source for {nuget.Name} failed.");
        if (previous?.Source is not null && previous.Source != nuget.Source &&
            _recipeUpdateJournal?.Recipes[context.Slug].Target?.Nugets.Any(n => n.Source == previous.Source) != true &&
            !NugetConfigManager.UnregisterSource(context.Slug, previous.Source, context.Logger))
            throw new InvalidOperationException($"Unregistering previous source for {nuget.Name} failed.");
    }
    private void ReverseNuget(PluginContext context, EEditorPluginRecipe.Nuget nuget)
    {
        var shared = _recipeUpdateJournal?.Recipes.Any(r => r.Key != context.Slug &&
            (r.Value.Target ?? r.Value.Applied).Nugets.Any(n => n.Name == nuget.Name)) == true;
        if (!shared && (context.Cli is not ICheckedDotnetCli cli || !cli.TryRemoveNugetFromProject(nuget.Name)))
            throw new InvalidOperationException($"Removing NuGet {nuget.Name} failed.");
        var keepsSource = _recipeUpdateJournal?.Recipes.GetValueOrDefault(context.Slug)?.Target?.Nugets.Any(n => n.Source == nuget.Source) == true;
        if (nuget.Source is not null && !keepsSource && !NugetConfigManager.UnregisterSource(context.Slug, nuget.Source, context.Logger))
            throw new InvalidOperationException($"Unregistering NuGet source for {nuget.Name} failed.");
    }
    private static void ApplyProject(PluginContext context, EEditorPluginRecipe.Project project)
    {
        if (context.Cli is not ICheckedDotnetCli cli || !cli.TryAddProjectToSolution(project.Path, project.FolderName))
            throw new InvalidOperationException($"Adding project {project.Path} to the solution failed.");
        if (project.Reference && !cli.TryAddProjectReference(project.Path)) throw new InvalidOperationException($"Adding project reference {project.Path} failed.");
    }
    private static void ReverseProject(PluginContext context, EEditorPluginRecipe.Project project)
    {
        if (context.Cli is not ICheckedDotnetCli cli) throw new InvalidOperationException("Checked dotnet CLI is unavailable.");
        if (project.Reference && !cli.TryRemoveProjectReference(project.Path)) throw new InvalidOperationException($"Removing project reference {project.Path} failed.");
        if (!cli.TryRemoveProjectFromSolution(project.Path)) throw new InvalidOperationException($"Removing project {project.Path} from the solution failed.");
    }
    private static void ApplyAutoload(PluginContext context, EEditorPluginRecipe.Autoload autoload)
    {
        if (ProjectSettings.GetSetting("autoload/" + autoload.Name, "").AsString().TrimStart('*') != autoload.Path)
            context.PluginBase.AddAutoloadSingleton(autoload.Name, autoload.Path);
        if (!ProjectSettings.HasSetting("autoload/" + autoload.Name)) throw new InvalidOperationException($"Adding autoload {autoload.Name} failed.");
    }
    private static void ReverseAutoload(PluginContext context, EEditorPluginRecipe.Autoload autoload)
    {
        if (ProjectSettings.HasSetting("autoload/" + autoload.Name)) context.PluginBase.RemoveAutoloadSingleton(autoload.Name);
        if (ProjectSettings.HasSetting("autoload/" + autoload.Name)) throw new InvalidOperationException($"Removing autoload {autoload.Name} failed.");
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
        var metadata = EditorPluginExtensions.ReadMetadata($"res://addons/{dependency.Slug}/plugin.cfg") ?? throw new InvalidOperationException($"Plugin now requires missing dependency {dependency.Slug}.");
        if (dependency.Version is not null && !MatchesVersion(metadata.Version, dependency.Version, _ePluginContext?.Logger))
            throw new InvalidOperationException($"Dependency {dependency.Slug} {metadata.Version} does not match {dependency.Version}.");
        if (EditorInterface.Singleton.IsPluginEnabled(dependency.Slug)) return;
        RegisterUpdateParticipant(dependency.Slug, metadata.Version);
        EditorInterface.Singleton.SetPluginEnabled(dependency.Slug, true);
        var context = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug && c.Plugin is not null);
        if (context is not null && context.State != EEditorPluginState.Activated) EnableEPlugin(context, false, true);
        if (!EditorInterface.Singleton.IsPluginEnabled(dependency.Slug) || context?.State == EEditorPluginState.Error || _toCheckEnable.Count > 0)
            throw new InvalidOperationException($"Dependency {dependency.Slug} could not be activated.");
    }
}
#endif
