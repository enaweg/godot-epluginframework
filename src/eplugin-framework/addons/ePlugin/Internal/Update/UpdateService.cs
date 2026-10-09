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
        var source = new LocalDirectorySource(index);
        return targets.Select(target => SemVer.TryParse(target.InstalledVersion, out var installed) &&
                source.Versions(target, options).FirstOrDefault() is { } candidate && SemVer.TryParse(candidate.NewVersion, out var local) &&
                local.CompareTo(installed) > 0 ? candidate : null)
            .OfType<UpdateCandidate>().OrderBy(c => c.Slug, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The highest version per plugin; a local package wins over a download of the same version.</summary>
    public static IReadOnlyList<UpdateCandidate> Merge(IEnumerable<UpdateCandidate> remote, IEnumerable<UpdateCandidate> local) =>
        local.Concat(remote).GroupBy(c => c.Slug, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => SemVer.TryParse(c.NewVersion, out var v) ? v : default).First())
            .OrderBy(c => c.Slug, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Every published version of one plugin, newest first, so a specific one can be installed: the versions of its
    /// update_url plus those in the local plugin directories. Unlike checks this does not touch the cache: the list is
    /// only shown, and installing re-validates the chosen package.
    /// </summary>
    public async Task<IReadOnlyList<UpdateCandidate>> ListVersionsAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct,
        LocalPackageIndex? localIndex = null)
    {
        var local = new LocalDirectorySource(localIndex ?? LocalPackageIndex.Empty).Versions(target, options);
        var source = target.UpdateUrl is null ? null : factory.Create(target.UpdateUrl) as IVersionListSource;
        if (source is null && local.Count == 0) throw new NotSupportedException("This update source cannot list versions.");
        IReadOnlyList<UpdateCandidate> remote = [];
        if (source is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds * 2));
            try { remote = await source.ListAsync(target, options, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (local.Count == 0) throw new TimeoutException("Request timed out.");
            }
            // Offline, the local plugin directories still offer their versions.
            catch (Exception ex) when (ex is not OperationCanceledException && local.Count > 0) { }
        }
        // Sorting is stable and local packages come first, so they win over a download of the same version.
        return local.Concat(remote).Where(c => SemVer.TryParse(c.NewVersion, out _))
            .OrderByDescending(c => { SemVer.TryParse(c.NewVersion, out var v); return v; })
            .DistinctBy(c => { SemVer.TryParse(c.NewVersion, out var v); return v.ToString(); }).ToArray();
    }
}
#endif
