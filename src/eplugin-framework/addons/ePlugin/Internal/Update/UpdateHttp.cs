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
        var rule = RuleOf(forge);
        var trusted = rule.Hosts.Concat(Trusted(rule, environment));
        if (!trusted.Contains(host, StringComparer.OrdinalIgnoreCase)) return null;
        return rule.TokenVariables.Select(environment).FirstOrDefault(token => !string.IsNullOrEmpty(token));
    }

    /// <summary>
    /// What to set so a request to <paramref name="uri"/> is sent with a token, or, when one was sent, what to check; null
    /// when the URL belongs to no forge ePlugin sends tokens to. Forges are told apart by host and API path.
    /// </summary>
    internal static string? Hint(Uri uri, bool tokenSent, Func<string, string?> environment)
    {
        Forge? forge = GitHubHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ? Forge.GitHub
            : uri.AbsolutePath.Contains("/api/v4/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/-/", StringComparison.Ordinal) ? Forge.GitLab
            : uri.AbsolutePath.Contains("/api/v1/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/releases", StringComparison.Ordinal) ? Forge.Forgejo
            : null;
        if (forge is not { } known) return null;
        var rule = RuleOf(known);
        var variables = rule.TokenVariables[0] + " (or " + string.Join(", ", rule.TokenVariables.Skip(1)) + ")";
        if (tokenSent) return $"Check that the token in {variables} can read this repository and has not expired.";
        var trusted = rule.Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
                      Trusted(rule, environment).Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        return trusted ? $"set {variables} in the editor's environment, then restart the editor."
            : $"set {variables} and add {uri.Host} to {rule.HostsVariable} in the editor's environment, then restart the editor.";
    }

    private static readonly string[] GitHubHosts = ["github.com", "api.github.com", "raw.githubusercontent.com"];

    private static Rule RuleOf(Forge forge) => forge switch
    {
        Forge.GitHub => new Rule(GitHubHosts, null, ["EPLUGIN_GITHUB_TOKEN", "GITHUB_TOKEN"]),
        Forge.GitLab => new Rule(["gitlab.com"], "EPLUGIN_GITLAB_HOSTS", ["EPLUGIN_GITLAB_TOKEN", "GITLAB_TOKEN"]),
        _ => new Rule(["codeberg.org"], "EPLUGIN_FORGEJO_HOSTS", ["EPLUGIN_FORGEJO_TOKEN", "FORGEJO_TOKEN", "GITEA_TOKEN"])
    };

    private static string[] Trusted(Rule rule, Func<string, string?> environment) =>
        (rule.HostsVariable is null ? null : environment(rule.HostsVariable))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private sealed record Rule(string[] Hosts, string? HostsVariable, string[] TokenVariables);
}

internal sealed class UpdateHttp
{
    public const long MaximumDownload = 256L * 1024 * 1024;
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client;
    public UpdateHttp(HttpClient? client = null) { _client = client ?? Shared; }

    /// <summary>
    /// How long a download may receive nothing before it fails. Its total time is not limited: a large GDExtension package
    /// takes long on a slow connection, but one that stopped sending would otherwise hang until it is canceled.
    /// </summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="accept">The media type asked for; GitHub's API sends a release asset's file only for application/octet-stream.</param>
    public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, CancellationToken ct,
        string? token = null, bool gitlab = false, string accept = "application/json")
    {
        var origin = uri;
        for (var i = 0; i <= 5; i++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("Only HTTPS update downloads without URL credentials are supported.");
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd("ePlugin-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (token is not null && uri.Host == origin.Host)
            {
                if (gitlab) request.Headers.Add("PRIVATE-TOKEN", token);
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw UpdateErrors.Unreachable(ex, uri); }
            // HttpClient's own timeout; the caller's token cancels the request otherwise.
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw UpdateErrors.TimedOut(uri.Host, _client.Timeout, ex); }
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
                // GitHub answers a primary rate limit with 403 and no remaining requests, a secondary one with Retry-After.
                var limited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.Forbidden && (Header(response, "X-RateLimit-Remaining") == "0" || response.Headers.RetryAfter is not null);
                var now = DateTimeOffset.UtcNow;
                var reset = UpdateErrors.RateLimitReset(Header(response, "X-RateLimit-Reset") ?? Header(response, "RateLimit-Reset"), response.Headers.RetryAfter, now);
                var status = response.StatusCode;
                response.Dispose();
                throw UpdateErrors.Status(uri, status, limited, reset, token is not null && uri.Host == origin.Host, now, Environment.GetEnvironmentVariable);
            }
            return response;
        }
        throw new IOException("Update request exceeded five redirects.");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

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
        var accept = "application/json";
        var target = new Uri(url);
        if (target.Host == "api.github.com")
        {
            // A release asset or archive through GitHub's API (GitHubReleaseSource uses it with a token): its own host.
            token = ForgeTokens.GitHub(target);
            accept = "application/octet-stream";
        }
        else if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) && target.Host == source.Host)
        {
            gitlab = source.AbsolutePath.Contains("/-/", StringComparison.Ordinal);
            if (source.Host == "github.com") token = ForgeTokens.GitHub(source);
            else if (gitlab) token = ForgeTokens.GitLab(source);
            else if (source.AbsolutePath.Contains("/releases", StringComparison.Ordinal)) token = ForgeTokens.Forgejo(source);
        }
        using var response = await SendAsync(target, HttpMethod.Get, ct, token, gitlab, accept).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyBounded(response, output, MaximumDownload, progress, ct, StallTimeout).ConfigureAwait(false);
    }
    private static async Task CopyBounded(HttpResponseMessage response, Stream output, long limit, IProgress<double>? progress, CancellationToken ct,
        TimeSpan? stallTimeout = null)
    {
        var length = response.Content.Headers.ContentLength;
        if (length > limit) throw new IOException("Update download exceeds its size limit.");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        try
        {
            while (true)
            {
                // restarted for every read, so only a connection that sends nothing for that long fails
                if (stallTimeout is { } timeout) stall.CancelAfter(timeout);
                if ((count = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) == 0) break;
                total += count;
                if (total > limit) throw new IOException("Update download exceeds its size limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                if (length > 0) progress?.Report((double)total / length.Value);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Update download stalled: nothing was received for {stallTimeout!.Value.TotalSeconds:0} seconds.");
        }
    }
}
#endif
