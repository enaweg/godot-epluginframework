#if TOOLS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class UpdateService(IUpdateSourceFactory factory, IClock clock, IUpdateStateStore store)
{
    /// <summary>Checks the update_url of every target that has one and caches the result.</summary>
    public async Task<UpdateCheckResult> CheckAsync(IReadOnlyList<PluginUpdateTarget> targets, UpdateCheckOptions options,
        CancellationToken ct)
    {
        targets = targets.Where(t => t.UpdateUrl is not null).ToArray();
        var updates = new ConcurrentBag<UpdateCandidate>();
        var failures = new ConcurrentBag<UpdateCheckFailure>();
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(targets.Select(async target =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!SemVer.TryParse(target.InstalledVersion, out var installed))
                    throw new InvalidOperationException($"Installed version '{target.InstalledVersion}' cannot be compared.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                var candidate = await factory.Create(target.UpdateUrl!).CheckAsync(target, options, timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                if (candidate is not null && SemVer.TryParse(candidate.NewVersion, out var remote) &&
                    remote.CompareTo(installed) > 0 && (options.AllowPrerelease || remote.Prerelease is null)) updates.Add(candidate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(target.Slug, ex is OperationCanceledException ? "Request timed out." : ex.Message)); }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
        var result = new UpdateCheckResult(updates.OrderBy(x => x.Slug, StringComparer.Ordinal).ToArray(),
            failures.OrderBy(x => x.Slug, StringComparer.Ordinal).ToArray(), clock.UtcNow);
        if (targets.Count == 0 || failures.Count < targets.Count)
        {
            store.State.LastCheckUtc = result.CheckedAtUtc;
            store.State.Results = result.Updates.ToList();
            store.Save();
        }
        return result;
    }

    /// <summary>
    /// The newest version in the local plugin directories that is newer than the installed one, per target. The index
    /// is in memory and rebuilt every editor start, so this is never cached.
    /// </summary>
    public static IReadOnlyList<UpdateCandidate> CheckLocal(IReadOnlyList<PluginUpdateTarget> targets, LocalPackageIndex index, UpdateCheckOptions options)
    {
        return targets.Select(target => SemVer.TryParse(target.InstalledVersion, out var installed) &&
                LocalDirectorySource.Versions(index, target, options).FirstOrDefault() is { } candidate && SemVer.TryParse(candidate.NewVersion, out var local) &&
                local.CompareTo(installed) > 0 ? candidate : null)
            .OfType<UpdateCandidate>().OrderBy(c => c.Slug, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The highest version per plugin; a local package wins over a download of the same version.</summary>
    public static IReadOnlyList<UpdateCandidate> Merge(IEnumerable<UpdateCandidate> remote, IEnumerable<UpdateCandidate> local) =>
        local.Concat(remote).GroupBy(c => c.Slug, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => SemVer.TryParse(c.NewVersion, out var v) ? v : default).First())
            .OrderBy(c => c.Slug, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Every version published at a plugin's update_url, newest first, so a specific one can be installed. Installing
    /// re-validates the chosen package. A listed version newer than the installed one and than the cached update is
    /// cached like a check found it, so the plugin shows the update without another check.
    /// Combine it with the local plugin directories' versions using <see cref="MergeVersions"/>.
    /// </summary>
    public async Task<IReadOnlyList<UpdateCandidate>> ListVersionsAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (target.UpdateUrl is null || factory.Create(target.UpdateUrl) is not IVersionListSource source)
            throw new NotSupportedException("This update source cannot list versions.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds * 2));
        IReadOnlyList<UpdateCandidate> versions;
        try { versions = MergeVersions([], await source.ListAsync(target, options, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Request timed out."); }
        RecordListedUpdate(target, versions, options);
        return versions;
    }

    /// <summary>
    /// Caches the newest listed version as the plugin's update. A cached update that is at least as new stays, e.g. the
    /// branch head of a pinned git source, which has no versions to list. The last check time is not changed, since
    /// the other plugins were not checked.
    /// </summary>
    private void RecordListedUpdate(PluginUpdateTarget target, IReadOnlyList<UpdateCandidate> versions, UpdateCheckOptions options)
    {
        if (!SemVer.TryParse(target.InstalledVersion, out var installed)) return;
        var newest = versions.FirstOrDefault();
        if (newest is null || !SemVer.TryParse(newest.NewVersion, out var listed) || listed.CompareTo(installed) <= 0 ||
            !options.AllowPrerelease && listed.Prerelease is not null) return;
        var cached = store.State.Results.FirstOrDefault(c => c.Slug == target.Slug && c.SourceUrl == target.UpdateUrl);
        if (cached is not null && SemVer.TryParse(cached.NewVersion, out var known) && known.CompareTo(listed) >= 0) return;
        // The list was read from the target's update_url, which is what the cache entry is matched against.
        var update = newest with { InstalledVersion = target.InstalledVersion, SourceUrl = target.UpdateUrl };
        store.State.Results = store.State.Results.Where(c => c.Slug != target.Slug).Append(update).ToList();
        store.Save();
    }

    /// <summary>One candidate per version, newest first; a local package wins over a download of the same version.</summary>
    public static IReadOnlyList<UpdateCandidate> MergeVersions(IEnumerable<UpdateCandidate> local, IEnumerable<UpdateCandidate> remote) =>
        local.Concat(remote)
            .Select(c => (Candidate: c, Valid: SemVer.TryParse(c.NewVersion, out var v), Version: v))
            .Where(c => c.Valid)
            // Sorting is stable and local packages come first, so they win a tie.
            .OrderByDescending(c => c.Version).DistinctBy(c => c.Version.ToString())
            .Select(c => c.Candidate).ToArray();
}
#endif
