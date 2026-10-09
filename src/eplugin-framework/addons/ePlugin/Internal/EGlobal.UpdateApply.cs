#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private UpdateJournals? _updateJournals;
    private bool _fetchingUpdate;
    private UpdateApplier? _updateApplier;
    internal event Action<UpdateJournal>? UpdateDecisionNeeded;
    private string UpdateProjectRoot => ProjectSettings.GlobalizePath("res://");
    private void InitializeUpdateJournals() => _updateJournals = new(Path.Combine(UpdateProjectRoot, ".godot/eplugin/updates"), message => _ePluginContext?.Logger.Error(message));
    private UpdateApplier Applier()
    {
        if (_stateStore is null || _updateCache is null) throw new InvalidOperationException("Update system is not initialized.");
        return _updateApplier ??= new(UpdateProjectRoot, _stateStore, new GodotUpdateHost(this), _updateCache);
    }

    internal async Task<(IReadOnlyList<ValidatedPackage> Packages, string Directory)> StageUpdatesAsync(IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress, CancellationToken ct, bool allowDowngrade = false)
    {
        if (_fetchingUpdate || _updateJournals is null || _updateJournals.Read().Any(j => j.IsActive)) throw new InvalidOperationException("An update is already in progress.");
        _fetchingUpdate = true;
        var targets = CollectUpdateTargets();
        var directory = _updateJournals.NewDirectory();
        try
        {
            var result = await Task.Run(() => new PackageFetcher(new(), new GitRunner()).FetchAsync(candidates, targets, directory, progress, ct, allowDowngrade), ct);
            return (result, directory);
        }
        finally { _fetchingUpdate = false; }
    }

    internal async Task<UpdateOutcome> ApplyUpdatesAsync(IReadOnlyList<ValidatedPackage> packages, string directory, bool trustChangedSources)
    {
        RefreshPlainPlugins();
        var ct = _ePluginContext!.UpdateLifetime;
        var filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (filesystem.IsScanning())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Editor filesystem scan did not finish.");
            await _ePluginContext.ToSignal(_ePluginContext.GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        }
        using var progress = ActivationProgress.Begin("Updating plugins...");
        var result = Applier().Apply(packages, directory, trustChangedSources);
        HandleUpdateOutcome(directory, result);
        return result;
    }
    private void HandleUpdateOutcome(string directory, UpdateOutcome result)
    {
        if (result == UpdateOutcome.AwaitingDecision)
        {
            var journal = UpdateJournal.Load(directory);
            Callable.From(() =>
            {
                if (UpdateDecisionNeeded is null) Applier().Rollback(journal, "No build-failure dialog is available.");
                else UpdateDecisionNeeded(journal);
            }).CallDeferred();
        }
        if (result is UpdateOutcome.Completed or UpdateOutcome.RolledBack) RefreshPendingUpdates();
    }
    internal void DecideUpdate(UpdateJournal journal, bool keep)
    {
        using var progress = ActivationProgress.Begin(keep ? "Keeping updated plugins..." : "Restoring plugins...");
        var result = keep ? Applier().Keep(journal) : Applier().Rollback(journal, "User requested rollback.");
        HandleUpdateOutcome(journal.Directory, result);
    }
    private void ResumeUpdates()
    {
        if (_updateJournals is null || _stateStore is null) return;
        // Cache is needed by recovery before the first scheduled check.
        _updateCache ??= new UpdateStateStore(Path.Combine(UpdateProjectRoot, ".godot/eplugin/update-state.json"));
        _updateCache.Load();
        foreach (var journal in _updateJournals.Read())
        {
            if (_stateStore.LastCompletedAttemptId == journal.AttemptId)
            {
                Directory.Delete(journal.Directory, true);
                continue;
            }
            var attempts = _stateStore.LocalAttempts.Where(a => a.AttemptId == journal.AttemptId).ToArray();
            if (attempts.Any(a => a.Reason.StartsWith("update_", StringComparison.Ordinal)))
            {
                _ePluginContext?.Logger.Warn($"Update needs manual recovery: {journal.Directory}. Use Retry failed in the ePlugin Manager.");
                continue;
            }
            UpdateOutcome result;
            switch (journal.State)
            {
                case UpdatePhase.Staged:
                    if (attempts.Length > 0 && !_stateStore.TryAbandonAttempt(journal.AttemptId)) continue;
                    Directory.Delete(journal.Directory, true);
                    continue;
                case UpdatePhase.Verified:
                    if (attempts.Length == 0) { _ePluginContext?.Logger.Error($"Update marker is missing: {journal.Directory}; repair manually."); continue; }
                    result = Applier().Commit(journal);
                    break;
                case UpdatePhase.InterimBuilt:
                case UpdatePhase.Reconciling:
                case UpdatePhase.Reconciled:
                    result = attempts.Length == 0 ? Applier().Rollback(journal, "Update marker was lost.") : Applier().Resume(journal);
                    break;
                case UpdatePhase.AwaitingDecision:
                    result = new GodotUpdateHost(this).UiAvailable ? UpdateOutcome.AwaitingDecision : Applier().Rollback(journal, "Cannot show the build-failure dialog.");
                    break;
                case UpdatePhase.Swapped:
                case UpdatePhase.RollingBack:
                    result = Applier().Rollback(journal, "Interrupted update.");
                    break;
                default:
                    // Retain diagnostic journals and kept-version backups for at least seven days.
                    if (DateTimeOffset.UtcNow - journal.CreatedUtc > TimeSpan.FromDays(7) && attempts.Length == 0) Directory.Delete(journal.Directory, true);
                    continue;
            }
            HandleUpdateOutcome(journal.Directory, result);
        }
    }
    private void RetryUpdate(UpdateJournal? journal)
    {
        if (journal is null) { _ePluginContext?.Logger.Error("Update journal is missing; restore addon files and project references manually before clearing local state."); return; }
        using var progress = ActivationProgress.Begin("Recovering plugin update...");
        HandleUpdateOutcome(journal.Directory, Applier().Retry(journal));
    }

    private sealed class GodotUpdateHost(EGlobal global) : IUpdateHost
    {
        // Called by name: both methods exist only since Godot 4.5, and plugins compile against the project's Godot SDK.
        private static readonly StringName CloseScene = "close_scene";
        private static readonly StringName GetOpenSceneRoots = "get_open_scene_roots";
        private const int MaxClosedScenes = 1000;
        public bool UiAvailable => DisplayServer.GetName() != "headless" && global._ePluginContext?.IsInsideTree() == true;
        public string BuildFailurePolicy => ProjectSettings.GetSetting("eplugin/updates/on_build_failure", "ask").AsString();
        public bool RestartAlways => ProjectSettings.GetSetting("eplugin/updates/restart_policy", "auto").AsString() == "always";
        public bool IsEnabled(string slug) => EditorInterface.Singleton.IsPluginEnabled(slug);
        public bool IsManaged(string slug) => global.IsManagedPlugin(slug);
        public void Preflight(IReadOnlyList<ValidatedPackage> packages)
        {
            if (EditorInterface.Singleton.IsPlayingScene()) throw new InvalidOperationException("Stop the running scene before updating plugins.");
            // Closing discards what SaveAllScenes cannot save, so refuse before the marker rather than lose an untitled scene.
            if (HasUntitledScene())
                throw new InvalidOperationException("Save or close untitled scenes before updating plugins.");
            foreach (var package in packages.Where(p => IsManaged(p.Candidate.Slug)))
                if (global._contexts.FirstOrDefault(c => c.Slug == package.Candidate.Slug)?.State != EEditorPluginState.Activated)
                    throw new InvalidOperationException($"Finish enabling {package.Candidate.Slug} before updating it.");
            var findings = global.UpdatePreflightFindings(packages.Select(p => p.Candidate with { NewVersion = PluginIni.Parse(File.ReadAllText(Path.Combine(p.StagingDir, "plugin.cfg")))["version"] }).ToArray());
            if (findings.Any(f => f.Severity == FindingSeverity.Error)) throw new InvalidOperationException(string.Join("; ", findings.Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Message)));
            foreach (var warning in findings.Where(f => f.Severity == FindingSeverity.Warning)) Log(warning.Message);
            if (global._toCheckEnable.Any() || global._toCheckDisable.Any()) throw new InvalidOperationException("Finish plugin transitions before updating.");
            if (packages.Any(p => p.ContainsCSharp) && global.GetOrCreateContext(global._ePluginContext!).Cli is not ICheckedDotnetCli)
                throw new InvalidOperationException("A working dotnet CLI is required for C# updates.");
        }
        public void PrepareJournal(UpdateJournal journal) => global.PrepareUpdateRecipes(journal);
        public void BeforeSwap(UpdateJournal journal)
        {
            if (journal.Plugins.Any(p => p.Slug == "ePlugin")) ActivationProgress.ForceCloseForUpdate();
        }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin)
        {
            if (!plugin.IsEPlugin || !journal.Recipes.TryGetValue(plugin.Slug, out var recipe)) return;
            plugin.Preserved = InterimBridge.Restore(plugin.Slug, recipe.Old, journal.Backup(plugin.Slug), Path.Combine(global.UpdateProjectRoot, "addons", plugin.Slug));
            journal.Save();
        }
        public void Reconcile(UpdateJournal journal, bool rollback) => global.ReconcileUpdateRecipes(journal, rollback);
        public void SetPlainEnabled(string slug, bool enabled) => EditorInterface.Singleton.SetPluginEnabled(slug, enabled);
        public void CloseScenes()
        {
            var editor = EditorInterface.Singleton;
            editor.SaveAllScenes();
            if (!editor.HasMethod(CloseScene))
            {
                Log("Open scenes stay open during the update; closing them needs Godot 4.5 or newer.");
                return;
            }
            if (HasUntitledScene())
            {
                Log("Open scenes stay open during the update; save or close untitled scenes first.");
                return;
            }
            // close_scene closes the active tab and returns DoesNotExist once only the empty untitled tab is left.
            for (var closed = 0; closed < MaxClosedScenes; closed++)
                if ((Error)editor.Call(CloseScene).AsInt32() != Error.Ok) return;
            Log("Some scenes could not be closed before the update.");
        }
        private static bool HasUntitledScene() => EditorInterface.Singleton.HasMethod(GetOpenSceneRoots) &&
            EditorInterface.Singleton.Call(GetOpenSceneRoots).AsGodotArray<Node>().Any(root => root is not null && string.IsNullOrEmpty(root.SceneFilePath));
        public void Scan() => EditorInterface.Singleton.GetResourceFilesystem().Scan();
        public BuildOutcome Build()
        {
            ActivationProgress.SetText("Building updated project...");
            return (global.GetOrCreateContext(global._ePluginContext!).Cli as ICheckedDotnetCli)?.TryBuild() ?? new(1, ["Checked dotnet CLI is unavailable."]);
        }
        public void RequestReload(UpdateJournal journal)
        {
            // The full in-place fixture exposed unreliable collectible-assembly reload on Godot 4.7.2.
            // Auto therefore uses the journal-backed restart path rather than a callback that pins old code.
            if (journal.IsActive) { journal.RestartRequired = true; journal.Save(); }
            Log("Restarting editor to load the updated assembly. If automatic launch fails, reopen the project to resume.");
            EditorInterface.Singleton.CallDeferred(EditorInterface.MethodName.RestartEditor, true);
        }
        public bool Verify(UpdatePluginJournal plugin)
        {
            var metadata = EditorPluginExtensions.ReadMetadata($"res://addons/{plugin.Slug}/plugin.cfg");
            return (plugin.Slug != "ePlugin" || global.IsValid()) && IsEnabled(plugin.Slug) && metadata?.Version == plugin.NewVersion &&
                global._contexts.FirstOrDefault(c => c.Slug == plugin.Slug)?.State != EEditorPluginState.Error;
        }
        public void Log(string message) => global._ePluginContext?.Logger.Log(message);
    }
}
#endif
