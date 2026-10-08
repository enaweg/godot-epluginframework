#if TOOLS
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ZipPackageRef), "zip")]
[JsonDerivedType(typeof(GitPackageRef), "git")]
internal abstract record UpdatePackageRef;
internal sealed record ZipPackageRef(string Url, string ExpectedName) : UpdatePackageRef;
internal sealed record GitPackageRef(string Repository, string Path, string Commit) : UpdatePackageRef;
internal sealed record PluginUpdateTarget(string Slug, string Name, string InstalledVersion, string UpdateUrl,
    string Directory, bool IsBlocked = false, bool StoreReadOnly = false, string? RecordedVersion = null);
internal sealed record UpdateCandidate(string Slug, string PluginName, string InstalledVersion, string NewVersion,
    string SourceUrl, string? ReleaseUrl, string? ResolvedRevision, UpdatePackageRef Package);
internal sealed record UpdateCheckFailure(string Slug, string Message);
internal sealed record UpdateCheckResult(IReadOnlyList<UpdateCandidate> Updates,
    IReadOnlyList<UpdateCheckFailure> Failures, DateTimeOffset CheckedAtUtc);
internal sealed record UpdateCheckOptions(bool AllowPrerelease = false, int TimeoutSeconds = 15);
internal interface IClock { DateTimeOffset UtcNow { get; } }
internal sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
internal interface IUpdateSource
{
    Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct);
}
/// <summary>A source that can list every published version, so a specific one (also an older one) can be installed.</summary>
internal interface IVersionListSource
{
    /// <summary>One candidate per published version; the order is not defined.</summary>
    Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct);
}
internal interface IUpdateSourceFactory { IUpdateSource Create(string url); }
internal sealed class UnsupportedUpdateSource(string url) : IUpdateSource
{
    public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
        Task.FromException<UpdateCandidate?>(new NotSupportedException($"Unsupported update_url '{url}'."));
}
internal sealed class UpdateSourceFactory(UpdateHttp? http = null, IGitRunner? git = null) : IUpdateSourceFactory
{
    public IUpdateSource Create(string url)
    {
        var transport = http ?? new UpdateHttp();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (uri.Host == "github.com" && segments.Length >= 3 && segments[2] == "releases")
                return new GitHubReleaseSource(transport, segments[0], segments[1].Replace(".git", ""), segments.Length > 3 && segments[3] == "tag");
            var index = Array.IndexOf(segments, "-");
            if (index > 0 && segments.Length > index + 1 && segments[index + 1] == "releases")
                return new GitLabReleaseSource(transport, uri.Authority, string.Join("/", segments[..index]), segments.Length > index + 2);
        }
        if (GitUrl.TryParse(url, out var parsed) && (parsed!.Repository.EndsWith(".git", StringComparison.Ordinal) || url.Contains("?path=")))
            return new GitSource(transport, git ?? new GitRunner(), parsed!);
        return new UnsupportedUpdateSource(url);
    }
}
#endif
