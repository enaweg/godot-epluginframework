#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
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
            optionals = OptionalRecipes(recipe, retain).Concat(ResolveOptionalRecipes(context, recipe)).DistinctBy(o => (o.Slug, o.Version)).ToList();
        var snapshot = RecipeSnapshot.Capture(recipe, optionals);
        RecipeReconciler.Validate(snapshot);
        return snapshot;
    }

    /// <summary>
    /// What the project already rules out for these updates before anything is downloaded: a GDExtension in the installed
    /// framework (every other plugin updates its own, see <see cref="GDExtensionPluginWorkflow"/>), or an enabled plugin's
    /// version constraint on an updated plugin.
    /// </summary>
    internal IReadOnlyList<Finding> UpdatePreflightFindings(IReadOnlyList<UpdateCandidate> candidates)
    {
        var findings = new List<Finding>();
        foreach (var candidate in candidates)
        {
            var directory = ProjectSettings.GlobalizePath(_host.PluginDirectory(candidate.Slug));
            if (candidate.Slug == FrameworkSlug && Directory.Exists(directory) &&
                PluginKindWorkflow.RefuseNativeExtension(PackageFiles.Files(directory)) is { } native) findings.Add(native);
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
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) positions.TryAdd(order[i], i);
        journal.Plugins = journal.Plugins.OrderBy(p => positions.GetValueOrDefault(p.Slug, int.MaxValue)).ToList();
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

    /// <summary>
    /// Brings every recipe of the journal to what it is after the update (<see cref="ApplyUpdateRecipes"/>) or, on
    /// rollback, back to what it was before (<see cref="ReverseUpdateRecipes"/>), one tracked operation at a time.
    /// </summary>
    internal void ReconcileUpdateRecipes(UpdateJournal journal, bool rollback)
    {
        _recipeUpdateJournal = journal;
        _updateRefreshSuppression++;
        try
        {
            if (rollback) ReverseUpdateRecipes(journal);
            else ApplyUpdateRecipes(journal);
        }
        finally { _recipeUpdateJournal = null; _updateRefreshSuppression--; }
    }

    private void ApplyUpdateRecipes(UpdateJournal journal)
    {
        var untouched = UntouchedRecipes(journal);
        foreach (var context in _contexts) context.RefreshMetadata(ReadInstalledMetadata(context.Slug));
        foreach (var (slug, record) in journal.Recipes.ToArray())
        {
            var context = _contexts.FirstOrDefault(c => c.Slug == slug);
            if (untouched.Contains(slug)) record.Target = record.Applied.Clone();
            else if (context is not null) record.Target = UpdateTarget(context, journal, record);
            else if (record.WasEnabled) throw new InvalidOperationException($"Enabled plugin {slug} has no instance after the update.");
            else record.Target = new();
        }
        var snapshots = journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Target!);
        if (HasNugetConflict(snapshots.Values)) throw new InvalidOperationException("Updated recipes declare conflicting versions or sources for a shared NuGet package.");
        journal.Save();
        foreach (var slug in RecipeReconciler.DependencyOrder(snapshots))
        {
            var record = journal.Recipes[slug];
            if (ReconciledContext(slug, record) is not { } context) continue;
            var plugin = journal.Plugins.FirstOrDefault(p => p.Slug == slug);
            if (record.Pending is { } pending)
                TrackRecipeOperation(context, pending, () => ExecuteUpdateOperation(context, pending, journal, plugin));
            foreach (var operation in RecipeReconciler.Plan(record.Applied, record.Target!, prepareTree: plugin is not null && !record.TreePrepared))
                TrackRecipeOperation(context, operation, () => ExecuteUpdateOperation(context, operation, journal, plugin));
            MarkReconciled(context, record, EEditorPluginState.Activated);
            journal.Save();
        }
        // Enabling a new hard dependency can satisfy optional recipes during the first pass.
        // Recompute from the now-enabled set and retain the operation-tracked resource state.
        foreach (var (slug, record) in journal.Recipes.ToArray())
        {
            var context = _contexts.FirstOrDefault(c => c.Slug == slug);
            // A newly enabled plain dependency has no editor context and contributes no new recipe.
            if (context is null) record.Target ??= record.Applied.Clone();
            else record.Target = untouched.Contains(slug) ? record.Applied.Clone() : UpdateTarget(context, journal, record);
        }
        if (HasNugetConflict(journal.Recipes.Values.Select(r => r.Target!)))
            throw new InvalidOperationException("Newly enabled dependencies declare conflicting versions or sources for a shared NuGet package.");
        journal.Save();
        foreach (var slug in RecipeReconciler.DependencyOrder(journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Target!)))
        {
            var context = _contexts.FirstOrDefault(c => c.Slug == slug);
            if (context is null) continue;
            var record = journal.Recipes[slug];
            var plugin = journal.Plugins.FirstOrDefault(p => p.Slug == slug);
            foreach (var operation in RecipeReconciler.Plan(record.Applied, record.Target!))
                TrackRecipeOperation(context, operation, () => ExecuteUpdateOperation(context, operation, journal, plugin));
            record.Applied = record.Target!.Clone();
            context.AppliedOptionalDependencies = OptionalRecipes(context.Builder.PluginRecipe, record.Target.AppliedOptionals).ToList();
            journal.Save();
        }
        foreach (var plugin in journal.Plugins)
        {
            var context = _contexts.FirstOrDefault(c => c.Slug == plugin.Slug);
            if (context is not null) context.State = EEditorPluginState.Activated;
        }
    }

    private void ReverseUpdateRecipes(UpdateJournal journal)
    {
        foreach (var context in _contexts) context.RefreshMetadata(ReadInstalledMetadata(context.Slug));
        foreach (var record in journal.Recipes.Values.ToArray())
        {
            if (record.Pending is not null) RecipeReconciler.Record(record.Applied, record.Pending);
            record.Pending = null;
            record.Target = record.Old.Clone();
        }
        var snapshots = journal.Recipes.ToDictionary(p => p.Key, p => p.Value.Target!);
        journal.Save();
        foreach (var slug in RecipeReconciler.DependencyOrder(snapshots).Reverse())
        {
            var record = journal.Recipes[slug];
            if (ReconciledContext(slug, record) is not { } context) continue;
            var plugin = journal.Plugins.FirstOrDefault(p => p.Slug == slug);
            // A rollback never enables a plugin.
            foreach (var operation in RecipeReconciler.Plan(record.Applied, record.Target!, prepareTree: false).Where(o => o.Kind != RecipeOperationKind.EnsureDependency))
                TrackRecipeOperation(context, operation, () => ExecuteUpdateOperation(context, operation, journal, plugin));
            MarkReconciled(context, record, record.WasEnabled ? EEditorPluginState.Activated : EEditorPluginState.Deactivated);
            journal.Save();
        }
        foreach (var entry in journal.Recipes.Where(r => !r.Value.WasEnabled).Reverse())
        {
            var context = _contexts.FirstOrDefault(c => c.Slug == entry.Key);
            if (context is not null) context.State = EEditorPluginState.Deactivated;
            if (_host.IsPluginEnabled(entry.Key)) _host.SetPluginEnabled(entry.Key, false);
        }
    }

    /// <summary>
    /// What a plugin's recipe is reconciled to after the update: an updated plugin's new recipe with its optional recipes
    /// resolved again, any other plugin's recipe keeping the optional recipes it had applied.
    /// </summary>
    private RecipeSnapshot UpdateTarget(PluginContext context, UpdateJournal journal, RecipeJournal record)
    {
        var updated = journal.Plugins.Any(p => p.Slug == context.Slug);
        if (updated) context.AppliedOptionalDependencies = null;
        return Snapshot(context, updated ? null : record.Old.AppliedOptionals);
    }

    /// <summary>The context whose recipe is reconciled; null for a plugin that was not enabled and has none.</summary>
    private PluginContext? ReconciledContext(string slug, RecipeJournal record)
    {
        var context = _contexts.FirstOrDefault(c => c.Slug == slug);
        if (context is null && record.WasEnabled) throw new InvalidOperationException($"Cannot reconcile {slug}: editor instance is missing.");
        return context;
    }

    /// <summary>Records that a plugin's dependencies and optional recipes are those of its target now.</summary>
    private static void MarkReconciled(PluginContext context, RecipeJournal record, EEditorPluginState state)
    {
        record.Applied.PluginDependencies = record.Target!.PluginDependencies.ToList();
        record.Applied.AppliedOptionals = record.Target.AppliedOptionals.ToList();
        context.AppliedOptionalDependencies = OptionalRecipes(context.Builder.PluginRecipe, record.Target.AppliedOptionals).ToList();
        context.State = state;
        context.ErrorDetail = null;
    }

    /// <summary>
    /// The optional dependencies of <paramref name="recipe"/> that <paramref name="applied"/> names, or the recipes the
    /// snapshot recorded for those it no longer declares.
    /// </summary>
    private static IEnumerable<EEditorPluginRecipe.OptionalPlugin> OptionalRecipes(EEditorPluginRecipe recipe, IEnumerable<OptionalRecipeSnapshot> applied) =>
        applied.Select(o => recipe.OptionalPluginDependencies.FirstOrDefault(p => p.Slug == o.Slug && p.Version == o.Version)
            ?? new EEditorPluginRecipe.OptionalPlugin(o.Slug, o.Version, o.Recipe.ToRecipe()));

    /// <summary>Whether the recipes declare one NuGet package with different versions or sources.</summary>
    private static bool HasNugetConflict(IEnumerable<RecipeSnapshot> snapshots) =>
        snapshots.SelectMany(s => s.Nugets).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(n => (n.Version, n.Source)).Distinct().Count() > 1);
    /// <summary>
    /// Plugins this journal must leave as they are: those another update has swapped but not reconciled yet (another
    /// group of the batch, or an update kept before reconciling that Retry still resumes), whose loaded code may already
    /// be their new version, and those rolled back since this assembly was loaded, whose loaded code still is. Their
    /// recipes are reconciled by their own journal, or after the next restart.
    /// </summary>
    private HashSet<string> UntouchedRecipes(UpdateJournal journal)
    {
        var untouched = new HashSet<string>(_rolledBackUpdates, StringComparer.Ordinal);
        foreach (var other in _updateJournals?.Read().Where(j => j.Id != journal.Id && (j.IsActive || IsKeptBeforeReconcile(j))) ?? [])
            untouched.UnionWith(other.Plugins.Select(p => p.Slug));
        untouched.ExceptWith(journal.Plugins.Select(p => p.Slug));
        return untouched;
    }

    /// <summary>Kept with build errors before reconciling, with the marker Retry resumes it by.</summary>
    private bool IsKeptBeforeReconcile(UpdateJournal journal) =>
        journal is { State: UpdatePhase.CommittedWithErrors, BeforeReconcile: true } &&
        _stateStore?.LocalAttempts.Any(a => a.AttemptId == journal.AttemptId) == true;

    /// <summary>
    /// The recipes of the journal's other plugins, every activated one, as they are reconciled to: like
    /// <see cref="RemainingRecipes"/> when disabling, a project or autoload one of them declares stays installed.
    /// </summary>
    private static List<EEditorPluginRecipe> OtherUpdateRecipes(PluginContext context, UpdateJournal journal) =>
        journal.Recipes.Where(r => r.Key != context.Slug).Select(r => (r.Value.Target ?? r.Value.Applied).ToRecipe()).ToList();

    private void ExecuteUpdateOperation(PluginContext context, RecipeOperation operation, UpdateJournal journal, UpdatePluginJournal? plugin)
    {
        switch (operation.Kind)
        {
            case RecipeOperationKind.RemoveAutoload: ReverseAutoload(context, operation.Autoload!, OtherUpdateRecipes(context, journal)); break;
            case RecipeOperationKind.RemoveProject: ReverseProject(context, operation.Project!, OtherUpdateRecipes(context, journal)); break;
            case RecipeOperationKind.HideDirectory: ShowHideHelper.HideDirectory(context, operation.Directory!, _host); break;
            case RecipeOperationKind.ShowDirectory: ShowHideHelper.ShowDirectory(context, operation.Directory!, _host); break;
            case RecipeOperationKind.RemoveNuget: ReverseNuget(context, operation.Nuget!); break;
            case RecipeOperationKind.AddNuget: ApplyNuget(context, operation.Nuget!); break;
            case RecipeOperationKind.AddProject: ApplyProject(context, operation.Project!); break;
            case RecipeOperationKind.AddAutoload: ApplyAutoload(context, operation.Autoload!, OtherUpdateRecipes(context, journal)); break;
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
