#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private RecipeSnapshot Snapshot(PluginContext context, IEnumerable<OptionalRecipeSnapshot>? retain = null)
    {
        if (context.Plugin is null) return new();
        if (!context.IsRecipeCreated) { context.Plugin.CreateRecipe(context.Builder); context.IsRecipeCreated = true; }
        var recipe = context.Builder.PluginRecipe;
        var optionals = context.AppliedOptionalDependencies ?? ResolveOptionalRecipes(context, recipe);
        if (retain is not null)
        {
            optionals = retain.Select(o => recipe.OptionalPluginDependencies.FirstOrDefault(p => p.Slug == o.Slug && p.Version == o.Version)
                ?? new EEditorPluginRecipe.OptionalPlugin(o.Slug, o.Version, o.Recipe.ToRecipe()))
                .Concat(ResolveOptionalRecipes(context, recipe)).DistinctBy(o => (o.Slug, o.Version)).ToList();
        }
        var snapshot = RecipeSnapshot.Capture(recipe, optionals);
        RecipeReconciler.Validate(snapshot);
        return snapshot;
    }

    /// <summary>
    /// What the project already rules out for these updates before anything is downloaded: an installed GDExtension, or
    /// an enabled plugin's version constraint on an updated plugin.
    /// </summary>
    internal IReadOnlyList<Finding> UpdatePreflightFindings(IReadOnlyList<UpdateCandidate> candidates)
    {
        var findings = new List<Finding>();
        foreach (var candidate in candidates)
        {
            var directory = ProjectSettings.GlobalizePath(_host.PluginDirectory(candidate.Slug));
            if (Directory.Exists(directory) && AddonPackageValidator.NativeExtension(PackageFiles.Files(directory)) is { } native) findings.Add(native);
        }
        var updated = candidates.ToDictionary(c => c.Slug, StringComparer.Ordinal);
        foreach (var context in _contexts.Where(c => c.Plugin is not null && c.State == EEditorPluginState.Activated))
        {
            var snapshot = Snapshot(context);
            foreach (var dependency in snapshot.PluginDependencies)
            {
                if (dependency.Version is not null && updated.TryGetValue(dependency.Slug, out var candidate) &&
                    !MatchesVersion(candidate.NewVersion, dependency.Version, context.Logger))
                    findings.Add(new("R15", FindingSeverity.Error, $"{context.Slug} requires {dependency.Slug} {dependency.Version}; version {candidate.NewVersion} is incompatible."));
            }
            foreach (var optional in snapshot.AppliedOptionals)
                if (optional.Version is not null && updated.TryGetValue(optional.Slug, out var candidate) && !MatchesVersion(candidate.NewVersion, optional.Version, context.Logger))
                    findings.Add(new("optional_constraint", FindingSeverity.Warning, $"{context.Slug}'s installed optional recipe for {optional.Slug} no longer matches. Its resources remain installed until {context.Slug} is toggled."));
        }
        return findings;
    }

    internal void PrepareUpdateRecipes(UpdateJournal journal)
    {
        foreach (var context in _contexts.Where(c => c.State == EEditorPluginState.Activated && (c.Plugin is not null || c.PluginBase == _ePluginContext)))
        {
            var snapshot = Snapshot(context);
            journal.Recipes[context.Slug] = new() { Old = snapshot, Applied = snapshot.Clone(), WasEnabled = true };
        }
        var order = RecipeReconciler.DependencyOrder(journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Old));
        journal.Plugins = journal.Plugins.OrderBy(p => { var i = order.ToList().IndexOf(p.Slug); return i < 0 ? int.MaxValue : i; }).ToList();
    }

    /// <summary>
    /// Plugins may have been enabled or disabled while an update was kept with build errors: the recipes of the plugins it
    /// does not update are taken from what is enabled now. Those it updates keep their baseline, because the loaded code
    /// may already be their new version.
    /// </summary>
    internal void RebaseUpdateRecipes(UpdateJournal journal)
    {
        var updated = journal.Plugins.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        foreach (var slug in journal.Recipes.Keys.Where(s => !updated.Contains(s)).ToArray()) journal.Recipes.Remove(slug);
        foreach (var context in _contexts.Where(c => c.State == EEditorPluginState.Activated && (c.Plugin is not null || c.PluginBase == _ePluginContext) && !updated.Contains(c.Slug)))
        {
            var snapshot = Snapshot(context);
            journal.Recipes[context.Slug] = new() { Old = snapshot, Applied = snapshot.Clone(), WasEnabled = true };
        }
    }

    internal void ReconcileUpdateRecipes(UpdateJournal journal, bool rollback)
    {
        _recipeUpdateJournal = journal;
        _updateRefreshSuppression++;
        try
        {
            var untouched = UntouchedRecipes(journal);
            foreach (var context in _contexts) context.RefreshMetadata(ReadInstalledMetadata(context.Slug));
            foreach (var entry in journal.Recipes.ToArray())
            {
                var record = entry.Value;
                var context = _contexts.FirstOrDefault(c => c.Slug == entry.Key);
                if (rollback)
                {
                    if (record.Pending is not null) RecipeReconciler.Record(record.Applied, record.Pending);
                    record.Pending = null;
                    record.Target = record.Old.Clone();
                }
                else if (untouched.Contains(entry.Key)) record.Target = record.Applied.Clone();
                else if (context is not null)
                {
                    var updated = journal.Plugins.Any(p => p.Slug == entry.Key);
                    if (updated) context.AppliedOptionalDependencies = null;
                    record.Target = Snapshot(context, updated ? null : record.Old.AppliedOptionals);
                }
                else if (record.WasEnabled) throw new InvalidOperationException($"Enabled plugin {entry.Key} has no instance after the update.");
                else record.Target = new();
            }
            var snapshots = journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Target!);
            var conflicts = snapshots.Values.SelectMany(s => s.Nugets).GroupBy(n => n.Name).Any(g => g.Select(n => (n.Version, n.Source)).Distinct().Count() > 1);
            if (!rollback && conflicts) throw new InvalidOperationException("Updated recipes declare conflicting versions or sources for a shared NuGet package.");
            journal.Save();
            var order = RecipeReconciler.DependencyOrder(snapshots);
            if (rollback) order = order.Reverse().ToArray();
            foreach (var slug in order)
            {
                var record = journal.Recipes[slug];
                var context = _contexts.FirstOrDefault(c => c.Slug == slug);
                if (context is null && record.WasEnabled) throw new InvalidOperationException($"Cannot reconcile {slug}: editor instance is missing.");
                if (context is null) continue;
                var plugin = journal.Plugins.FirstOrDefault(p => p.Slug == slug);
                if (!rollback && record.Pending is { } pending)
                    TrackRecipeOperation(context, pending, () => ExecuteUpdateOperation(context, pending, journal, plugin));
                var prepare = !rollback && plugin is not null && !record.TreePrepared;
                foreach (var operation in RecipeReconciler.Plan(record.Applied, record.Target!, prepare))
                {
                    if (rollback && operation.Kind == RecipeOperationKind.EnsureDependency) continue;
                    TrackRecipeOperation(context, operation, () => ExecuteUpdateOperation(context, operation, journal, plugin));
                }
                record.Applied.PluginDependencies = record.Target!.PluginDependencies.ToList();
                record.Applied.AppliedOptionals = record.Target.AppliedOptionals.ToList();
                context.AppliedOptionalDependencies = record.Target.AppliedOptionals.Select(o =>
                    context.Builder.PluginRecipe.OptionalPluginDependencies.FirstOrDefault(p => p.Slug == o.Slug && p.Version == o.Version)
                    ?? new EEditorPluginRecipe.OptionalPlugin(o.Slug, o.Version, o.Recipe.ToRecipe())).ToList();
                context.State = record.WasEnabled || !rollback ? EEditorPluginState.Activated : EEditorPluginState.Deactivated;
                context.ErrorDetail = null;
                journal.Save();
            }
            if (!rollback)
            {
                // Enabling a new hard dependency can satisfy optional recipes during the first pass.
                // Recompute from the now-enabled set and retain the operation-tracked resource state.
                foreach (var entry in journal.Recipes.ToArray())
                {
                    var context = _contexts.FirstOrDefault(c => c.Slug == entry.Key);
                    // A newly enabled plain dependency has no editor context and contributes no new recipe.
                    if (context is null) { entry.Value.Target ??= entry.Value.Applied.Clone(); continue; }
                    if (untouched.Contains(entry.Key)) { entry.Value.Target = entry.Value.Applied.Clone(); continue; }
                    var updated = journal.Plugins.Any(p => p.Slug == entry.Key);
                    if (updated) context.AppliedOptionalDependencies = null;
                    entry.Value.Target = Snapshot(context, updated ? null : entry.Value.Old.AppliedOptionals);
                }
                if (journal.Recipes.Values.SelectMany(r => r.Target!.Nugets).GroupBy(n => n.Name).Any(g => g.Select(n => (n.Version, n.Source)).Distinct().Count() > 1))
                    throw new InvalidOperationException("Newly enabled dependencies declare conflicting versions or sources for a shared NuGet package.");
                journal.Save();
                foreach (var slug in RecipeReconciler.DependencyOrder(journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Target!)))
                {
                    var context = _contexts.FirstOrDefault(c => c.Slug == slug);
                    if (context is null) continue;
                    var record = journal.Recipes[slug];
                    foreach (var operation in RecipeReconciler.Plan(record.Applied, record.Target!))
                        TrackRecipeOperation(context, operation, () => ExecuteUpdateOperation(context, operation, journal, journal.Plugins.FirstOrDefault(p => p.Slug == slug)));
                    record.Applied = record.Target!.Clone();
                    context.AppliedOptionalDependencies = record.Target.AppliedOptionals.Select(o =>
                        context.Builder.PluginRecipe.OptionalPluginDependencies.FirstOrDefault(p => p.Slug == o.Slug && p.Version == o.Version)
                        ?? new EEditorPluginRecipe.OptionalPlugin(o.Slug, o.Version, o.Recipe.ToRecipe())).ToList();
                    journal.Save();
                }
            }
            if (rollback)
            {
                foreach (var entry in journal.Recipes.Where(r => !r.Value.WasEnabled).Reverse())
                {
                    var context = _contexts.FirstOrDefault(c => c.Slug == entry.Key);
                    if (context is not null) context.State = EEditorPluginState.Deactivated;
                    if (_host.IsPluginEnabled(entry.Key)) _host.SetPluginEnabled(entry.Key, false);
                }
            }
            else
            {
                foreach (var plugin in journal.Plugins)
                {
                    var context = _contexts.FirstOrDefault(c => c.Slug == plugin.Slug);
                    if (context is not null) context.State = EEditorPluginState.Activated;
                }
            }
        }
        finally { _recipeUpdateJournal = null; _updateRefreshSuppression--; }
    }
    /// <summary>
    /// Plugins this journal must leave as they are: those another group of the batch has swapped but not reconciled yet,
    /// whose loaded code may already be their new version, and those rolled back since this assembly was loaded, whose
    /// loaded code still is. Their recipes are reconciled by their own journal, or after the next restart.
    /// </summary>
    private HashSet<string> UntouchedRecipes(UpdateJournal journal)
    {
        var untouched = new HashSet<string>(_rolledBackUpdates, StringComparer.Ordinal);
        foreach (var other in _updateJournals?.Read().Where(j => j.IsActive && j.Id != journal.Id) ?? [])
            untouched.UnionWith(other.Plugins.Select(p => p.Slug));
        untouched.ExceptWith(journal.Plugins.Select(p => p.Slug));
        return untouched;
    }

    private void ExecuteUpdateOperation(PluginContext context, RecipeOperation operation, UpdateJournal journal, UpdatePluginJournal? plugin)
    {
        switch (operation.Kind)
        {
            case RecipeOperationKind.RemoveAutoload: ReverseAutoload(context, operation.Autoload!); break;
            case RecipeOperationKind.RemoveProject: ReverseProject(context, operation.Project!); break;
            case RecipeOperationKind.HideDirectory: ShowHideHelper.HideDirectory(context, operation.Directory!); break;
            case RecipeOperationKind.ShowDirectory: ShowHideHelper.ShowDirectory(context, operation.Directory!); break;
            case RecipeOperationKind.RemoveNuget: ReverseNuget(context, operation.Nuget!); break;
            case RecipeOperationKind.AddNuget: ApplyNuget(context, operation.Nuget!); break;
            case RecipeOperationKind.AddProject: ApplyProject(context, operation.Project!); break;
            case RecipeOperationKind.AddAutoload: ApplyAutoload(context, operation.Autoload!); break;
            case RecipeOperationKind.EnsureDependency: EnsureUpdateDependency(operation.Dependency!); break;
            case RecipeOperationKind.PrepareTree:
                if (plugin is not null)
                {
                    var root = ProjectSettings.GlobalizePath(_host.PluginDirectory(context.Slug));
                    InterimBridge.Remove(root, plugin.Preserved);
                    UidMap.Restore(root, plugin.Uids);
                    journal.Recipes[context.Slug].TreePrepared = true;
                }
                break;
        }
    }
}
#endif
