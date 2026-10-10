#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private UpdateStateStore? _updateCache;
    private UpdateService? _updateService;
    private bool _checkingUpdates;
    private IReadOnlyList<UpdateCandidate> _remoteUpdates = [];
    /// <summary>The highest known version per plugin, from its update_url or from the local plugin directories.</summary>
    public IReadOnlyList<UpdateCandidate> PendingUpdates => UpdateService.Merge(_remoteUpdates, _localUpdates);
    internal DateTimeOffset? LastUpdateCheck => _updateCache?.State.LastCheckUtc;
    internal UpdateCache? UpdateCache => _updateCache?.State;
    private static bool AllowPrerelease => ProjectSettings.GetSetting("eplugin/updates/allow_prerelease", false).AsBool();

    private void InitializeUpdates(EPluginPlugin plugin)
    {
        _updateCache ??= new UpdateStateStore(Path.Combine(ProjectSettings.GlobalizePath("res://.godot/eplugin"), "update-state.json"));
        _updateCache.Load();
        _updateService = new UpdateService(new UpdateSourceFactory(), new SystemClock(), _updateCache);
        InitializeUpdateSites();
        InitializeLocalSources();
        _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, CollectUpdateTargets(), AllowPrerelease);
        var check = _updateJournals?.Read().Any(j => j.IsActive) != true;
        var engine = Engine.GetSingleton("Engine");
        if (engine.HasMeta("eplugin_update_check_ran")) check = false;
        else if (check) engine.SetMeta("eplugin_update_check_ran", true);
        // The local index is regenerated on every start, also after an assembly reload dropped it.
        _ = IndexThenCheckAsync(check);
    }

    private async Task IndexThenCheckAsync(bool check)
    {
        await RebuildLocalIndexAsync();
        if (check) await CheckForUpdatesAsync();
    }

    /// <summary>Re-reads installed versions after an update so neither source keeps offering what is installed now.</summary>
    private void RefreshPendingUpdates()
    {
        var targets = CollectUpdateTargets();
        if (_updateCache is not null) _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, targets, AllowPrerelease);
        _localUpdates = UpdateService.CheckLocal(targets, LocalIndex, new(AllowPrerelease));
    }

    /// <summary>
    /// Every enabled plugin with a readable plugin.cfg, with the update site the project sets for it. Plugins without
    /// an update site can still be updated from the local plugin directories; remote checks skip them.
    /// </summary>
    internal IReadOnlyList<PluginUpdateTarget> CollectUpdateTargets()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        return GetEnabledPluginSlugs().Select(slug =>
        {
            var directory = $"res://addons/{slug}";
            var metadata = EditorPluginExtensions.ReadMetadata(directory + "/plugin.cfg");
            return metadata is null ? null : new PluginUpdateTarget(slug, metadata.Name,
                metadata.Version, metadata.UpdateUrl, ProjectSettings.GlobalizePath(directory),
                _stateStore?.IsBlocked(slug) == true, _stateStore?.IsReadOnly != false, _stateStore?.GetShared(slug)?.Version,
                UpdateSiteOf(slug));
        }).Where(t => t is not null).Cast<PluginUpdateTarget>().ToArray();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false)
    {
        if (_updateService is null || _updateCache is null || _ePluginContext is null)
            return new([], [], DateTimeOffset.UtcNow);
        if (_checkingUpdates) return new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
        _checkingUpdates = true;
        var ct = _ePluginContext.UpdateLifetime;
        try
        {
            // A manual check also finds ZIP files added to the local plugin directories since they were indexed.
            if (force) await RebuildLocalIndexAsync();
            var targets = CollectUpdateTargets();
            var allow = AllowPrerelease;
            var enabled = ProjectSettings.GetSetting("eplugin/updates/check_enabled", true).AsBool();
            var interval = ProjectSettings.GetSetting("eplugin/updates/check_interval_hours", 20).AsDouble();
            _localUpdates = UpdateService.CheckLocal(targets, LocalIndex, new(allow));
            // Scheduled checks are off: nothing is fetched or logged, but the known updates stay what callers see.
            if (!force && !enabled) return new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
            if (!force && !UpdateScheduler.ShouldCheck(DateTimeOffset.UtcNow, LastUpdateCheck, enabled, interval))
            {
                _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, targets, allow);
                var cached = new UpdateCheckResult(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
                PrintUpdates(cached, false);
                return cached;
            }
            var service = _updateService;
            var result = await Task.Run(() => service.CheckAsync(targets, new(allow), ct), ct).ConfigureAwait(false);
            await OnEditorThread(() => { _remoteUpdates = result.Updates; result = result with { Updates = PendingUpdates }; PrintUpdates(result, force); }, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { return new([], [], DateTimeOffset.UtcNow); }
        catch (Exception ex)
        {
            await OnEditorThread(() => _ePluginContext?.Logger.Warn($"Update check failed: {ex.Message}"), ct).ConfigureAwait(false);
            return new([], [new("ePlugin", ex.Message)], DateTimeOffset.UtcNow);
        }
        finally { _checkingUpdates = false; }
    }

    /// <summary>The versions of an enabled plugin in the local plugin directories, newest first. Never touches the disk.</summary>
    internal IReadOnlyList<UpdateCandidate> ListLocalVersions(PluginUpdateTarget target) =>
        LocalDirectorySource.Versions(LocalIndex, target, new(AllowPrerelease));

    /// <summary>
    /// All versions published at an enabled plugin's update_url, newest first. A newer version found this way becomes
    /// a pending update, as if a check had found it.
    /// </summary>
    internal async Task<IReadOnlyList<UpdateCandidate>> ListRemoteVersionsAsync(PluginUpdateTarget target, CancellationToken ct)
    {
        var service = _updateService ?? throw new InvalidOperationException("Update system is not initialized.");
        var allow = AllowPrerelease;
        var versions = await Task.Run(() => service.ListVersionsAsync(target, new(allow), ct), ct);
        await OnEditorThread(() => { if (_updateCache is not null) _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, CollectUpdateTargets(), allow); }, ct);
        return versions;
    }

    internal static Task OnEditorThread(Action action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        Callable.From(() =>
        {
            try { if (!ct.IsCancellationRequested) { action(); completion.TrySetResult(); } }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { registration.Dispose(); }
        }).CallDeferred();
        return completion.Task;
    }

    private void PrintUpdates(UpdateCheckResult result, bool manual)
    {
        foreach (var failure in result.Failures) _ePluginContext?.Logger.Warn($"Update check for '{failure.Slug}' failed: {failure.Message}");
        foreach (var fallback in result.Fallbacks ?? [])
            _ePluginContext?.Logger.Warn($"The project's update site for '{fallback.Slug}' did not work, so its plugin.cfg update_url was checked: {fallback.Message}");
        if (result.Updates.Count == 0)
        {
            if (manual && result.Failures.Count == 0) _ePluginContext?.Logger.Log("All plugins are up to date.");
            return;
        }
        _ePluginContext?.Logger.Log($"Plugin updates available ({result.Updates.Count}):");
        foreach (var update in result.Updates)
        {
            _ePluginContext?.Logger.Log($"  {update.Slug} {update.InstalledVersion} -> {update.NewVersion}  {update.ReleaseUrl ?? update.Origin}");
            if (_updateCache?.State.FailedUpdates.GetValueOrDefault(update.Slug)?.Any(f => f.Version == update.NewVersion) == true)
                _ePluginContext?.Logger.Warn($"  {update.Slug} {update.NewVersion} failed previously; repair the cause before retrying.");
        }
        _ePluginContext?.Logger.Log("Open the ePlugin Manager (Project > Tools > ePlugin Manager... or the ePlugin toolbar button) to install them.");
    }
}
#endif
