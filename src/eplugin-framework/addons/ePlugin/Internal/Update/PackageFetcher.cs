#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class PackageFetcher(UpdateHttp http, IGitRunner git)
{
    public async Task<IReadOnlyList<ValidatedPackage>> FetchAsync(IReadOnlyList<UpdateCandidate> candidates,
        IReadOnlyList<PluginUpdateTarget> targets, string transactionDirectory, IProgress<double>? progress, CancellationToken ct, bool allowDowngrade = false,
        bool allowReinstall = false)
    {
        var result = new List<ValidatedPackage>();
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var target = System.Linq.Enumerable.First(targets, t => t.Slug == candidate.Slug);
                var stage = PackageFiles.Inside(Path.Combine(transactionDirectory, "staging"), candidate.Slug);
                Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                var part = i;
                var relay = new InlineProgress(p => progress?.Report((part + p) / candidates.Count));
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
                    await new GitSource(http, git, url).FetchAsync(package, stage, ct).ConfigureAwait(false);
                }
                else throw new InvalidDataException("Unsupported package reference.");
                var validated = new AddonPackageValidator().Validate(new(target, candidate, stage, allowDowngrade, allowReinstall));
                if (!validated.IsValid) throw new InvalidDataException($"Package for '{candidate.Slug}' was refused: " + string.Join("; ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(validated.Findings, f => f.Severity == FindingSeverity.Error), f => f.Message)));
                result.Add(validated);
                progress?.Report((i + 1.0) / candidates.Count);
            }
            return result;
        }
        catch { if (Directory.Exists(transactionDirectory)) Directory.Delete(transactionDirectory, true); throw; }
    }
}
internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
#endif
