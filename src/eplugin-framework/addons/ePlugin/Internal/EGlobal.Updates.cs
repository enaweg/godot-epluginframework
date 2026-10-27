#if TOOLS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private UpdateStateStore? _updateCache;
    private UpdateService? _updateService;
    private readonly UpdateCheckGate _updateChecks = new();
    private IReadOnlyList<UpdateCandidate> _remoteUpdates = [];
    /// <summary>
    /// The highest known version per plugin, from its update_url or from the local directories, without a release
    /// that is installed already.
    /// </summary>
    public IReadOnlyList<UpdateCandidate> PendingUpdates => UpdateService.Merge(UpdateService.WithoutInstalled(_remoteUpdates, _updateCache?.State.Revisions), _localUpdates);
    internal DateTimeOffset? LastUpdateCheck => _updateCache?.State.LastCheckUtc;
    /// <summary>See <see cref="UpdateScheduler.NextCheck"/>.</summary>
    internal DateTimeOffset? NextUpdateCheck => UpdateScheduler.NextCheck(LastUpdateCheck, ScheduledChecksEnabled, CheckIntervalHours);
    private static bool ScheduledChecksEnabled => ProjectSettings.GetSetting("eplugin/updates/check_enabled", true).AsBool();
    private static double CheckIntervalHours => ProjectSettings.GetSetting("eplugin/updates/check_interval_hours", 20).AsDouble();
    internal UpdateCache? UpdateCache => _updateCache?.State;

    private void InitializeUpdates(EPluginPlugin plugin)
    {
        _updateCache ??= new UpdateStateStore(Path.Combine(UpdateProjectRoot, ".godot/eplugin/update-state.json"));
        _updateCache.Load();
        // The update applier changes the cache on the editor thread, so checks write their results there too.
        _updateService = new UpdateService(new UpdateSourceFactory(), new SystemClock(), _updateCache, OnEditorThread);
        InitializeUpdateSites();
        InitializeLocalSources();
        _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, CollectUpdateTargets());
        var check = _updateJournals?.Read().Any(j => j.IsActive) != true;
        var engine = Engine.GetSingleton("Engine");
        if (engine.HasMeta("eplugin_update_check_ran")) check = false;
        else if (check) engine.SetMeta("eplugin_update_check_ran", true);
        // The local index is regenerated on every start, also after an assembly reload dropped it.
        _ = IndexThenCheckAsync(check);
    }

    private async Task IndexThenCheckAsync(bool check)
    {
        // The sizes of the local directories are checked when the update check is due, decided before it records its run.
        var cleanup = check && UpdateScheduler.ShouldCheck(DateTimeOffset.UtcNow, LastUpdateCheck, ScheduledChecksEnabled, CheckIntervalHours);
        await RebuildLocalIndexAsync();
        if (check) await CheckForUpdatesAsync();
        if (cleanup) ScheduleCleanupCheck();
    }

    /// <summary>Re-reads installed versions after an update so neither source keeps offering what is installed now.</summary>
    private void RefreshPendingUpdates()
    {
        var targets = CollectUpdateTargets();
        if (_updateCache is not null) _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, targets);
        _localUpdates = UpdateService.CheckLocal(targets, LocalIndex);
    }

    /// <summary>
    /// Every enabled plugin with a readable plugin.cfg and every GDExtension add-on, with the update site the project sets
    /// for it. Plugins without an update site can still be updated from the local directories; remote checks skip
    /// them.
    /// </summary>
    internal IReadOnlyList<PluginUpdateTarget> CollectUpdateTargets()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        return GetEnabledPluginSlugs().Select(slug =>
        {
            var directory = _host.PluginDirectory(slug);
            var metadata = EditorPluginExtensions.ReadMetadata(directory + "/plugin.cfg");
            return metadata is null ? null : new PluginUpdateTarget(slug, metadata.Name,
                metadata.Version, metadata.UpdateUrl, ProjectSettings.GlobalizePath(directory),
                _stateStore?.IsBlocked(slug) == true, _stateStore?.IsReadOnly != false, _stateStore?.GetShared(slug)?.Version,
                UpdateSiteOf(slug), KnownPluginOf(slug)?.UpdateUrl,
                slug == FrameworkSlug ? PluginKind.Framework : IsManagedPlugin(slug) ? PluginKind.EPlugin : PluginKind.Unknown);
        }).Where(t => t is not null).Cast<PluginUpdateTarget>().Concat(ExtensionAddonSlugs().Select(ExtensionAddonTarget)).ToArray();
    }

    /// <summary>
    /// Checks for updates; <paramref name="force"/> for a manual check. See <see cref="UpdateCheckGate"/> for a check
    /// started while another one runs.
    /// </summary>
    public Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false)
    {
        if (_updateService is null || _updateCache is null || _ePluginContext is null)
            return Task.FromResult(new UpdateCheckResult([], [], DateTimeOffset.UtcNow));
        return _updateChecks.RunAsync(force, RunUpdateCheckAsync, () => new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow));
    }

    private async Task<UpdateCheckResult> RunUpdateCheckAsync(bool force)
    {
        if (_updateService is null || _updateCache is null || _ePluginContext is null)
            return new([], [], DateTimeOffset.UtcNow);
        var ct = _ePluginContext.UpdateLifetime;
        try
        {
            // A manual check also finds ZIP files added to the local directories since they were indexed.
            if (force) await RebuildLocalIndexAsync();
            var targets = CollectUpdateTargets();
            var enabled = ScheduledChecksEnabled;
            var interval = CheckIntervalHours;
            _localUpdates = UpdateService.CheckLocal(targets, LocalIndex);
            // Scheduled checks are off: nothing is fetched or logged, but the known updates stay what callers see.
            if (!force && !enabled) return new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
            if (!force && !UpdateScheduler.ShouldCheck(DateTimeOffset.UtcNow, LastUpdateCheck, enabled, interval))
            {
                _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, targets);
                var cached = new UpdateCheckResult(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
                PrintUpdates(cached, false);
                return cached;
            }
            var service = _updateService;
            var result = await Task.Run(() => service.CheckAsync(targets, new(), ct), ct).ConfigureAwait(false);
            await OnEditorThread(() => { _remoteUpdates = result.Updates; result = result with { Updates = PendingUpdates }; PrintUpdates(result, force); }, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { return new([], [], DateTimeOffset.UtcNow); }
        catch (Exception ex)
        {
            await OnEditorThread(() => _ePluginContext?.Logger.Warn($"Update check failed: {ex.Message}"), ct).ConfigureAwait(false);
            return new([], [new("ePlugin", ex.Message)], DateTimeOffset.UtcNow);
        }
    }

    /// <summary>The versions of an enabled plugin in the local directories, newest first. Never touches the disk.</summary>
    internal IReadOnlyList<UpdateCandidate> ListLocalVersions(PluginUpdateTarget target) =>
        LocalDirectorySource.Versions(LocalIndex, target);

    /// <summary>
    /// All versions published at an enabled plugin's update_url, newest first. A newer version found this way becomes
    /// a pending update, as if a check had found it.
    /// </summary>
    internal async Task<IReadOnlyList<UpdateCandidate>> ListRemoteVersionsAsync(PluginUpdateTarget target, CancellationToken ct)
    {
        var service = _updateService ?? throw new InvalidOperationException("Update system is not initialized.");
        var versions = await Task.Run(() => service.ListVersionsAsync(target, new(), ct), ct);
        await OnEditorThread(() => { if (_updateCache is not null) _remoteUpdates = UpdateScheduler.CurrentCached(_updateCache.State, CollectUpdateTargets()); }, ct);
        return versions;
    }

    internal static Task OnEditorThread(Action action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        Post(() =>
        {
            try { if (!ct.IsCancellationRequested) { action(); completion.TrySetResult(); } }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { registration.Dispose(); }
        });
        return completion.Task;
    }

    // Work for the editor thread, run by EPluginPlugin._Process. Unlike Callable.CallDeferred it hands Godot no delegate:
    // a C# assembly reload discards this queue with the rest of the old assembly's static state, so nothing queued by
    // old code runs after the reload, and background threads never call into Godot.
    private static readonly ConcurrentQueue<Action> Posted = new();

    /// <summary>Runs <paramref name="action"/> on the editor thread in a later frame. Callable from any thread.</summary>
    internal static void Post(Action action) => Posted.Enqueue(action);

    /// <summary>Runs the work posted so far; called once per frame on the editor thread.</summary>
    internal static void RunPosted()
    {
        // Work posted while running waits for the next frame, so a chain of posts cannot keep the frame from ending.
        for (var count = Posted.Count; count > 0 && Posted.TryDequeue(out var action); count--)
        {
            try { action(); }
            catch (Exception ex) { GD.PushError($"ePlugin editor-thread work failed: {ex}"); }
        }
    }

    private void PrintUpdates(UpdateCheckResult result, bool manual)
    {
        foreach (var failure in result.Failures) _ePluginContext?.Logger.Warn($"Update check for '{failure.Slug}' failed: {failure.Message}");
        foreach (var fallback in result.Fallbacks ?? [])
            _ePluginContext?.Logger.Warn($"An update site of '{fallback.Slug}' did not work, so the next one was checked: {fallback.Message}");
        if (result.Updates.Count == 0)
        {
            if (manual && result.Failures.Count == 0) _ePluginContext?.Logger.Log("All plugins are up to date.");
            return;
        }
        _ePluginContext?.Logger.Log($"Plugin updates available ({result.Updates.Count}):");
        foreach (var update in result.Updates)
        {
            _ePluginContext?.Logger.Log($"  {update.Slug} {update.InstalledVersion} -> {update.NewVersion}  {update.ReleaseUrl ?? update.Origin}");
            if (_updateCache?.State.LastFailure(update) is not null)
                _ePluginContext?.Logger.Warn($"  {update.Slug} {update.NewVersion} failed previously; repair the cause before retrying.");
        }
        _ePluginContext?.Logger.Log("Open the ePlugin Manager (Project > Tools > ePlugin Manager... or the ePlugin toolbar button) to install them.");
    }
}

/// <summary>
/// Runs one update check at a time. A scheduled check started while one runs returns the known updates at once. A manual
/// check waits for the running one: it returns that check's result when it was manual too, and otherwise checks itself
/// afterwards, as only a manual check indexes the local directories again and always asks the update sites.
/// </summary>
internal sealed class UpdateCheckGate
{
    private readonly object _gate = new();
    private Task<UpdateCheckResult>? _running;
    private bool _runningForced;

    /// <param name="force">A manual check.</param>
    /// <param name="check">Runs the check; called for one check at a time.</param>
    /// <param name="known">The updates known now, for a scheduled check that is not run.</param>
    public async Task<UpdateCheckResult> RunAsync(bool force, Func<bool, Task<UpdateCheckResult>> check, Func<UpdateCheckResult> known)
    {
        while (true)
        {
            var mine = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateCheckResult>? running;
            bool runningForced;
            // The running check may end on a thread-pool thread while a waiting one is on the editor thread.
            lock (_gate)
            {
                running = _running;
                runningForced = _runningForced;
                if (running is null)
                {
                    _running = mine.Task;
                    _runningForced = force;
                }
            }

            if (running is not null)
            {
                if (!force) return known();
                // Back on the caller's context (the editor thread's), since the check run next calls into Godot.
                var result = await running;
                if (runningForced) return result;
                continue;
            }

            try
            {
                var result = await check(force).ConfigureAwait(false);
                End(mine.Task);
                mine.SetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                // A waiting manual check reports the failure too, rather than throwing it a second time.
                End(mine.Task);
                mine.SetResult(new([], [new("ePlugin", ex.Message)], DateTimeOffset.UtcNow));
                throw;
            }
        }
    }

    // Ended before its waiters continue, so a waiter that checks itself afterwards finds no check running.
    private void End(Task<UpdateCheckResult> check)
    {
        lock (_gate)
        {
            if (_running == check) _running = null;
        }
    }
}
#endif
