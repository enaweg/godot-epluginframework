#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// What a failed update request tells the user: the host, why it failed and what to do, from HTTP status, rate limit
/// headers, connection failures and git's error output. No network is used.
/// </summary>
[TestSuite]
public class UpdateErrorsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [TestCase]
    public static void ARateLimitEndsAtTheTimeItsHeadersName()
    {
        Assertions.AssertObject(UpdateErrors.RateLimitReset("1793190000", null, Now)).IsEqual(DateTimeOffset.FromUnixTimeSeconds(1793190000));
        // a small number is a delay, as some servers send RateLimit-Reset
        Assertions.AssertObject(UpdateErrors.RateLimitReset("60", null, Now)).IsEqual(Now.AddSeconds(60));
        Assertions.AssertObject(UpdateErrors.RateLimitReset(null, new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Now)).IsEqual(Now.AddSeconds(30));
        Assertions.AssertObject(UpdateErrors.RateLimitReset(null, new RetryConditionHeaderValue(Now.AddMinutes(5)), Now)).IsEqual(Now.AddMinutes(5));
        Assertions.AssertObject(UpdateErrors.RateLimitReset("soon", null, Now)).IsNull();
        Assertions.AssertObject(UpdateErrors.RateLimitReset(null, null, Now)).IsNull();

        // shown in local time, with the date when it is not today
        var culture = CultureInfo.InvariantCulture;
        Assertions.AssertString(UpdateErrors.LocalTime(Now.AddMinutes(5), Now, culture)).IsEqual(Now.AddMinutes(5).ToLocalTime().ToString("t", culture));
        Assertions.AssertString(UpdateErrors.LocalTime(Now.AddDays(3), Now, culture)).IsEqual(Now.AddDays(3).ToLocalTime().ToString("g", culture));
    }

    [TestCase]
    public static async Task ARateLimitNamesTheHostWhenToTryAgainAndTheToken()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(20);
        var http = Http(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var error = await Fails(() => http.ReadTextAsync("https://api.github.com/repos/owner/repo/releases", CancellationToken.None));

        Assertions.AssertBool(error.RateLimited).IsTrue();
        Assertions.AssertString(error.Message).StartsWith("The rate limit of api.github.com was reached; try again after ")
            .Contains(UpdateErrors.LocalTime(DateTimeOffset.FromUnixTimeSeconds(reset.ToUnixTimeSeconds()), DateTimeOffset.UtcNow, CultureInfo.CurrentCulture))
            .Contains("set EPLUGIN_GITHUB_TOKEN (or GITHUB_TOKEN) in the editor's environment");

        // 429 with Retry-After, and GitHub's secondary limit: 403 with Retry-After
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Forbidden })
        {
            var retry = Http(_ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return response;
            });
            var limited = await Fails(() => retry.ReadTextAsync("https://codeberg.org/api/v1/repos/owner/repo/releases", CancellationToken.None));
            Assertions.AssertObject(limited.Problem).IsEqual(UpdateProblem.RateLimited);
            Assertions.AssertString(limited.Message).Contains("codeberg.org").Contains("EPLUGIN_FORGEJO_TOKEN");
        }
    }

    [TestCase]
    public static async Task RefusedAccessNamesTheHostAndThatAPrivateRepositoryNeedsAToken()
    {
        var missing = await Fails(() => Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound))
            .ReadTextAsync("https://api.github.com/repos/owner/private/releases", CancellationToken.None));
        Assertions.AssertObject(missing.Problem).IsEqual(UpdateProblem.Access);
        Assertions.AssertString(missing.Message).StartsWith("api.github.com found no such repository or release (HTTP 404)")
            .Contains("A private repository needs a token: set EPLUGIN_GITHUB_TOKEN (or GITHUB_TOKEN)");

        // with a token sent, the token is what to check
        var refused = await Fails(() => Http(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
            .ReadTextAsync("https://api.github.com/repos/owner/private/releases", CancellationToken.None, "token"));
        Assertions.AssertString(refused.Message).IsEqual("api.github.com refused access (HTTP 401). Check that the token in EPLUGIN_GITHUB_TOKEN " +
                                                         "(or GITHUB_TOKEN) can read this repository and has not expired.");

        var failing = await Fails(() => Http(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))
            .ReadTextAsync("https://example.org/releases", CancellationToken.None));
        Assertions.AssertObject(failing.Problem).IsEqual(UpdateProblem.Http);
        Assertions.AssertString(failing.Message).IsEqual("example.org failed with HTTP 502; try again later.");
    }

    [TestCase]
    public static void ASelfHostedForgeIsNamedWithTheVariableThatTrustsIt()
    {
        string? None(string _) => null;
        Assertions.AssertString(ForgeTokens.Hint(new Uri("https://git.example.org/api/v4/projects/a%2Fb/releases"), false, None))
            .IsEqual("set EPLUGIN_GITLAB_TOKEN (or GITLAB_TOKEN) and add git.example.org to EPLUGIN_GITLAB_HOSTS in the editor's environment, then restart the editor.");
        string? Trusted(string name) => name == "EPLUGIN_FORGEJO_HOSTS" ? "other.org, git.example.org" : null;
        Assertions.AssertString(ForgeTokens.Hint(new Uri("https://git.example.org/api/v1/repos/a/b/releases"), false, Trusted))
            .IsEqual("set EPLUGIN_FORGEJO_TOKEN (or FORGEJO_TOKEN, GITEA_TOKEN) in the editor's environment, then restart the editor.");
        // no forge, no token to name
        Assertions.AssertObject(ForgeTokens.Hint(new Uri("https://example.org/plugin.zip"), false, None)).IsNull();
    }

    [TestCase]
    public static async Task AConnectionThatFailsNamesTheHostAndWhatToCheck()
    {
        async Task<UpdateHttpException> Unreachable(Exception thrown) =>
            await Fails(() => Http(_ => throw thrown).ReadTextAsync("https://github.com/owner/repo/releases", CancellationToken.None));

        var dns = await Unreachable(new HttpRequestException(HttpRequestError.NameResolutionError, "Name or service not known"));
        Assertions.AssertObject(dns.Problem).IsEqual(UpdateProblem.Offline);
        Assertions.AssertString(dns.Message).StartsWith("Cannot find the host github.com: check your network connection or proxy");

        var refused = await Unreachable(new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
        Assertions.AssertObject(refused.Problem).IsEqual(UpdateProblem.Offline);
        Assertions.AssertString(refused.Message).IsEqual("Cannot connect to github.com: check your network connection or proxy.");

        var tls = await Unreachable(new HttpRequestException("SSL failed", new AuthenticationException("The remote certificate is invalid")));
        Assertions.AssertObject(tls.Problem).IsEqual(UpdateProblem.Tls);
        Assertions.AssertString(tls.Message).Contains("github.com").Contains("date and time");

        // HttpClient's own timeout, not the caller's cancellation
        using var client = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }))
        { Timeout = TimeSpan.FromMilliseconds(100) };
        var slow = await Fails(() => new UpdateHttp(client).ReadTextAsync("https://example.org/releases", CancellationToken.None));
        Assertions.AssertObject(slow.Problem).IsEqual(UpdateProblem.Timeout);
        Assertions.AssertString(slow.Message).StartsWith("example.org did not respond within").Contains("check your network connection or proxy");
    }

    [TestCase]
    public static void GitsErrorOutputTellsWhetherCredentialsOrTheNetworkAreMissing()
    {
        var (credentials, auth) = UpdateErrors.GitHint("fatal: could not read Username for 'https://github.com': terminal prompts disabled");
        Assertions.AssertObject(auth).IsEqual(UpdateProblem.GitAuthentication);
        Assertions.AssertString(credentials).Contains("credential helper").Contains("SSH key");
        Assertions.AssertObject(UpdateErrors.GitHint("git@host: Permission denied (publickey).").Problem).IsEqual(UpdateProblem.GitAuthentication);
        Assertions.AssertObject(UpdateErrors.GitHint("fatal: unable to access 'https://x/': Could not resolve host: x").Problem).IsEqual(UpdateProblem.Offline);
        Assertions.AssertObject(UpdateErrors.GitHint("fatal: bad object").Problem).IsEqual(UpdateProblem.Other);
        // packet traces of a capability check are left out
        Assertions.AssertString(UpdateErrors.GitError("12:00 packet: git< version 2\nfatal: bad object\n")).IsEqual("fatal: bad object");
    }

    [TestCase]
    public static async Task AGitSourceThatNeedsCredentialsSaysSo()
    {
        var git = new FailingGit("fatal: could not read Username for 'https://example.org': terminal prompts disabled");
        Assertions.AssertBool(GitUrl.TryParse("https://example.org/owner/private.git", out var url)).IsTrue();
        var source = new GitSource(new UpdateHttp(), git, url!);

        UpdateProblemException? error = null;
        try { await source.CheckAsync(new PluginUpdateTarget("plugin", "Plugin", "1.0.0", url!.Repository, "/unused"), new(), CancellationToken.None); }
        catch (UpdateProblemException ex) { error = ex; }

        Assertions.AssertObject(error).IsNotNull();
        Assertions.AssertObject(error!.Problem).IsEqual(UpdateProblem.GitAuthentication);
        Assertions.AssertString(error.Message).StartsWith("git ls-remote failed: fatal: could not read Username").Contains("credential helper");

        Assertions.AssertString(GitRunner.Command(["fetch", "--depth", "1", "origin"])).IsEqual("fetch");
        Assertions.AssertString(GitRunner.Command(["--version"])).IsEqual("--version");
    }

    [TestCase]
    public static async Task WhenEverySiteFailsTheSameWayTheProblemIsKept()
    {
        var service = new UpdateService(new Sites(), new SystemClock(), new MemoryStore());
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://offline.example/b/releases", "/unused",
            OverrideUrl: "https://offline.example/a/releases", KnownUrl: "https://offline.example/c/releases");

        var offline = await service.CheckAsync([target], new(), CancellationToken.None);
        Assertions.AssertObject(offline.Failures[0].Problem).IsEqual(UpdateProblem.Offline);

        // a later site that works is named as the one used
        var fallback = await service.CheckAsync([target with { KnownUrl = "https://working.example/c/releases" }], new(), CancellationToken.None);
        Assertions.AssertInt(fallback.Failures.Count).IsEqual(0);
        Assertions.AssertObject(fallback.Fallbacks![0].UsedSite).IsEqual(UpdateSite.BuiltIn);
    }

    [TestCase]
    public static async Task AGitLabReleaseWithoutAPackageIsLeftOutOfTheVersionListAndLogged()
    {
        const string releases = """
            [{"tag_name":"v2.0.0","name":"v2.0.0","assets":{"links":[],"sources":[]}},
             {"tag_name":"v1.5.0","name":"v1.5.0","assets":{"links":[],"sources":[{"format":"zip","url":"https://gitlab.com/group/plugin/-/archive/v1.5.0/plugin-v1.5.0.zip"}]}}]
            """;
        var logged = new List<string>();
        var factory = new UpdateSourceFactory(Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases) }), log: logged.Add);
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://gitlab.com/group/plugin/-/releases", "/unused");

        var listed = await ((IVersionListSource)factory.Create(target.UpdateUrl!)).ListAsync(target, new(), CancellationToken.None);

        Assertions.AssertThat(listed.Select(c => c.ResolvedRevision).ToArray()).ContainsExactly("v1.5.0");
        Assertions.AssertInt(logged.Count).IsEqual(1);
        Assertions.AssertString(logged[0]).StartsWith("Release v2.0.0 of gitlab.com/group/plugin is not listed for plugin");
    }

    private static UpdateHttp Http(Func<HttpRequestMessage, HttpResponseMessage> handle) =>
        new(new HttpClient(new Handler((request, _) => Task.FromResult(handle(request)))));

    private static async Task<UpdateHttpException> Fails(Func<Task> request)
    {
        try { await request(); }
        catch (UpdateHttpException ex) { return ex; }
        throw new InvalidOperationException("The request did not fail.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
    }

    private sealed class FailingGit(string error) : IGitRunner
    {
        public Task<GitOutcome> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken ct, bool trace = false) =>
            Task.FromResult(arguments.Contains("--version") ? new GitOutcome(0, "git version 2.45.0", "") : new GitOutcome(128, "", error));
    }

    private sealed class Sites : IUpdateSourceFactory
    {
        public IUpdateSource Create(string url) => new Site(url);
    }

    private sealed class Site(string url) : IUpdateSource
    {
        public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
            url.Contains("offline", StringComparison.Ordinal)
                ? Task.FromException<UpdateCandidate?>(new UpdateHttpException("Cannot connect to offline.example.", UpdateProblem.Offline))
                : Task.FromResult<UpdateCandidate?>(new(target.Slug, target.Name, target.InstalledVersion, "2.0.0", url, null, null,
                    new ZipPackageRef("https://working.example/plugin.zip", target.Slug)));
    }

    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateCache State { get; } = new();
        public void Save() { }
    }
}
#endif
