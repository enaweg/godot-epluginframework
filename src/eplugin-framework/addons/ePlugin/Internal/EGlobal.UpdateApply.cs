#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private UpdateJournals? _updateJournals;
    private bool _fetchingUpdate;
    private UpdateApplier? _updateApplier;
    internal event Action<UpdateJournal>? UpdateDecisionNeeded;
    /// <summary>Raised when an update schedules an editor restart, before the restart runs.</summary>
    internal event Action? RestartRequested;
    /// <summary>True once an editor restart was scheduled in this session.</summary>
    internal bool IsRestartPending { get; private set; }
    // While positive, restarts requested by update transactions wait until the last of them is done.
    private int _restartDeferrals;
    private bool _restartDeferred;
    private bool _deferScans;
    private bool _scanDeferred;
    // Plugins rolled back since this assembly was loaded: it still holds their new code until the editor restarts.
    private readonly HashSet<string> _rolledBackUpdates = new(StringComparer.Ordinal);
    private IUpdateHost? _updateHost;
    private string UpdateProjectRoot => _host.ProjectRoot;
    /// <summary>What update transactions need from the editor; tests set a fake before the first transaction.</summary>
    internal IUpdateHost UpdateHost
    {
        get => _updateHost ??= new GodotUpdateHost(this);
        set => _updateHost = value;
    }
    internal void InitializeUpdateJournals() => _updateJournals = new(Path.Combine(UpdateProjectRoot, ".godot/eplugin/updates"), message => _ePluginContext?.Logger.Error(message));
    private UpdateApplier Applier()
    {
        if (_stateStore is null || _updateCache is null) throw new InvalidOperationException("Update system is not initialized.");
        return _updateApplier ??= new(UpdateProjectRoot, _stateStore, UpdateHost, _updateCache);
    }

    /// <summary>
    /// Stages and validates a batch. Packages that cannot be fetched or are refused are returned apart and the rest can
    /// still be installed; it throws only when every package was refused.
    /// </summary>
    internal async Task<(IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused, string Directory)> StageUpdatesAsync(IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress, CancellationToken ct, bool allowDowngrade = false, bool allowReinstall = false)
    {
        if (_fetchingUpdate || _updateJournals is null || _updateJournals.Read().Any(j => j.IsActive)) throw new InvalidOperationException("An update is already in progress.");
        _fetchingUpdate = true;
        var targets = CollectUpdateTargets();
        var directory = _updateJournals.NewDirectory();
        try
        {
            var result = await Task.Run(() => new PackageFetcher(new(), new GitRunner()).FetchAsync(candidates, targets, directory, progress, ct, allowDowngrade, allowReinstall), ct);
            foreach (var refused in result.Refused) _ePluginContext?.Logger.Warn($"Skipping the update of {refused.Candidate.Slug} {refused.Candidate.NewVersion}: {refused.Reason}");
            return (result.Packages, result.Refused, directory);
        }
        finally { _fetchingUpdate = false; }
    }

    /// <summary>
    /// Installs a staged batch in independent groups (see <see cref="UpdateGroups"/>): a group that fails is rolled back
    /// alone. All groups run in one go without letting the editor process frames, so it cannot reload an assembly one of
    /// them built; the editor restarts at most once, after the last group.
    /// </summary>
    /// <param name="askOnBuildFailure">Set for an update started from a plugin's details: a build failure may then ask
    /// whether to keep the new version instead of rolling it back.</param>
    internal async Task<IReadOnlyList<UpdateGroupResult>> ApplyUpdatesAsync(IReadOnlyList<ValidatedPackage> packages, string directory, bool trustChangedSources,
        bool askOnBuildFailure = false)
    {
        RefreshPlainPlugins();
        var ct = _ePluginContext!.UpdateLifetime;
        // Restored when this method returns, before the deferred editor restart saves the editor settings.
        using var unfocusedScans = _host.SuspendUnfocusedImports();
        // Opened before waiting for the filesystem scan, so the window covers the whole installation.
        using var progress = ActivationProgress.Begin("Updating plugins...");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (_host.IsScanningFilesystem)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Editor filesystem scan did not finish. Wait until the editor is idle and try again.");
            await _ePluginContext.ToSignal(_ePluginContext.GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        }
        var groups = UpdateGroupsOf(packages);
        using var restarts = DeferRestarts();
        // The editor drops a scan requested while one runs, so a scan per group would miss later groups: scan once after all.
        using var scans = groups.Length > 1 ? DeferScans() : null;
        return Applier().ApplyGroups(groups, directory, _updateJournals!.NewDirectory, trustChangedSources,
            (index, group) =>
            {
                if (groups.Length == 1) return;
                ActivationProgress.SetText($"Updating {GroupNames(group)} ({index + 1} of {groups.Length})...");
            },
            result =>
            {
                if (result.Outcome is { } outcome) HandleUpdateOutcome(result.Directory, outcome);
            }, askOnBuildFailure);
    }

    /// <summary>What a build failure does to an update of this plugin started from its details: ask, keep or rollback.</summary>
    internal string DetailsBuildFailurePolicy(string slug)
    {
        var host = UpdateHost;
        return UpdateFailureDecision.Choose(slug == "ePlugin", UpdateFailureDecision.Policy(host.BuildFailurePolicy, false, true), host.UiAvailable);
    }

    private static string GroupNames(IReadOnlyList<ValidatedPackage> group) => string.Join(", ", group.Select(p => p.Candidate.PluginName));

    private IReadOnlyList<ValidatedPackage>[] UpdateGroupsOf(IReadOnlyList<ValidatedPackage> packages)
    {
        var dependencies = _contexts.Where(c => c.Plugin is not null && c.State == EEditorPluginState.Activated).DistinctBy(c => c.Slug)
            .ToDictionary(c => c.Slug, c => (IReadOnlyCollection<string>)Snapshot(c).PluginDependencies.Select(d => d.Slug).ToArray(), StringComparer.Ordinal);
        return UpdateGroups.Plan(packages.Select(p => p.Candidate.Slug).ToArray(), dependencies, "ePlugin")
            .Select(group => (IReadOnlyList<ValidatedPackage>)group.Select(slug => packages.First(p => p.Candidate.Slug == slug)).ToArray()).ToArray();
    }

    private Deferral DeferRestarts()
    {
        _restartDeferrals++;
        return new Deferral(() =>
        {
            if (--_restartDeferrals > 0 || !_restartDeferred) return;
            _restartDeferred = false;
            RestartForUpdate();
        });
    }

    private Deferral DeferScans()
    {
        _deferScans = true;
        return new Deferral(() =>
        {
            _deferScans = false;
            var scan = _scanDeferred && !_restartDeferred && !IsRestartPending;
            _scanDeferred = false;
            // A restart scans everything anyway.
            if (scan) _host.ScanFilesystem();
        });
    }

    private void RestartForUpdate()
    {
        if (IsRestartPending) return;
        _ePluginContext?.Logger.Log("Restarting editor to load the updated assembly. If automatic launch fails, reopen the project to resume.");
        IsRestartPending = true;
        ActivationProgress.PrepareForRestart();
        try { RestartRequested?.Invoke(); }
        catch (Exception ex) { _ePluginContext?.Logger.Log("Cannot prepare the editor restart: " + ex.Message); }
        _host.RestartEditor();
    }

    private sealed class Deferral(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose()
        {
            var release = _release;
            _release = null;
            release?.Invoke();
        }
    }
    private void HandleUpdateOutcome(string directory, UpdateOutcome result)
    {
        if (result == UpdateOutcome.AwaitingDecision)
        {
            var journal = UpdateJournal.Load(directory);
            Post(() =>
            {
                if (UpdateDecisionNeeded is null) Applier().Rollback(journal, "No build-failure dialog is available.");
                else UpdateDecisionNeeded(journal);
            });
        }
        if (result == UpdateOutcome.RolledBack)
        {
            try { _rolledBackUpdates.UnionWith(UpdateJournal.Load(directory).Plugins.Select(p => p.Slug)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
        }
        if (result is UpdateOutcome.Completed or UpdateOutcome.RolledBack) RefreshPendingUpdates();
    }
    internal void DecideUpdate(UpdateJournal journal, bool keep)
    {
        using var progress = ActivationProgress.Begin(keep ? "Keeping updated plugins..." : "Restoring plugins...");
        var result = keep ? Applier().Keep(journal) : Applier().Rollback(journal, "User requested rollback.");
        HandleUpdateOutcome(journal.Directory, result);
    }
    /// <summary>Finishes or rolls back the update transactions an editor restart interrupted; called once at startup.</summary>
    internal void ResumeUpdates()
    {
        if (_updateJournals is null || _stateStore is null) return;
        // Cache is needed by recovery before the first scheduled check.
        _updateCache ??= new UpdateStateStore(Path.Combine(UpdateProjectRoot, ".godot/eplugin/update-state.json"));
        _updateCache.Load();
        // Groups of one batch restart the editor once, after the last of them.
        using var restarts = DeferRestarts();
        foreach (var listed in _updateJournals.Read())
        {
            // Runs inside Initialize: one journal that cannot be resumed must not keep the framework from starting.
            try { ResumeUpdate(listed); }
            catch (Exception ex)
            {
                _ePluginContext?.Logger.Error($"Cannot resume update {listed.Directory}; it is tried again at the next start: {ex.Message}");
            }
        }
    }

    private void ResumeUpdate(UpdateJournal listed)
    {
        // An earlier group's commit or rollback may have refreshed this journal's backups since it was listed.
        var journal = UpdateJournal.Load(listed.Directory);
        if (_stateStore!.LastCompletedAttemptId == journal.AttemptId)
        {
            DeleteJournal(journal);
            return;
        }
        var attempts = _stateStore.LocalAttempts.Where(a => a.AttemptId == journal.AttemptId).ToArray();
        if (attempts.Any(a => a.Reason.StartsWith("update_", StringComparison.Ordinal)))
        {
            _ePluginContext?.Logger.Warn($"Update needs manual recovery: {journal.Directory}. Use Retry under Needs attention in the ePlugin Manager.");
            return;
        }
        UpdateOutcome result;
        switch (journal.State)
        {
            case UpdatePhase.Staged:
                if (attempts.Length > 0 && !_stateStore.TryAbandonAttempt(journal.AttemptId)) return;
                DeleteJournal(journal);
                return;
            case UpdatePhase.Verified:
                if (attempts.Length == 0) { _ePluginContext?.Logger.Error($"Update marker is missing: {journal.Directory}; repair manually."); return; }
                result = Applier().Commit(journal);
                break;
            case UpdatePhase.InterimBuilt:
            case UpdatePhase.Reconciling:
            case UpdatePhase.Reconciled:
                result = attempts.Length == 0 ? Applier().Rollback(journal, "Update marker was lost.") : Applier().Resume(journal);
                break;
            case UpdatePhase.AwaitingDecision:
                result = UpdateHost.UiAvailable ? UpdateOutcome.AwaitingDecision : Applier().Rollback(journal, "Cannot show the build-failure dialog.");
                break;
            case UpdatePhase.Swapped:
            case UpdatePhase.RollingBack:
                result = Applier().Rollback(journal, "Interrupted update.");
                break;
            default:
                // Retain diagnostic journals and kept-version backups for at least seven days.
                if (DateTimeOffset.UtcNow - journal.CreatedUtc > TimeSpan.FromDays(7) && attempts.Length == 0) DeleteJournal(journal);
                return;
        }
        HandleUpdateOutcome(journal.Directory, result);
    }

    /// <summary>Deletes a finished journal's folder; one that cannot be deleted yet is tried again at the next start.</summary>
    private void DeleteJournal(UpdateJournal journal)
    {
        try { AddonFiles.DeleteTree(journal.Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ePluginContext?.Logger.Warn($"Cannot delete update journal {journal.Directory} yet; it is tried again at the next start: {ex.Message}");
        }
    }
    /// <summary>The update of <paramref name="slug"/> kept with build errors, if there is one; see <see cref="RollbackKeptUpdate"/>.</summary>
    internal UpdateJournal? KeptUpdate(string slug)
    {
        if (_stateStore is null || _updateJournals is null) return null;
        var attempts = _stateStore.LocalAttempts.Where(a => a.Reason == "update_kept_build_failed").Select(a => a.AttemptId).ToHashSet();
        return _updateJournals.Read().LastOrDefault(j => j.State == UpdatePhase.CommittedWithErrors && attempts.Contains(j.AttemptId) && j.Plugins.Any(p => p.Slug == slug));
    }

    /// <summary>The journal of the update whose failed attempt blocks <paramref name="slug"/>, if there is one.</summary>
    internal UpdateJournal? FailedUpdateJournal(string slug)
    {
        if (_stateStore?.GetLocal(slug) is not { } attempt || _updateJournals is null) return null;
        return _updateJournals.Read().LastOrDefault(j => j.AttemptId == attempt.AttemptId);
    }

    /// <summary>Restores the versions an update kept with build errors replaced; asked for in the ePlugin Manager.</summary>
    internal void RollbackKeptUpdate(UpdateJournal journal)
    {
        using var progress = ActivationProgress.Begin("Restoring plugins...");
        UpdateOutcome result;
        try { result = Applier().RollbackKept(journal); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _ePluginContext?.Logger.Error($"Cannot roll back the kept update: {ex.Message}");
            return;
        }
        HandleUpdateOutcome(journal.Directory, result);
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
        public bool UiAvailable => global.LicenseUiAvailable;
        public string BuildFailurePolicy => ProjectSettings.GetSetting("eplugin/updates/on_build_failure", "ask").AsString();
        public bool RestartAlways => ProjectSettings.GetSetting("eplugin/updates/restart_policy", "auto").AsString() == "always";
        // A GDExtension add-on is never enabled, but Godot always runs it.
        public bool IsEnabled(string slug) => global._host.IsPluginEnabled(slug) || global.IsExtensionAddon(slug);
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
            var findings = global.UpdatePreflightFindings(packages.Select(p => p.Candidate with { NewVersion = UpdateApplier.PackageVersion(p) }).ToArray());
            if (findings.Any(f => f.Severity == FindingSeverity.Error)) throw new InvalidOperationException(string.Join("; ", findings.Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Message)));
            foreach (var warning in findings.Where(f => f.Severity == FindingSeverity.Warning)) Log(warning.Message);
            if (global._toCheckEnable.Count > 0 || global._toCheckDisable.Count > 0) throw new InvalidOperationException("Finish plugin transitions before updating.");
            if (packages.Any(p => p.ContainsCSharp) && global.GetOrCreateContext(global._ePluginContext!).Cli is not ICheckedDotnetCli)
                throw new InvalidOperationException("A working dotnet CLI is required for C# updates.");
        }
        public void PrepareJournal(UpdateJournal journal) => global.PrepareUpdateRecipes(journal);
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin)
        {
            if (!plugin.IsEPlugin || !journal.Recipes.TryGetValue(plugin.Slug, out var recipe)) return;
            plugin.Preserved = InterimBridge.Restore(plugin.Slug, recipe.Old, journal.Backup(plugin.Slug), Path.Combine(global.UpdateProjectRoot, "addons", plugin.Slug));
            journal.Save();
        }
        public void Reconcile(UpdateJournal journal, bool rollback) => global.ReconcileUpdateRecipes(journal, rollback);
        public void RebaseRecipes(UpdateJournal journal) => global.RebaseUpdateRecipes(journal);
        public void SetPlainEnabled(string slug, bool enabled) => global._host.SetPluginEnabled(slug, enabled);
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
        public void Scan()
        {
            if (global._deferScans) { global._scanDeferred = true; return; }
            global._host.ScanFilesystem();
        }
        public BuildOutcome Build()
        {
            ActivationProgress.SetText("Building updated project...");
            return (global.GetOrCreateContext(global._ePluginContext!).Cli as ICheckedDotnetCli)?.TryBuild() ?? new(1, ["Checked dotnet CLI is unavailable."]);
        }
        public bool KeepOnProjectErrors => ProjectSettings.GetSetting("eplugin/updates/keep_on_project_errors", true).AsBool();
        public IReadOnlyList<string> PluginCodeRoots(UpdateJournal journal) => PluginCode.Roots(global.UpdateProjectRoot, journal);
        public BuildOutcome BuildPluginsOnly(PluginsOnlyBuild build)
        {
            ActivationProgress.SetText("Building the plugins without the project's code...");
            return (global.GetOrCreateContext(global._ePluginContext!).Cli as ICheckedDotnetCli)?.TryBuildPluginsOnly(build) ?? new(1, ["Checked dotnet CLI is unavailable."]);
        }
        public void RequestReload(UpdateJournal journal)
        {
            // The full in-place fixture exposed unreliable collectible-assembly reload on Godot 4.7.2.
            // Auto therefore uses the journal-backed restart path rather than a callback that pins old code.
            if (journal.IsActive) { journal.RestartRequired = true; journal.Save(); }
            if (global._restartDeferrals > 0) { global._restartDeferred = true; return; }
            global.RestartForUpdate();
        }
        public bool ReloadExtension(string resourcePath)
        {
            var status = GDExtensionManager.IsExtensionLoaded(resourcePath)
                ? GDExtensionManager.ReloadExtension(resourcePath) : GDExtensionManager.LoadExtension(resourcePath);
            if (status != GDExtensionManager.LoadStatus.Ok) Log($"Godot could not reload {resourcePath}: {status}.");
            return status == GDExtensionManager.LoadStatus.Ok;
        }
        public bool Verify(UpdatePluginJournal plugin)
        {
            // An add-on has no version in its files; what is there to check is that it is still a GDExtension or became a plugin.
            if (PluginKinds.Workflow(plugin) is GDExtensionAddonWorkflow)
                return global.IsExtensionAddon(plugin.Slug) || File.Exists(Path.Combine(ProjectSettings.GlobalizePath(global._host.PluginDirectory(plugin.Slug)), "plugin.cfg"));
            var metadata = EditorPluginExtensions.ReadMetadata($"{global._host.PluginDirectory(plugin.Slug)}/plugin.cfg");
            return (plugin.Slug != "ePlugin" || global.IsValid()) && IsEnabled(plugin.Slug) && metadata?.Version == plugin.NewVersion &&
                global._contexts.FirstOrDefault(c => c.Slug == plugin.Slug)?.State != EEditorPluginState.Error;
        }
        public void Log(string message) => global._ePluginContext?.Logger.Log(message);
    }
}
#endif
