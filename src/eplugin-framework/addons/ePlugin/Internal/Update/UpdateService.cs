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
    /// Whether <paramref name="version"/> is offered as an update of <paramref name="installed"/>: the next better version
    /// of the same kind. A stable version moves only to a newer stable one, a prerelease to any newer version (a newer
    /// prerelease or the release). Every other version is installed from the plugin's version list.
    /// </summary>
    public static bool IsUpdate(SemVer installed, SemVer version) => version.CompareTo(installed) > 0 && Offers(installed, version);

    /// <summary>
    /// Whether a check may choose <paramref name="version"/> for the target, before comparing it with the installed one
    /// (see <see cref="IsUpdate"/>). An installed version that cannot be read counts as stable.
    /// </summary>
    public static bool Offers(PluginUpdateTarget target, SemVer version) =>
        Offers(SemVer.TryParse(target.InstalledVersion, out var installed) ? installed : default, version);

    private static bool Offers(SemVer installed, SemVer version) => version.Prerelease is null || installed.Prerelease is not null;

    /// <summary>
    /// Checks the update site of every target that has one and caches the result. A project update site is tried first,
    /// then the plugin.cfg update_url, then the built-in one, each when the ones before are unsupported or fail.
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
                    IsUpdate(installed, remote)) updates.Add(candidate);
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
    /// The newest version in the local plugin directories that is an update of the installed one (see
    /// <see cref="IsUpdate"/>), per target. The index
    /// is in memory and rebuilt every editor start, so this is never cached.
    /// </summary>
    public static IReadOnlyList<UpdateCandidate> CheckLocal(IReadOnlyList<PluginUpdateTarget> targets, LocalPackageIndex index)
    {
        return targets.Select(target => SemVer.TryParse(target.InstalledVersion, out var installed)
                ? LocalDirectorySource.Versions(index, target).FirstOrDefault(c => SemVer.TryParse(c.NewVersion, out var local) && IsUpdate(installed, local))
                : null)
            .OfType<UpdateCandidate>().OrderBy(c => c.Slug, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Leaves out the releases that are installed already. That matters for a release announced as a newer version whose
    /// plugin.cfg did not raise it: after it was installed, the installed version is still lower than the announced one.
    /// </summary>
    public static IReadOnlyList<UpdateCandidate> WithoutInstalled(IEnumerable<UpdateCandidate> candidates, IReadOnlyDictionary<string, AppliedRevision>? revisions) =>
        candidates.Where(c => !(c.ResolvedRevision is { } revision && revisions?.GetValueOrDefault(c.Slug) is { } applied &&
                                applied.Source == c.Origin && applied.Revision == revision && applied.Version == c.InstalledVersion)).ToArray();

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
        RecordListedUpdate(target, url, versions);
        return versions;
    }

    /// <summary>
    /// Runs <paramref name="run"/> against the target's update sites in order, each with its own timeout: the project's
    /// update site, the plugin.cfg update_url, then the built-in one, each when the ones before are missing, unsupported
    /// or fail. Returns the first result, the
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
                var site = target with { UpdateUrl = url, OverrideUrl = null, KnownUrl = null };
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
    /// Caches the newest listed version that is an update (see <see cref="IsUpdate"/>) as the plugin's update. A cached
    /// update that is at least as new stays. The last
    /// check time is not changed, since the other plugins were not checked.
    /// </summary>
    private void RecordListedUpdate(PluginUpdateTarget target, string url, IReadOnlyList<UpdateCandidate> versions)
    {
        if (!SemVer.TryParse(target.InstalledVersion, out var installed)) return;
        var newest = versions.FirstOrDefault(v => SemVer.TryParse(v.NewVersion, out var version) && IsUpdate(installed, version));
        if (newest is null || !SemVer.TryParse(newest.NewVersion, out var listed)) return;
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
            .OrderByDescending(c => c.Version).DistinctBy(c => c.Version.Key)
            .Select(c => c.Candidate).ToArray();
}
#endif
