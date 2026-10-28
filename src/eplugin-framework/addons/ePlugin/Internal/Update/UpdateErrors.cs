#if TOOLS
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>Why an update request failed, so failures of one kind at every update site can be reported once.</summary>
internal enum UpdateProblem
{
    /// <summary>Anything else, such as a release without a usable package.</summary>
    Other,
    /// <summary>The host could not be resolved or connected to: no network connection, a DNS or proxy problem.</summary>
    Offline,
    /// <summary>The host did not respond in time.</summary>
    Timeout,
    /// <summary>No secure connection could be made, e.g. a wrong system clock or a proxy that intercepts HTTPS.</summary>
    Tls,
    /// <summary>The host's rate limit was reached.</summary>
    RateLimited,
    /// <summary>HTTP 401, 403 or 404: the repository is private, needs a token, or does not exist.</summary>
    Access,
    /// <summary>Any other HTTP error status.</summary>
    Http,
    /// <summary>git is not installed, not on PATH, or too old.</summary>
    GitMissing,
    /// <summary>git needed credentials it could not ask for.</summary>
    GitAuthentication,
}

/// <summary>An update request failed for a reason the user can act on; the message says what to do.</summary>
internal class UpdateProblemException(string message, UpdateProblem problem, Exception? inner = null) : IOException(message, inner)
{
    public UpdateProblem Problem { get; } = problem;
}

/// <summary>An update site answered with an error status or could not be reached.</summary>
internal sealed class UpdateHttpException(string message, UpdateProblem problem = UpdateProblem.Http, Exception? inner = null)
    : UpdateProblemException(message, problem, inner)
{
    public bool RateLimited => Problem == UpdateProblem.RateLimited;
}

/// <summary>
/// Turns failed update requests into messages that name the host and what to do. Pure, so the texts are tested without a
/// network.
/// </summary>
internal static class UpdateErrors
{
    public static UpdateProblem ProblemOf(Exception ex) => ex switch
    {
        UpdateProblemException problem => problem.Problem,
        TimeoutException => UpdateProblem.Timeout,
        _ => UpdateProblem.Other
    };

    /// <summary>The host of an update site or URL for messages; the text itself when it names none.</summary>
    public static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host;
        if (GitUrl.TryParse(url, out var git) && git!.Repository.StartsWith("git@", StringComparison.Ordinal))
            return git.Repository[4..].Split(':')[0];
        return url;
    }

    /// <summary>A request to <paramref name="uri"/> that got no response.</summary>
    public static UpdateHttpException Unreachable(HttpRequestException ex, Uri uri)
    {
        var host = uri.Host;
        return Connection(ex) switch
        {
            Failure.HostNotFound => new($"Cannot find the host {host}: check your network connection or proxy, and the update site's address.", UpdateProblem.Offline, ex),
            Failure.Tls => new($"No secure connection to {host} could be made: check the system's date and time, or a proxy or antivirus that intercepts HTTPS.", UpdateProblem.Tls, ex),
            Failure.Timeout => TimedOut(host, null, ex),
            Failure.Connection => new($"Cannot connect to {host}: check your network connection or proxy.", UpdateProblem.Offline, ex),
            _ => new($"The request to {host} failed: {ex.Message}", UpdateProblem.Other, ex)
        };
    }

    /// <summary>A host that did not respond within <paramref name="after"/>, or within its timeout when null.</summary>
    public static UpdateHttpException TimedOut(string host, TimeSpan? after, Exception? inner = null) =>
        new(after is { } time ? $"{host} did not respond within {time.TotalSeconds:0} second{(Math.Round(time.TotalSeconds) == 1 ? "" : "s")}: check your network connection or proxy."
            : $"{host} did not respond in time: check your network connection or proxy.", UpdateProblem.Timeout, inner);

    private enum Failure { Unknown, HostNotFound, Connection, Tls, Timeout }

    private static Failure Connection(HttpRequestException ex)
    {
        switch (ex.HttpRequestError)
        {
            case HttpRequestError.NameResolutionError: return Failure.HostNotFound;
            case HttpRequestError.SecureConnectionError: return Failure.Tls;
            case HttpRequestError.ProxyTunnelError: return Failure.Connection;
        }
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException) return Failure.Tls;
            if (inner is SocketException socket)
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => Failure.HostNotFound,
                    SocketError.TimedOut => Failure.Timeout,
                    _ => Failure.Connection
                };
        }
        return ex.HttpRequestError == HttpRequestError.ConnectionError ? Failure.Connection : Failure.Unknown;
    }

    /// <summary>
    /// The error of an update site that answered with <paramref name="status"/>: its host, and for a rate limit when to try
    /// again; for 401, 403 and 404, and for a rate limit, which token raises it.
    /// </summary>
    /// <param name="tokenSent">A token was sent with the request.</param>
    public static UpdateHttpException Status(Uri uri, HttpStatusCode status, bool rateLimited, DateTimeOffset? reset, bool tokenSent,
        DateTimeOffset now, Func<string, string?> environment)
    {
        var host = uri.Host;
        var code = (int)status;
        if (rateLimited)
        {
            var when = reset is { } time ? "try again after " + LocalTime(time, now, CultureInfo.CurrentCulture) : "try again later";
            var hint = tokenSent ? "" : ForgeTokens.Hint(uri, false, environment) is { } token ? " For a higher limit: " + token : "";
            return new($"The rate limit of {host} was reached; {when}.{hint}", UpdateProblem.RateLimited);
        }
        if (code is 401 or 403 or 404)
        {
            var what = code == 404 ? $"{host} found no such repository or release (HTTP 404): check the update site's address."
                : $"{host} refused access (HTTP {code}).";
            var hint = ForgeTokens.Hint(uri, tokenSent, environment);
            return new(what + (hint is null ? "" : tokenSent ? " " + hint : " A private repository needs a token: " + hint), UpdateProblem.Access);
        }
        return new(code >= 500 ? $"{host} failed with HTTP {code}; try again later." : $"{host} answered with HTTP {code}.", UpdateProblem.Http);
    }

    /// <summary>
    /// When a rate limit ends: <c>X-RateLimit-Reset</c> or GitLab's <c>RateLimit-Reset</c> in Unix seconds (a small number
    /// is a delay in seconds, as other servers send it), else <c>Retry-After</c> as a delay or a date; null when none says.
    /// </summary>
    public static DateTimeOffset? RateLimitReset(string? reset, RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        var first = reset?.Split(',')[0].Trim();
        if (long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            if (value < 1_000_000_000) return now.AddSeconds(value);
            if (value <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        if (retryAfter?.Delta is { } delta) return now + delta;
        return retryAfter?.Date;
    }

    /// <summary><paramref name="time"/> in the local time zone: its time of day today, otherwise with the date.</summary>
    public static string LocalTime(DateTimeOffset time, DateTimeOffset now, CultureInfo culture)
    {
        var local = time.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? local.ToString("t", culture) : local.ToString("g", culture);
    }

    /// <summary>
    /// What git's error output tells the user to do: credentials for a private repository, which git cannot ask for, or the
    /// network; empty when it says nothing known.
    /// </summary>
    public static (string Hint, UpdateProblem Problem) GitHint(string error)
    {
        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("terminal prompts disabled") || Has("Authentication failed") || Has("could not read Username") ||
            Has("could not read Password") || Has("Permission denied (publickey") || Has("Host key verification failed") ||
            Has("Repository not found"))
            return (" Check the repository's address. A private repository needs credentials that git cannot ask for here: set up a git " +
                    "credential helper or an SSH key that needs no passphrase prompt (ssh runs in batch mode), then restart the editor.",
                UpdateProblem.GitAuthentication);
        if (Has("Could not resolve host") || Has("Could not resolve hostname") || Has("unable to access") && Has("Failed to connect") ||
            Has("Network is unreachable") || Has("Connection timed out") || Has("Connection refused"))
            return (" Check your network connection or proxy.", UpdateProblem.Offline);
        return ("", UpdateProblem.Other);
    }

    /// <summary>The last non-empty lines of git's error output, which say why it failed; packet traces are left out.</summary>
    public static string GitError(string error)
    {
        var lines = error.Split('\n').Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.Contains("packet:", StringComparison.Ordinal) && !l.StartsWith("trace:", StringComparison.Ordinal)).ToArray();
        return lines.Length == 0 ? "no error output" : string.Join(" ", lines.TakeLast(3));
    }
}
#endif
