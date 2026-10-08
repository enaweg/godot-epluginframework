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
    public static string? Select(IEnumerable<(string Name, string Url)> assets, string slug)
    {
        var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            new Uri(a.Url).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (zips.Length == 1) return zips[0].Url;
        var named = zips.Where(a => a.Name.Contains(slug, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1) return named[0].Url;
        var plugin = zips.Where(a => a.Name.Contains("addon", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase)).ToArray();
        return plugin.Length == 1 ? plugin[0].Url : null;
    }
    public static string? String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static bool Flag(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    public static JsonElement[] Array(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
}

internal sealed class GitHubReleaseSource(UpdateHttp http, string owner, string repository, bool pinned = false) : IUpdateSource, IVersionListSource
{
    private static string? Token => Environment.GetEnvironmentVariable("EPLUGIN_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return [];
        using var document = JsonDocument.Parse(await http.ReadTextAsync($"https://api.github.com/repos/{owner}/{repository}/releases?per_page=100", ct, Token).ConfigureAwait(false));
        return document.RootElement.EnumerateArray()
            .Where(r => !ReleaseAssets.Flag(r, "draft") && (options.AllowPrerelease || !ReleaseAssets.Flag(r, "prerelease")) &&
                SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) && (options.AllowPrerelease || v.Prerelease is null))
            .Select(r => Candidate(target, r)).ToArray();
    }
    private UpdateCandidate Candidate(PluginUpdateTarget target, JsonElement release)
    {
        var root = $"https://github.com/{owner}/{repository}";
        var tag = ReleaseAssets.String(release, "tag_name")!;
        SemVer.TryParse(tag, out var version);
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
                .Where(r => !ReleaseAssets.Flag(r, "draft") && (options.AllowPrerelease || !ReleaseAssets.Flag(r, "prerelease")) &&
                    SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) && (options.AllowPrerelease || v.Prerelease is null))
                .OrderByDescending(r => { SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v); return v; }).Select(r => (JsonElement?)r).FirstOrDefault();
            return release is null ? null : Candidate(target, release.Value);
        }
        catch (UpdateHttpException ex) when (ex.RateLimited)
        {
            using var response = await http.SendAsync(new Uri(root + "/releases/latest"), HttpMethod.Head, ct).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? throw new IOException("Cannot resolve latest release.");
            var path = final.AbsolutePath;
            var marker = path.IndexOf("/releases/tag/", StringComparison.Ordinal);
            if (final.Host != "github.com" || marker < 0 || !SemVer.TryParse(Uri.UnescapeDataString(path[(marker + 14)..]), out var version) ||
                !options.AllowPrerelease && version.Prerelease is not null) throw;
            var tag = Uri.UnescapeDataString(path[(marker + 14)..]);
            return new(target.Slug, target.Name, target.InstalledVersion, version.ToString(), target.UpdateUrl, final.ToString(), tag,
                new ZipPackageRef($"{root}/archive/refs/tags/{Uri.EscapeDataString(tag)}.zip", target.Slug));
        }
    }
}
internal sealed class GitLabReleaseSource(UpdateHttp http, string host, string project, bool pinned = false) : IUpdateSource, IVersionListSource
{
    private async Task<JsonElement[]> Releases(UpdateCheckOptions options, int count, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("EPLUGIN_GITLAB_TOKEN") ?? Environment.GetEnvironmentVariable("GITLAB_TOKEN");
        using var document = JsonDocument.Parse(await http.ReadTextAsync($"https://{host}/api/v4/projects/{Uri.EscapeDataString(project)}/releases?per_page={count}", ct, token, true).ConfigureAwait(false));
        return document.RootElement.EnumerateArray().Where(r => !ReleaseAssets.Flag(r, "upcoming_release") &&
            SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v) && (options.AllowPrerelease || v.Prerelease is null))
            .Select(r => r.Clone()).ToArray();
    }
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return null;
        var release = (await Releases(options, 30, ct).ConfigureAwait(false))
            .OrderByDescending(r => { SemVer.TryParse(ReleaseAssets.String(r, "tag_name"), out var v); return v; }).Select(r => (JsonElement?)r).FirstOrDefault();
        return release is null ? null : Candidate(target, release.Value);
    }
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (pinned) return [];
        var candidates = new List<UpdateCandidate>();
        // A release without a ZIP cannot be installed; leave it out instead of failing the whole list.
        foreach (var release in await Releases(options, 100, ct).ConfigureAwait(false))
            try { candidates.Add(Candidate(target, release)); } catch (IOException) { }
        return candidates;
    }
    private UpdateCandidate Candidate(PluginUpdateTarget target, JsonElement release)
    {
        var tag = ReleaseAssets.String(release, "tag_name")!;
        SemVer.TryParse(tag, out var version);
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
#endif
