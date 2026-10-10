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
    /// <summary>
    /// Checks the update site of every target that has one and caches the result. A project update site is tried first;
    /// when it is unsupported or fails, the plugin.cfg update_url is checked instead.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(IReadOnlyList<PluginUpdateTarget> targets, UpdateCheckOptions options,
        CancellationToken ct)
    {
        targets = targets.Where(t => t.UpdateUrls.Count > 0).ToArray();
        var updates = new ConcurrentBag<UpdateCandidate>();
        var failures = new ConcurrentBag<UpdateCheckFailure>();
        var fallbacks = new ConcurrentBag<UpdateCheckFailure>();
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(targets.Select(async target =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!SemVer.TryParse(target.InstalledVersion, out var installed))
                    throw new InvalidOperationException($"Installed version '{target.InstalledVersion}' cannot be compared.");
                var (candidate, _, fallback) = await FirstWorkingSiteAsync(target, TimeSpan.FromSeconds(options.TimeoutSeconds),
                    (source, site, token) => source.CheckAsync(site, options, token), ct).ConfigureAwait(false);
                if (fallback is not null) fallbacks.Add(new(target.Slug, fallback));
                if (candidate is not null && SemVer.TryParse(candidate.NewVersion, out var remote) &&
                    remote.CompareTo(installed) > 0 && (options.AllowPrerelease || remote.Prerelease is null)) updates.Add(candidate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(target.Slug, ex is OperationCanceledException ? "Request timed out." : ex.Message)); }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
        var result = new UpdateCheckResult(updates.OrderBy(x => x.Slug, StringComparer.Ordinal).ToArray(),
            failures.OrderBy(x => x.Slug, StringComparer.Ordinal).ToArray(), clock.UtcNow,
            fallbacks.OrderBy(x => x.Slug, StringComparer.Ordinal).ToArray());
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
        if (target.UpdateUrls.Count == 0) throw new NotSupportedException("This update source cannot list versions.");
        var (listed, url, _) = await FirstWorkingSiteAsync(target, TimeSpan.FromSeconds(options.TimeoutSeconds * 2),
            (source, site, token) => source is IVersionListSource list ? list.ListAsync(site, options, token)
                : throw new NotSupportedException("This update source cannot list versions."), ct).ConfigureAwait(false);
        var versions = MergeVersions([], listed);
        RecordListedUpdate(target, url, versions, options);
        return versions;
    }

    /// <summary>
    /// Runs <paramref name="run"/> against the target's update sites in order, each with its own timeout: the project's
    /// update site, then the plugin.cfg update_url when the first is unsupported or fails. Returns the first result, the
    /// site it came from, and why the sites before it were skipped. The source sees a target whose UpdateUrl is the site
    /// it reads, so its candidates name that site as their SourceUrl.
    /// </summary>
    /// <exception cref="Exception">
    /// Every site failed. With a single site its own exception, a timeout as <see cref="TimeoutException"/>; otherwise one
    /// naming each failure.
    /// </exception>
    private async Task<(T Result, string Url, string? Fallback)> FirstWorkingSiteAsync<T>(PluginUpdateTarget target,
        TimeSpan timeoutAfter, Func<IUpdateSource, PluginUpdateTarget, CancellationToken, Task<T>> run, CancellationToken ct)
    {
        var urls = target.UpdateUrls;
        var errors = new List<string>();
        foreach (var url in urls)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutAfter);
            try
            {
                var site = target with { UpdateUrl = url, OverrideUrl = null };
                var result = await run(factory.Create(url), site, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                return (result, url, errors.Count == 0 ? null : $"{string.Join("; ", errors)}; used {url} instead");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (urls.Count == 1)
            {
                if (ex is OperationCanceledException) throw new TimeoutException("Request timed out.");
                throw;
            }
            catch (Exception ex) { errors.Add($"{url}: {(ex is OperationCanceledException ? "Request timed out." : ex.Message)}"); }
        }
        throw new InvalidOperationException(string.Join("; ", errors));
    }

    /// <summary>
    /// Caches the newest listed version as the plugin's update. A cached update that is at least as new stays, e.g. the
    /// branch head of a pinned git source, which has no versions to list. The last check time is not changed, since
    /// the other plugins were not checked.
    /// </summary>
    private void RecordListedUpdate(PluginUpdateTarget target, string url, IReadOnlyList<UpdateCandidate> versions, UpdateCheckOptions options)
    {
        if (!SemVer.TryParse(target.InstalledVersion, out var installed)) return;
        var newest = versions.FirstOrDefault();
        if (newest is null || !SemVer.TryParse(newest.NewVersion, out var listed) || listed.CompareTo(installed) <= 0 ||
            !options.AllowPrerelease && listed.Prerelease is not null) return;
        var cached = store.State.Results.FirstOrDefault(c => c.Slug == target.Slug && c.SourceUrl is { } source && target.UpdateUrls.Contains(source));
        if (cached is not null && SemVer.TryParse(cached.NewVersion, out var known) && known.CompareTo(listed) >= 0) return;
        // The list was read from this site, which is what the cache entry is matched against.
        var update = newest with { InstalledVersion = target.InstalledVersion, SourceUrl = url };
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
