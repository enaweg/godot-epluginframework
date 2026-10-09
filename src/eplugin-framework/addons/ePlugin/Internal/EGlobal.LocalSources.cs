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
/// index is rebuilt in the background on every editor start and assembly reload; a per-user cache of what each ZIP file
/// held keeps that from opening unchanged archives again.
/// </summary>
internal sealed partial class EGlobal
{
    private LocalSourceSettings? _localSources;
    private LocalIndexCache? _localIndexCache;
    private CancellationTokenSource? _indexing;
    private IReadOnlyList<UpdateCandidate> _localUpdates = [];
    internal LocalPackageIndex LocalIndex { get; private set; } = LocalPackageIndex.Empty;
    internal bool IsIndexingLocalSources => _indexing is not null;
    internal IReadOnlyList<string> LocalDirectories => _localSources?.Directories ?? [];
    /// <summary>Why the list of local plugin directories cannot be changed; null when it can.</summary>
    internal string? LocalDirectoriesProblem => _localSources?.Problem;
    /// <summary>Raised on the editor thread when indexing starts or a new index is in place.</summary>
    internal event Action? LocalIndexChanged;

    private void InitializeLocalSources()
    {
        // The editor's configuration folder belongs to the user and is shared by all projects and Godot versions.
        var config = EditorInterface.Singleton.GetEditorPaths().GetConfigDir();
        _localSources = new LocalSourceSettings(Path.Combine(config, "eplugin", "local-sources.json"));
        _localIndexCache = new LocalIndexCache(Path.Combine(config, "eplugin", "local-index.json"));
        _localSources.Load();
        if (_localSources.Problem is { } problem) _ePluginContext?.Logger.Warn($"Local plugin directories: {problem}");
    }

    /// <summary>Adds a directory to the user's list and indexes again; false when it is already listed.</summary>
    /// <exception cref="InvalidOperationException">The list is read-only, see <see cref="LocalDirectoriesProblem"/>.</exception>
    internal bool AddLocalDirectory(string directory) => ChangeLocalDirectories(settings => settings.Add(directory));

    /// <inheritdoc cref="AddLocalDirectory"/>
    internal bool RemoveLocalDirectory(string directory) => ChangeLocalDirectories(settings => settings.Remove(directory));

    /// <summary>
    /// Reads the list again, as another open editor may have changed it, and indexes again when it differs from the
    /// one indexed.
    /// </summary>
    internal void ReloadLocalDirectories() => ChangeLocalDirectories(settings => { settings.Load(); return false; });

    private bool ChangeLocalDirectories(Func<LocalSourceSettings, bool> change)
    {
        if (_localSources is null) return false;
        var before = _localSources.Directories.ToArray();
        try { return change(_localSources); }
        finally
        {
            // Reading the file before a change also picks up what other editors changed.
            if (!before.SequenceEqual(_localSources.Directories, PackageFiles.PathComparer)) _ = RebuildLocalIndexAsync();
        }
    }

    /// <summary>
    /// Indexes the local plugin directories off the editor thread, then recomputes the local updates. A newer call
    /// supersedes a running one. Completes on the editor thread's context when awaited from it.
    /// </summary>
    internal async Task RebuildLocalIndexAsync()
    {
        if (_localSources is null || _localIndexCache is null || _ePluginContext is null) return;
        var lifetime = _ePluginContext.UpdateLifetime;
        _indexing?.Cancel();
        var run = _indexing = new CancellationTokenSource();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Token, lifetime);
        var directories = _localSources.Directories.ToArray();
        var cache = _localIndexCache;
        LocalIndexChanged?.Invoke();
        try
        {
            var index = await Task.Run(() => LocalPackageIndexer.Build(directories, cache, cancel.Token), cancel.Token).ConfigureAwait(false);
            await OnEditorThread(() => { if (_indexing == run) ApplyLocalIndex(index); }, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try
            {
                await OnEditorThread(() =>
                {
                    _ePluginContext?.Logger.Warn($"Indexing local plugin directories failed: {ex.Message}");
                    if (_indexing == run) { _indexing = null; LocalIndexChanged?.Invoke(); }
                }, lifetime).ConfigureAwait(false);
            }
            // The plugin is being unloaded; nothing is left to report to.
            catch (OperationCanceledException) { }
        }
    }

    private void ApplyLocalIndex(LocalPackageIndex index)
    {
        _indexing = null;
        LocalIndex = index;
        // Failures remembered from an earlier indexing were logged then; the dialog still lists all of them.
        foreach (var failure in index.Failures.Where(f => !f.Cached)) _ePluginContext?.Logger.Warn($"Local plugin package skipped: {failure.Path}: {failure.Message}");
        RefreshLocalUpdates();
        LocalIndexChanged?.Invoke();
    }

    private void RefreshLocalUpdates() => _localUpdates = UpdateService.CheckLocal(CollectUpdateTargets(), LocalIndex, new(AllowPrerelease));
}
#endif
