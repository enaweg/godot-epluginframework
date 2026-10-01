#if TOOLS
using System;
using System.IO;
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
            if (source.Host == "github.com") token = Environment.GetEnvironmentVariable("EPLUGIN_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            else if (gitlab) token = Environment.GetEnvironmentVariable("EPLUGIN_GITLAB_TOKEN") ?? Environment.GetEnvironmentVariable("GITLAB_TOKEN");
        }
        using var response = await SendAsync(new Uri(url), HttpMethod.Get, ct, token, gitlab).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyBounded(response, output, MaximumDownload, progress, ct).ConfigureAwait(false);
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
