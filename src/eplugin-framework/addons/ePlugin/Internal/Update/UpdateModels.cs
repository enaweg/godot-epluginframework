#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Kinds;

namespace Enaweg.Plugin.Internal.Update;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ZipPackageRef), "zip")]
[JsonDerivedType(typeof(GitPackageRef), "git")]
[JsonDerivedType(typeof(LocalZipPackageRef), "local-zip")]
internal abstract record UpdatePackageRef;
internal sealed record ZipPackageRef(string Url, string ExpectedName) : UpdatePackageRef;
internal sealed record GitPackageRef(string Repository, string Path, string Commit) : UpdatePackageRef;
/// <summary>A ZIP file found by indexing one of the user's local plugin directories.</summary>
internal sealed record LocalZipPackageRef(string Path) : UpdatePackageRef;
/// <param name="UpdateUrl">The plugin.cfg update_url; null when only local plugin directories can update the plugin.</param>
/// <param name="OverrideUrl">The update site the project sets for the plugin, which is tried before the plugin.cfg one.</param>
/// <param name="KnownUrl">The update site of ePlugin's built-in list, the last one tried.</param>
/// <param name="Kind">
/// What the editor knows of the installed plugin: <see cref="PluginKind.Framework"/>, <see cref="PluginKind.EPlugin"/>, or
/// <see cref="PluginKind.Unknown"/> for every other plugin, whose kind validation reads from its files.
/// </param>
internal sealed record PluginUpdateTarget(string Slug, string Name, string InstalledVersion, string? UpdateUrl,
    string Directory, bool IsBlocked = false, bool StoreReadOnly = false, string? RecordedVersion = null,
    string? OverrideUrl = null, string? KnownUrl = null, PluginKind Kind = PluginKind.Unknown)
{
    /// <summary>
    /// The update sites to try, in order: the project's, the plugin.cfg update_url, then the built-in one. A candidate
    /// found at any of them belongs to the plugin. Empty when only local plugin directories can update the plugin.
    /// </summary>
    public IReadOnlyList<string> UpdateUrls => new[] { OverrideUrl, UpdateUrl, KnownUrl }
        .Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url!).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A GDExtension add-on ePlugin did not install: nothing names its version, so no release counts as an update of it,
    /// but its latest release is still looked up so the ePlugin Manager can offer to install it.
    /// </summary>
    public bool VersionUnknown => Kind == PluginKind.GDExtensionAddon && InstalledVersion.Length == 0;
}
/// <param name="SourceUrl">The update_url the candidate was found at; null for a package of a local plugin directory.</param>
internal sealed record UpdateCandidate(string Slug, string PluginName, string InstalledVersion, string NewVersion,
    string? SourceUrl, string? ReleaseUrl, string? ResolvedRevision, UpdatePackageRef Package)
{
    /// <summary>Where the package comes from: the update_url, or the ZIP file of a local package.</summary>
    [JsonIgnore] public string Origin => SourceUrl ?? (Package as LocalZipPackageRef)?.Path ?? "";
}
internal sealed record UpdateCheckFailure(string Slug, string Message);
/// <param name="Fallbacks">Plugins whose project update site failed, so their plugin.cfg update_url was used instead.</param>
internal sealed record UpdateCheckResult(IReadOnlyList<UpdateCandidate> Updates,
    IReadOnlyList<UpdateCheckFailure> Failures, DateTimeOffset CheckedAtUtc, IReadOnlyList<UpdateCheckFailure>? Fallbacks = null);
internal sealed record UpdateCheckOptions(int TimeoutSeconds = 15);
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
    /// <summary>Whether <paramref name="url"/> is an update site the updater can read; nothing is contacted.</summary>
    public static bool IsSupported(string? url) =>
        !string.IsNullOrWhiteSpace(url) && new UpdateSourceFactory().Create(url.Trim()) is not UnsupportedUpdateSource;

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
            // The same path as GitHub's on any other host: Forgejo and Gitea, e.g. Codeberg.
            if (uri.Host != "github.com" && index < 0 && segments.Length >= 3 && segments[2] == "releases" &&
                segments[..2].All(s => s.Length > 0) && (segments.Length == 3 || segments.Length >= 5 && segments[3] == "tag"))
                return new ForgejoReleaseSource(transport, uri.Authority, segments[0], segments[1].Replace(".git", ""), segments.Length > 3);
        }
        if (GitUrl.TryParse(url, out var parsed) && (parsed!.Repository.EndsWith(".git", StringComparison.Ordinal) || url.Contains("?path=")))
            return new GitSource(transport, git ?? new GitRunner(), parsed!);
        return new UnsupportedUpdateSource(url);
    }
}
#endif
