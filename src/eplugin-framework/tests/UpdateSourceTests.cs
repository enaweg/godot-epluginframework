using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateSourceTests
{
    [TestCase]
    public void GitUrlRejectsUnsafeTransportsAndPaths()
    {
        foreach (var url in new[] { "https://github.com/a/b.git?path=addons/plugin#main", "ssh://git@host/project.git#v1.0", "git@host:owner/repo.git?path=plugin" })
            Assertions.AssertBool(GitUrl.TryParse(url, out _)).IsTrue();
        foreach (var url in new[] { "file:///tmp/repo", "ext::evil", "-remote", "https://host/repo.git?path=../evil", "https://host/repo.git?path=/evil", "https://host/repo.git?path=a%0Ab", "https://host/repo.git#-option" })
            Assertions.AssertBool(GitUrl.TryParse(url, out _)).IsFalse();
    }

    [TestCase]
    public async Task GitHubChoosesHighestStableAndPluginAsset()
    {
        using var client = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[{"name":"plugin.zip","browser_download_url":"https://example.org/plugin.zip"}]},
             {"tag_name":"2.0.0-beta.1","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"3.0.0","draft":true,"assets":[]}]
            """)));
        var source = new GitHubReleaseSource(new(client), "owner", "repo");
        var result = await source.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(result!.NewVersion).IsEqual("1.2.0");
        Assertions.AssertString(((ZipPackageRef)result.Package).Url).IsEqual("https://example.org/plugin.zip");
        Assertions.AssertObject(await new GitHubReleaseSource(new(client), "owner", "repo", true).CheckAsync(Target(), new(), CancellationToken.None)).IsNull();
    }

    [TestCase]
    public async Task GitLabSupportsSubgroupsAndSourceArchives()
    {
        string? requested = null;
        using var client = new HttpClient(new Handler(request => { requested = request.RequestUri!.AbsoluteUri; return Json("""
            [{"tag_name":"1.3.0","upcoming_release":false,"assets":{"links":[],"sources":[{"format":"zip","url":"https://git.example/archive.zip"}]}}]
            """); }));
        var result = await new GitLabReleaseSource(new(client), "git.example", "team/sub/project").CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertBool(requested!.Contains("team%2Fsub%2Fproject", StringComparison.OrdinalIgnoreCase)).IsTrue();
        Assertions.AssertString(result!.NewVersion).IsEqual("1.3.0");
    }

    [TestCase]
    public async Task RedirectDropsCredentialsAndRefusesHttp()
    {
        var calls = 0;
        var leaked = false;
        using var client = new HttpClient(new Handler(request =>
        {
            if (++calls == 1) return Redirect("https://other.example/archive");
            leaked = request.Headers.Authorization is not null || request.Headers.Contains("PRIVATE-TOKEN");
            return Json("{}");
        }));
        using var response = await new UpdateHttp(client).SendAsync(new("https://origin.example/archive"), HttpMethod.Get, CancellationToken.None, "test-token");
        Assertions.AssertBool(leaked).IsFalse();
        using var insecure = new HttpClient(new Handler(_ => Redirect("http://other.example/archive")));
        var refused = false;
        try { using var unused = await new UpdateHttp(insecure).SendAsync(new("https://origin.example/archive"), HttpMethod.Get, CancellationToken.None); }
        catch (IOException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();
    }

    [TestCase]
    public async Task RateLimitFallsBackToLatestRedirect()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                limited.Headers.Add("X-RateLimit-Remaining", "0");
                return limited;
            }
            return request.RequestUri.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)
                ? Redirect("https://github.com/owner/repo/releases/tag/v1.4.0") : Json("{}");
        }));
        var candidate = await new GitHubReleaseSource(new(client), "owner", "repo").CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(candidate!.NewVersion).IsEqual("1.4.0");
    }

    [TestCase]
    public async Task BranchCheckUsesRawMetadataAndTagsArePinned()
    {
        using var client = new HttpClient(new Handler(_ => Json("[plugin]\nversion=\"2.0.0\"\n")));
        var git = new FakeGit();
        var branch = new GitSource(new(client), git, new("https://github.com/owner/repo.git", "addon", "main"));
        var candidate = await branch.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(candidate!.ResolvedRevision!).IsEqual(FakeGit.Commit);
        Assertions.AssertBool(git.Calls.Any(c => c.Contains("fetch"))).IsFalse();
        var pinned = new GitSource(new(client), git, new("https://github.com/owner/repo.git", "addon", "v2.0.0"));
        Assertions.AssertObject(await pinned.CheckAsync(Target(), new(), CancellationToken.None)).IsNull();
    }

    [TestCase]
    public async Task SparseFetchLimitsCheckoutAndCleansWorkingTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var git = new FakeGit();
            var source = new GitSource(new(), git, new("https://host/repo.git", "addon", null));
            await source.FetchAsync(new("https://host/repo.git", "addon", FakeGit.Commit), Path.Combine(root, "staged"), CancellationToken.None);
            Assertions.AssertBool(git.Calls.Any(c => c.Contains("--filter=blob:none") && c.Contains("--depth"))).IsTrue();
            Assertions.AssertBool(git.Calls.Any(c => c.Contains("--no-cone") && c.Contains("/addon/"))).IsTrue();
            Assertions.AssertBool(File.Exists(Path.Combine(root, "staged/plugin.cfg"))).IsTrue();
            Assertions.AssertInt(Directory.GetDirectories(root, "git-work-*").Length).IsEqual(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public async Task SourcesListEveryPublishedVersion()
    {
        using var github = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[{"name":"plugin.zip","browser_download_url":"https://example.org/1.2.zip"}]},
             {"tag_name":"v1.1.0","draft":false,"prerelease":false,"assets":[]},
             {"tag_name":"2.0.0-beta.1","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"3.0.0","draft":true,"assets":[]}]
            """)));
        var listed = await new GitHubReleaseSource(new(github), "owner", "repo").ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "1.2.0", "1.1.0" });
        Assertions.AssertString(((ZipPackageRef)listed[1].Package).Url).IsEqual("https://github.com/owner/repo/archive/refs/tags/v1.1.0.zip");
        Assertions.AssertInt((await new GitHubReleaseSource(new(github), "owner", "repo").ListAsync(Target(), new(AllowPrerelease: true), CancellationToken.None)).Count).IsEqual(3);

        using var gitlab = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"1.3.0","upcoming_release":false,"assets":{"links":[],"sources":[{"format":"zip","url":"https://git.example/1.3.zip"}]}},
             {"tag_name":"1.2.0","upcoming_release":false,"assets":{"links":[],"sources":[]}}]
            """)));
        var gitlabVersions = await new GitLabReleaseSource(new(gitlab), "git.example", "team/project").ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(gitlabVersions.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "1.3.0" });

        var tags = await new GitSource(new(), new FakeGit(), new("https://host/repo.git", "addon", null)).ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(tags.Single().NewVersion).IsEqual("2.0.0");
        Assertions.AssertString(((GitPackageRef)tags[0].Package).Commit).IsEqual(FakeGit.Commit);
        Assertions.AssertInt((await new GitSource(new(), new FakeGit(), new("https://host/repo.git", "addon", "main")).ListAsync(Target(), new(), CancellationToken.None)).Count).IsEqual(0);
    }

    private static PluginUpdateTarget Target() => new("plugin", "Plugin", "1.0.0", "https://github.com/owner/repo/releases", "/unused");
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new(url);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }
    private sealed class FakeGit : IGitRunner
    {
        public const string Commit = "0123456789012345678901234567890123456789";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<GitOutcome> RunAsync(string directory, IReadOnlyList<string> args, CancellationToken ct, bool trace = false)
        {
            Calls.Add(args);
            if (args.Contains("checkout"))
            {
                Directory.CreateDirectory(Path.Combine(directory, "addon"));
                File.WriteAllText(Path.Combine(directory, "addon/plugin.cfg"), "[plugin]\nversion=\"2.0.0\"\n");
            }
            return Task.FromResult(new GitOutcome(0, args.Contains("--version") ? "git version 2.56.0" : args.Contains("ls-remote") ? $"{Commit}\trefs/heads/main\n{Commit}\trefs/tags/v2.0.0\n" : "", trace ? "fetch=shallow filter" : ""));
        }
    }
}
