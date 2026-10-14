#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>A package of a batch that could not be fetched or was refused by validation; nothing of it stays staged.</summary>
internal sealed record RefusedPackage(UpdateCandidate Candidate, string Reason);
/// <summary>The packages of a batch that were staged and validated, and the ones that were refused.</summary>
internal sealed record StagedBatch(IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused);
internal sealed class PackageFetcher(UpdateHttp http, IGitRunner git)
{
    /// <summary>
    /// Stages and validates every package of the batch. A package that cannot be fetched or is refused is left out alone,
    /// so it does not hold back the others, which are installed in independent groups anyway. Only a batch in which every
    /// package was refused fails, naming the reason of each.
    /// </summary>
    public async Task<StagedBatch> FetchAsync(IReadOnlyList<UpdateCandidate> candidates,
        IReadOnlyList<PluginUpdateTarget> targets, string transactionDirectory, IProgress<double>? progress, CancellationToken ct, bool allowDowngrade = false,
        bool allowReinstall = false)
    {
        var result = new List<ValidatedPackage>();
        var refused = new List<RefusedPackage>();
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var stage = PackageFiles.Inside(Path.Combine(transactionDirectory, "staging"), candidate.Slug);
                var part = i;
                var relay = new InlineProgress(p => progress?.Report((part + p) / candidates.Count));
                string? reason;
                try
                {
                    var validated = await StageAsync(candidate, targets, stage, transactionDirectory, relay, allowDowngrade, allowReinstall, ct).ConfigureAwait(false);
                    reason = validated.IsValid ? null
                        : $"Package for '{candidate.Slug}' was refused: " + string.Join("; ", validated.Findings.Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Message));
                    if (reason is null) result.Add(validated);
                }
                // Only the user cancels the batch; a timeout of one download refuses that package.
                catch (Exception ex) when (!ct.IsCancellationRequested) { reason = $"Cannot fetch the package for '{candidate.Slug}': {ex.Message}"; }
                if (reason is not null)
                {
                    refused.Add(new(candidate, reason));
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                }
                progress?.Report((i + 1.0) / candidates.Count);
            }
            if (result.Count == 0) throw new InvalidDataException(string.Join(" ", refused.Select(r => r.Reason)));
            return new(result, refused);
        }
        catch { if (Directory.Exists(transactionDirectory)) Directory.Delete(transactionDirectory, true); throw; }
    }

    private async Task<ValidatedPackage> StageAsync(UpdateCandidate candidate, IReadOnlyList<PluginUpdateTarget> targets, string stage,
        string transactionDirectory, IProgress<double> relay, bool allowDowngrade, bool allowReinstall, CancellationToken ct)
    {
        var target = targets.First(t => t.Slug == candidate.Slug);
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        if (candidate.Package is ZipPackageRef zip)
        {
            var download = Path.Combine(transactionDirectory, "download.tmp");
            try
            {
                await http.DownloadAsync(zip.Url, download, relay, ct, candidate.SourceUrl).ConfigureAwait(false);
                SafeZipExtractor.Extract(download, candidate.Slug, stage, ct);
            }
            finally { if (File.Exists(download)) File.Delete(download); }
        }
        else if (candidate.Package is LocalZipPackageRef local)
        {
            // Read in place: the archive is only opened, never changed, and staging validates it like a download.
            if (candidate.SourceUrl is not null || !Path.IsPathFullyQualified(local.Path) || !local.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local package does not match its declared source.");
            if (!File.Exists(local.Path)) throw new FileNotFoundException($"Local package '{local.Path}' no longer exists; check for updates again.");
            SafeZipExtractor.Extract(local.Path, candidate.Slug, stage, ct, requireSlugFolder: true);
        }
        else if (candidate.Package is GitPackageRef package)
        {
            if (candidate.SourceUrl is null || !GitUrl.TryParse(candidate.SourceUrl, out var url) || url!.Repository != package.Repository || url.Path != package.Path)
                throw new InvalidDataException("Git package does not match its declared source.");
            await new GitSource(http, git, url).FetchAsync(package, candidate.Slug, stage, ct).ConfigureAwait(false);
        }
        else throw new InvalidDataException("Unsupported package reference.");
        return new AddonPackageValidator().Validate(new(target, candidate, stage, allowDowngrade, allowReinstall));
    }
}
internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
#endif
