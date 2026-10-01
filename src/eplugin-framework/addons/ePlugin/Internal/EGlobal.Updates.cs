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
    public IReadOnlyList<UpdateCandidate> PendingUpdates { get; private set; } = [];
    internal DateTimeOffset? LastUpdateCheck => _updateCache?.State.LastCheckUtc;
    internal UpdateCache? UpdateCache => _updateCache?.State;

    private void InitializeUpdates(EPluginPlugin plugin)
    {
        _updateCache ??= new UpdateStateStore(Path.Combine(ProjectSettings.GlobalizePath("res://.godot/eplugin"), "update-state.json"));
        _updateCache.Load();
        _updateService = new UpdateService(new UpdateSourceFactory(), new SystemClock(), _updateCache);
        if (_updateJournals?.Read().Any(j => j.IsActive) == true) return;
        var engine = Engine.GetSingleton("Engine");
        if (engine.HasMeta("eplugin_update_check_ran")) return;
        engine.SetMeta("eplugin_update_check_ran", true);
        _ = CheckForUpdatesAsync();
    }

    internal IReadOnlyList<PluginUpdateTarget> CollectUpdateTargets()
    {
        RefreshPlainPlugins();
        return GetEnabledPluginSlugs().Select(slug =>
        {
            var directory = $"res://addons/{slug}";
            var metadata = EditorPluginExtensions.ReadMetadata(directory + "/plugin.cfg");
            return metadata?.UpdateUrl is null ? null : new PluginUpdateTarget(slug, metadata.Name,
                metadata.Version, metadata.UpdateUrl, ProjectSettings.GlobalizePath(directory),
                _stateStore?.IsBlocked(slug) == true, _stateStore?.IsReadOnly != false, _stateStore?.GetShared(slug)?.Version);
        }).Where(t => t is not null).Cast<PluginUpdateTarget>().ToArray();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false)
    {
        if (_updateService is null || _updateCache is null || _ePluginContext is null)
            return new([], [], DateTimeOffset.UtcNow);
        if (_checkingUpdates) return new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
        var targets = CollectUpdateTargets();
        var allow = ProjectSettings.GetSetting("eplugin/updates/allow_prerelease", false).AsBool();
        var enabled = ProjectSettings.GetSetting("eplugin/updates/check_enabled", true).AsBool();
        var interval = ProjectSettings.GetSetting("eplugin/updates/check_interval_hours", 20).AsDouble();
        if (!force && !enabled) return new([], [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
        if (!force && !UpdateScheduler.ShouldCheck(DateTimeOffset.UtcNow, LastUpdateCheck, enabled, interval))
        {
            PendingUpdates = UpdateScheduler.CurrentCached(_updateCache.State, targets, allow);
            PrintUpdates(new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow), false);
            return new(PendingUpdates, [], LastUpdateCheck ?? DateTimeOffset.UtcNow);
        }
        _checkingUpdates = true;
        var ct = _ePluginContext.UpdateLifetime;
        try
        {
            var service = _updateService;
            var result = await Task.Run(() => service.CheckAsync(targets, new(allow), ct), ct).ConfigureAwait(false);
            await OnEditorThread(() => { PendingUpdates = result.Updates; PrintUpdates(result, force); _checkingUpdates = false; }, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { return new([], [], DateTimeOffset.UtcNow); }
        catch (Exception ex)
        {
            await OnEditorThread(() => { _checkingUpdates = false; _ePluginContext?.Logger.Warn($"Update check failed: {ex.Message}"); }, ct).ConfigureAwait(false);
            return new([], [new("ePlugin", ex.Message)], DateTimeOffset.UtcNow);
        }
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
        if (result.Updates.Count == 0)
        {
            if (manual && result.Failures.Count == 0) _ePluginContext?.Logger.Log("All plugins are up to date.");
            return;
        }
        _ePluginContext?.Logger.Log($"Plugin updates available ({result.Updates.Count}):");
        foreach (var update in result.Updates)
        {
            _ePluginContext?.Logger.Log($"  {update.Slug} {update.InstalledVersion} -> {update.NewVersion}  {update.ReleaseUrl ?? update.SourceUrl}");
            if (_updateCache?.State.FailedUpdates.GetValueOrDefault(update.Slug)?.Any(f => f.Version == update.NewVersion) == true)
                _ePluginContext?.Logger.Warn($"  {update.Slug} {update.NewVersion} failed previously; repair the cause before retrying.");
        }
        _ePluginContext?.Logger.Log("Use Project > Tools > Update ePlugin addons... to install them.");
    }
}
#endif
