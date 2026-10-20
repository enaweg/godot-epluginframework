#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class UpdateHttpException(string message, bool rateLimited = false) : IOException(message)
{
    public bool RateLimited { get; } = rateLimited;
}

/// <summary>
/// The tokens for private release APIs, read from the editor's environment. Each goes only to the hosts it belongs to:
/// an update_url comes from a plugin's plugin.cfg, so any installed add-on could otherwise name a host that receives it.
/// GitHub's token goes to GitHub, GitLab's to gitlab.com and Forgejo's to codeberg.org, the last two also to the hosts
/// listed in EPLUGIN_GITLAB_HOSTS or EPLUGIN_FORGEJO_HOSTS (host names separated by commas, e.g. a self-hosted instance).
/// </summary>
internal static class ForgeTokens
{
    internal enum Forge { GitHub, GitLab, Forgejo }

    public static string? GitHub(Uri uri) => For(Forge.GitHub, uri.Host, Environment.GetEnvironmentVariable);
    public static string? GitLab(Uri uri) => For(Forge.GitLab, uri.Host, Environment.GetEnvironmentVariable);
    public static string? Forgejo(Uri uri) => For(Forge.Forgejo, uri.Host, Environment.GetEnvironmentVariable);

    /// <summary>The token of <paramref name="forge"/> for <paramref name="host"/>, or null when the host is not trusted with it.</summary>
    internal static string? For(Forge forge, string host, Func<string, string?> environment)
    {
        var rule = forge switch
        {
            Forge.GitHub => new Rule(["github.com", "api.github.com", "raw.githubusercontent.com"], null, ["EPLUGIN_GITHUB_TOKEN", "GITHUB_TOKEN"]),
            Forge.GitLab => new Rule(["gitlab.com"], "EPLUGIN_GITLAB_HOSTS", ["EPLUGIN_GITLAB_TOKEN", "GITLAB_TOKEN"]),
            _ => new Rule(["codeberg.org"], "EPLUGIN_FORGEJO_HOSTS", ["EPLUGIN_FORGEJO_TOKEN", "FORGEJO_TOKEN", "GITEA_TOKEN"])
        };
        var trusted = rule.Hosts.Concat((rule.HostsVariable is null ? null : environment(rule.HostsVariable))?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);
        if (!trusted.Contains(host, StringComparer.OrdinalIgnoreCase)) return null;
        return rule.TokenVariables.Select(environment).FirstOrDefault(token => !string.IsNullOrEmpty(token));
    }

    private sealed record Rule(string[] Hosts, string? HostsVariable, string[] TokenVariables);
}

internal sealed class UpdateHttp
{
    public const long MaximumDownload = 256L * 1024 * 1024;
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client;
    public UpdateHttp(HttpClient? client = null) { _client = client ?? Shared; }

    public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, CancellationToken ct,
        string? token = null, bool gitlab = false)
    {
        var origin = uri;
        for (var i = 0; i <= 5; i++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("Only HTTPS update downloads without URL credentials are supported.");
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd("ePlugin-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (token is not null && uri.Host == origin.Host)
            {
                if (gitlab) request.Headers.Add("PRIVATE-TOKEN", token);
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.RequestMessage ??= request;
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new IOException("Redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var limited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && string.Join("", remaining) == "0";
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var values) ? string.Join(",", values) : response.Headers.RetryAfter?.ToString();
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new UpdateHttpException(limited ? $"Rate limit reached (reset/retry: {reset ?? "unknown"})." : $"Update request failed with HTTP {status}.", limited);
            }
            return response;
        }
        throw new IOException("Update request exceeded five redirects.");
    }

    public async Task<string> ReadTextAsync(string url, CancellationToken ct, string? token = null, bool gitlab = false)
    {
        using var response = await SendAsync(new Uri(url), HttpMethod.Get, ct, token, gitlab).ConfigureAwait(false);
        using var output = new MemoryStream();
        await CopyBounded(response, output, 4 * 1024 * 1024, null, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    public async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct, string? sourceUrl = null)
    {
        string? token = null;
        var gitlab = false;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) && new Uri(url).Host == source.Host)
        {
            gitlab = source.AbsolutePath.Contains("/-/", StringComparison.Ordinal);
            if (source.Host == "github.com") token = ForgeTokens.GitHub(source);
            else if (gitlab) token = ForgeTokens.GitLab(source);
            else if (source.AbsolutePath.Contains("/releases", StringComparison.Ordinal)) token = ForgeTokens.Forgejo(source);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await SendAsync(new Uri(url), HttpMethod.Get, timeout.Token, token, gitlab).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyBounded(response, output, MaximumDownload, progress, timeout.Token).ConfigureAwait(false);
    }
    private static async Task CopyBounded(HttpResponseMessage response, Stream output, long limit, IProgress<double>? progress, CancellationToken ct)
    {
        var length = response.Content.Headers.ContentLength;
        if (length > limit) throw new IOException("Update download exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new IOException("Update download exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            if (length > 0) progress?.Report((double)total / length.Value);
        }
    }
}
#endif
