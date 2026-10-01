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

    internal async Task<(IReadOnlyList<ValidatedPackage> Packages, string Directory)> StageUpdatesAsync(IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress, CancellationToken ct)
    {
        if (_fetchingUpdate || _updateJournals is null || _updateJournals.Read().Any(j => j.IsActive)) throw new InvalidOperationException("An update is already in progress.");
        _fetchingUpdate = true;
        var targets = CollectUpdateTargets();
        var directory = _updateJournals.NewDirectory();
        try
        {
            var result = await Task.Run(() => new PackageFetcher(new(), new GitRunner()).FetchAsync(candidates, targets, directory, progress, ct), ct);
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
        if (result is UpdateOutcome.Completed or UpdateOutcome.RolledBack) RefreshPlainPlugins();
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
                _ePluginContext?.Logger.Warn($"Update needs manual recovery: {journal.Directory}. Use Retry failed ePlugin addons.");
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
        public bool UiAvailable => DisplayServer.GetName() != "headless" && global._ePluginContext?.IsInsideTree() == true;
        public string BuildFailurePolicy => ProjectSettings.GetSetting("eplugin/updates/on_build_failure", "ask").AsString();
        public bool RestartAlways => ProjectSettings.GetSetting("eplugin/updates/restart_policy", "auto").AsString() == "always";
        public bool IsEnabled(string slug) => EditorInterface.Singleton.IsPluginEnabled(slug);
        public bool IsManaged(string slug) => global.IsManagedPlugin(slug);
        public void Preflight(IReadOnlyList<ValidatedPackage> packages)
        {
            if (EditorInterface.Singleton.IsPlayingScene()) throw new InvalidOperationException("Stop the running scene before updating plugins.");
            if (packages.Any(p => IsManaged(p.Candidate.Slug))) throw new InvalidOperationException("Managed recipe updates are not available in this milestone.");
            if (global._toCheckEnable.Any() || global._toCheckDisable.Any()) throw new InvalidOperationException("Finish plugin transitions before updating.");
            if (packages.Any(p => p.ContainsCSharp) && global.GetOrCreateContext(global._ePluginContext!).Cli is not ICheckedDotnetCli)
                throw new InvalidOperationException("A working dotnet CLI is required for C# updates.");
        }
        public void PrepareJournal(UpdateJournal journal) { }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { }
        public void Reconcile(UpdateJournal journal, bool rollback)
        {
            foreach (var plugin in journal.Plugins)
            {
                var context = global._contexts.FirstOrDefault(c => c.Slug == plugin.Slug);
                context?.RefreshMetadata();
                if (context is not null) context.State = EEditorPluginState.Activated;
            }
        }
        public void SetPlainEnabled(string slug, bool enabled) => EditorInterface.Singleton.SetPluginEnabled(slug, enabled);
        public void SaveScenes() => EditorInterface.Singleton.SaveAllScenes();
        public void Scan() => EditorInterface.Singleton.GetResourceFilesystem().Scan();
        public BuildOutcome Build()
        {
            ActivationProgress.SetText("Building updated project...");
            return (global.GetOrCreateContext(global._ePluginContext!).Cli as ICheckedDotnetCli)?.TryBuild() ?? new(1, ["Checked dotnet CLI is unavailable."]);
        }
        public void RequestReload(UpdateJournal journal)
        {
            if (RestartAlways || journal.Plugins.Any(p => p.Slug == "ePlugin"))
            {
                journal.RestartRequired = true; journal.Save();
                Log("Restarting editor to finish the update. If automatic launch fails, reopen the project to resume.");
                Callable.From(() => EditorInterface.Singleton.RestartEditor(true)).CallDeferred();
            }
            else
            {
                var attempt = journal.AttemptId;
                var timer = global._ePluginContext!.GetTree().CreateTimer(30);
                timer.Timeout += () =>
                {
                    if (global._updateJournals?.Read().Any(j => j.AttemptId == attempt && j.State == UpdatePhase.InterimBuilt) == true)
                    { Log("Assembly reload did not resume the update; restarting. Reopen the project if automatic launch fails."); EditorInterface.Singleton.RestartEditor(true); }
                };
            }
        }
        public bool Verify(UpdatePluginJournal plugin)
        {
            var metadata = EditorPluginExtensions.ReadMetadata($"res://addons/{plugin.Slug}/plugin.cfg");
            return IsEnabled(plugin.Slug) && metadata?.Version == plugin.NewVersion &&
                global._contexts.FirstOrDefault(c => c.Slug == plugin.Slug)?.State != EEditorPluginState.Error;
        }
        public void Log(string message) => global._ePluginContext?.Logger.Log(message);
    }
}
#endif
