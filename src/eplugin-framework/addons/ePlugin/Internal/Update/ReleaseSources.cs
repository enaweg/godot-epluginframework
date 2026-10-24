#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

internal static class ReleaseAssets
{
    /// <summary>
    /// The ZIP asset of a release to install as <paramref name="slug"/>: the only one, else the one named exactly like the
    /// slug (as "lua-gdextension.zip" next to "lua-gdextension+luajit.zip"), else the one whose name contains the slug
    /// (also with '-', '_' and '.' mixed up, as in "godot-servo-1.2.zip" for godot_servo), else the one named like an
    /// add-on, plugin or GDExtension. Among several, a demo, example, sample or tutorial project released next to the
    /// add-on is left out, and of those left the one named like an add-on is taken, as LimboAI's
    /// "limboai+v1.8.1.gdextension-4.6.zip" next to its engine builds and export templates. Null when that leaves none or
    /// several, and the source archive is installed instead.
    /// </summary>
    public static string? Select(IEnumerable<(string Name, string Url)> assets, string slug)
    {
        var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            new Uri(a.Url).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (zips.Length == 1) return zips[0].Url;
        foreach (var matches in new Func<string, bool>[]
                 {
                     name => Plain(Path.GetFileNameWithoutExtension(name)) == Plain(slug),
                     name => name.Contains(slug, StringComparison.OrdinalIgnoreCase),
                     name => Plain(name).Contains(Plain(slug), StringComparison.Ordinal),
                     IsAddon
                 })
        {
            var named = zips.Where(a => matches(a.Name)).ToArray();
            if (named.Length > 1) named = named.Where(a => !IsExtra(a.Name)).ToArray();
            if (named.Length > 1 && named.Where(a => IsAddon(a.Name)).ToArray() is { Length: > 0 } addons) named = addons;
            if (named.Length == 1) return named[0].Url;
        }
        return null;
    }

    private static string Plain(string name) => new(name.Where(c => c is not ('-' or '_' or '.' or ' ')).Select(char.ToLowerInvariant).ToArray());

    private static readonly string[] Extras = ["demo", "example", "sample", "tutorial"];
    private static readonly string[] Addons = ["addon", "plugin", "gdextension"];

    /// <summary>A project released next to the add-on rather than the add-on itself.</summary>
    private static bool IsExtra(string name) => Extras.Any(extra => name.Contains(extra, StringComparison.OrdinalIgnoreCase));

    /// <summary>The add-on itself, rather than an engine build, export template or library released next to it.</summary>
    private static bool IsAddon(string name) => Addons.Any(addon => name.Contains(addon, StringComparison.OrdinalIgnoreCase));
    public static string? String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static bool Flag(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    public static JsonElement[] Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    public static SemVer Version(JsonElement release)
    {
        var tag = String(release, "tag_name");
        if (!SemVer.TryParse(tag, out var version)) throw new InvalidDataException($"Invalid release version tag: {tag}.");
        return version;
    }
}

internal sealed class GitHubReleaseSource(UpdateHttp http, string owner, string repository, bool pinned = false) : IUpdateSource, IVersionListSource
{
    private static string? Token => ForgeTokens.GitHub(new Uri("https://api.github.com"));
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return [];
        using var document = JsonDocument.Parse(await http.ReadTextAsync($"https://api.github.com/repos/{owner}/{repository}/releases?per_page=100", ct, Token).ConfigureAwait(false));
        return document.RootElement.EnumerateArray()
            .Where(r => !ReleaseAssets.Flag(r, "draft") && SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out _))
            .Select(r => Candidate(target, r)).ToArray();
    }
    private UpdateCandidate Candidate(PluginUpdateTarget target, JsonElement release)
    {
        var root = $"https://github.com/{owner}/{repository}";
        var tag = ReleaseAssets.String(release, "tag_name")!;
        var version = ReleaseAssets.Version(release);
        var assets = ReleaseAssets.Array(release, "assets").Select(a => (ReleaseAssets.String(a, "name") ?? "", ReleaseAssets.String(a, "browser_download_url") ?? "")).Where(a => a.Item2.Length > 0);
        var download = ReleaseAssets.Select(assets, target.Slug) ?? $"{root}/archive/refs/tags/{Uri.EscapeDataString(tag)}.zip";
        return new(target.Slug, target.Name, target.InstalledVersion, version.ToString(), target.UpdateUrl,
            ReleaseAssets.String(release, "html_url") ?? $"{root}/releases/tag/{Uri.EscapeDataString(tag)}", tag, new ZipPackageRef(download, target.Slug));
    }
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return null;
        var root = $"https://github.com/{owner}/{repository}";
        try
        {
            using var document = JsonDocument.Parse(await http.ReadTextAsync($"https://api.github.com/repos/{owner}/{repository}/releases?per_page=30", ct, Token).ConfigureAwait(false));
            var release = document.RootElement.EnumerateArray()
                .Where(r => !ReleaseAssets.Flag(r, "draft") && SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) &&
                    UpdateService.Offers(target, v))
                .OrderByDescending(ReleaseAssets.Version).Select(r => (JsonElement?)r).FirstOrDefault();
            return release is null ? null : Candidate(target, release.Value);
        }
        catch (UpdateHttpException ex) when (ex.RateLimited)
        {
            using var response = await http.SendAsync(new Uri(root + "/releases/latest"), HttpMethod.Head, ct).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? throw new IOException("Cannot resolve latest release.");
            var path = final.AbsolutePath;
            var marker = path.IndexOf("/releases/tag/", StringComparison.Ordinal);
            if (final.Host != "github.com" || marker < 0 || !SemVer.TryParse(Uri.UnescapeDataString(path[(marker + 14)..]), out var version)) throw;
            if (!UpdateService.Offers(target, version)) return null;
            var tag = Uri.UnescapeDataString(path[(marker + 14)..]);
            return new(target.Slug, target.Name, target.InstalledVersion, version.ToString(), target.UpdateUrl, final.ToString(), tag,
                new ZipPackageRef($"{root}/archive/refs/tags/{Uri.EscapeDataString(tag)}.zip", target.Slug));
        }
    }
}
internal sealed class GitLabReleaseSource(UpdateHttp http, string host, string project, bool pinned = false) : IUpdateSource, IVersionListSource
{
    private async Task<JsonElement[]> Releases(int count, CancellationToken ct)
    {
        var url = $"https://{host}/api/v4/projects/{Uri.EscapeDataString(project)}/releases?per_page={count}";
        using var document = JsonDocument.Parse(await http.ReadTextAsync(url, ct, ForgeTokens.GitLab(new Uri(url)), true).ConfigureAwait(false));
        return document.RootElement.EnumerateArray().Where(r => !ReleaseAssets.Flag(r, "upcoming_release") &&
            SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out _))
            .Select(r => r.Clone()).ToArray();
    }
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return null;
        var release = (await Releases(30, ct).ConfigureAwait(false))
            .Where(r => SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) && UpdateService.Offers(target, v))
            .OrderByDescending(ReleaseAssets.Version).Select(r => (JsonElement?)r).FirstOrDefault();
        return release is null ? null : Candidate(target, release.Value);
    }
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return [];
        var candidates = new List<UpdateCandidate>();
        // A release without a ZIP cannot be installed; leave it out instead of failing the whole list.
        foreach (var release in await Releases(100, ct).ConfigureAwait(false))
            try { candidates.Add(Candidate(target, release)); } catch (IOException) { }
        return candidates;
    }
    private UpdateCandidate Candidate(PluginUpdateTarget target, JsonElement release)
    {
        var tag = ReleaseAssets.String(release, "tag_name")!;
        var version = ReleaseAssets.Version(release);
        var assets = release.GetProperty("assets");
        var download = ReleaseAssets.Select(ReleaseAssets.Array(assets, "links").Select(a =>
            (ReleaseAssets.String(a, "name") ?? "", ReleaseAssets.String(a, "direct_asset_url") ?? ReleaseAssets.String(a, "url") ?? "")).Where(a => a.Item2.Length > 0), target.Slug)
            ?? ReleaseAssets.Array(assets, "sources").Where(a => ReleaseAssets.String(a, "format") == "zip").Select(a => ReleaseAssets.String(a, "url")).FirstOrDefault();
        if (download is null) throw new IOException("Release has no ZIP package.");
        var link = release.TryGetProperty("_links", out var links) ? ReleaseAssets.String(links, "self") : null;
        return new(target.Slug, target.Name, target.InstalledVersion, version.ToString(), target.UpdateUrl,
            link ?? $"https://{host}/{project}/-/releases/{Uri.EscapeDataString(tag)}", tag, new ZipPackageRef(download, target.Slug));
    }
}
/// <summary>Releases of Forgejo and Gitea, which share their API, such as Codeberg's: https://host/owner/repository/releases.</summary>
internal sealed class ForgejoReleaseSource(UpdateHttp http, string host, string owner, string repository, bool pinned = false) : IUpdateSource, IVersionListSource
{
    // Forgejo and Gitea return at most 50 releases per page.
    private const int PageSize = 50;
    private async Task<JsonElement[]> Releases(int pages, CancellationToken ct)
    {
        var releases = new List<JsonElement>();
        for (var page = 1; page <= pages; page++)
        {
            var url = $"https://{host}/api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?limit={PageSize}&page={page}";
            using var document = JsonDocument.Parse(await http.ReadTextAsync(url, ct, ForgeTokens.Forgejo(new Uri(url))).ConfigureAwait(false));
            var list = document.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
            releases.AddRange(list.Where(r => !ReleaseAssets.Flag(r, "draft") && SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out _)));
            if (list.Length < PageSize) break;
        }
        return releases.ToArray();
    }
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return null;
        var release = (await Releases(1, ct).ConfigureAwait(false))
            .Where(r => SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) && UpdateService.Offers(target, v))
            .OrderByDescending(ReleaseAssets.Version).Select(r => (JsonElement?)r).FirstOrDefault();
        return release is null ? null : Candidate(target, release.Value);
    }
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return [];
        return (await Releases(2, ct).ConfigureAwait(false)).Select(r => Candidate(target, r)).ToArray();
    }
    private UpdateCandidate Candidate(PluginUpdateTarget target, JsonElement release)
    {
        var root = $"https://{host}/{owner}/{repository}";
        var tag = ReleaseAssets.String(release, "tag_name")!;
        var version = ReleaseAssets.Version(release);
        var assets = ReleaseAssets.Array(release, "assets").Select(a => (ReleaseAssets.String(a, "name") ?? "", ReleaseAssets.String(a, "browser_download_url") ?? "")).Where(a => a.Item2.Length > 0);
        var download = ReleaseAssets.Select(assets, target.Slug) ?? ReleaseAssets.String(release, "zipball_url") ?? $"{root}/archive/{Uri.EscapeDataString(tag)}.zip";
        return new(target.Slug, target.Name, target.InstalledVersion, version.ToString(), target.UpdateUrl,
            ReleaseAssets.String(release, "html_url") ?? $"{root}/releases/tag/{Uri.EscapeDataString(tag)}", tag, new ZipPackageRef(download, target.Slug));
    }
}
#endif
