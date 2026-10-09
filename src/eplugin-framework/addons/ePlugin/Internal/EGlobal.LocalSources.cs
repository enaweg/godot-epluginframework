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

/// <summary>
/// Local plugin directories: per-user folders whose ZIP files (also in subfolders) are offered as plugin versions. The
/// index lives in memory only and is rebuilt in the background on every editor start.
/// </summary>
internal sealed partial class EGlobal
{
    private LocalSourceSettings? _localSources;
    private CancellationTokenSource? _indexing;
    private IReadOnlyList<UpdateCandidate> _localUpdates = [];
    internal LocalPackageIndex LocalIndex { get; private set; } = LocalPackageIndex.Empty;
    internal bool IsIndexingLocalSources => _indexing is not null;
    internal IReadOnlyList<string> LocalDirectories => _localSources?.Directories ?? [];
    /// <summary>Raised on the editor thread when indexing starts or a new index is in place.</summary>
    internal event Action? LocalIndexChanged;

    private void InitializeLocalSources()
    {
        // The editor's configuration folder belongs to the user and is shared by all projects and Godot versions.
        var config = EditorInterface.Singleton.GetEditorPaths().GetConfigDir();
        _localSources = new LocalSourceSettings(Path.Combine(config, "eplugin", "local-sources.json"));
        _localSources.Load();
    }

    /// <summary>Adds a directory to the user's list and indexes again; false when it is already listed.</summary>
    internal bool AddLocalDirectory(string directory)
    {
        if (_localSources is null || !_localSources.Add(directory)) return false;
        _ = RebuildLocalIndexAsync();
        return true;
    }

    internal bool RemoveLocalDirectory(string directory)
    {
        if (_localSources is null || !_localSources.Remove(directory)) return false;
        _ = RebuildLocalIndexAsync();
        return true;
    }

    /// <summary>
    /// Indexes the local plugin directories off the editor thread, then recomputes the local updates. A newer call
    /// supersedes a running one. Completes on the editor thread's context when awaited from it.
    /// </summary>
    internal async Task RebuildLocalIndexAsync()
    {
        if (_localSources is null || _ePluginContext is null) return;
        _indexing?.Cancel();
        var run = _indexing = new CancellationTokenSource();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Token, _ePluginContext.UpdateLifetime);
        var directories = _localSources.Directories.ToArray();
        LocalIndexChanged?.Invoke();
        try
        {
            var index = await Task.Run(() => LocalPackageIndexer.Build(directories, cancel.Token), cancel.Token).ConfigureAwait(false);
            await OnEditorThread(() => { if (_indexing == run) ApplyLocalIndex(index); }, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await OnEditorThread(() =>
            {
                _ePluginContext?.Logger.Warn($"Indexing local plugin directories failed: {ex.Message}");
                if (_indexing == run) { _indexing = null; LocalIndexChanged?.Invoke(); }
            }, _ePluginContext?.UpdateLifetime ?? CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void ApplyLocalIndex(LocalPackageIndex index)
    {
        _indexing = null;
        LocalIndex = index;
        foreach (var failure in index.Failures) _ePluginContext?.Logger.Warn($"Local plugin package skipped: {failure.Path}: {failure.Message}");
        RefreshLocalUpdates();
        LocalIndexChanged?.Invoke();
    }

    private void RefreshLocalUpdates() => _localUpdates = UpdateService.CheckLocal(CollectUpdateTargets(), LocalIndex, new(AllowPrerelease));
}
#endif
