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
        Assertions.AssertString(result!.NewVersion).IsEqual("v1.2.0");
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
        Assertions.AssertString(candidate!.NewVersion).IsEqual("v1.4.0");
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
            await source.FetchAsync(new("https://host/repo.git", "addon", FakeGit.Commit), "plugin", Path.Combine(root, "staged"), CancellationToken.None);
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
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "v1.2.0", "v1.1.0" });
        Assertions.AssertString(((ZipPackageRef)listed[1].Package).Url).IsEqual("https://github.com/owner/repo/archive/refs/tags/v1.1.0.zip");
        Assertions.AssertInt((await new GitHubReleaseSource(new(github), "owner", "repo").ListAsync(Target(), new(AllowPrerelease: true), CancellationToken.None)).Count).IsEqual(3);

        using var gitlab = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"1.3.0","upcoming_release":false,"assets":{"links":[],"sources":[{"format":"zip","url":"https://git.example/1.3.zip"}]}},
             {"tag_name":"1.2.0","upcoming_release":false,"assets":{"links":[],"sources":[]}}]
            """)));
        var gitlabVersions = await new GitLabReleaseSource(new(gitlab), "git.example", "team/project").ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(gitlabVersions.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "1.3.0" });

        var tags = await new GitSource(new(), new FakeGit(), new("https://host/repo.git", "addon", null)).ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(tags.Single().NewVersion).IsEqual("v2.0.0");
        Assertions.AssertString(((GitPackageRef)tags[0].Package).Commit).IsEqual(FakeGit.Commit);
    }

    [TestCase]
    public async Task PinnedGitSourcesListTheCommitTheyPointToNow()
    {
        using var client = new HttpClient(new Handler(_ => Json("[plugin]\nversion=\"1.0.0\"\n")));
        // A branch, tag or commit is listed even when its plugin.cfg version is the installed one, so it can be reinstalled.
        foreach (var reference in new[] { "main", "v2.0.0", FakeGit.Commit })
        {
            var listed = await new GitSource(new(client), new FakeGit(), new("https://github.com/owner/repo.git", "addon", reference))
                .ListAsync(Target(), new(), CancellationToken.None);
            Assertions.AssertString(listed.Single().NewVersion).IsEqual("1.0.0");
            Assertions.AssertString(((GitPackageRef)listed[0].Package).Commit).IsEqual(FakeGit.Commit);
        }
        var unknown = new GitSource(new(client), new FakeGit(), new("https://github.com/owner/repo.git", "addon", "missing"));
        try { await unknown.ListAsync(Target(), new(), CancellationToken.None); throw new InvalidOperationException("Listing an unknown ref succeeded."); }
        catch (IOException) { }
    }

    [TestCase]
    public void TagPagesNameTheirRepository()
    {
        foreach (var (page, repository, path) in new[]
                 {
                     ("https://github.com/sn1ks0h/Global-Asset-Manager/tags", "https://github.com/sn1ks0h/Global-Asset-Manager.git", ""),
                     ("https://gitlab.example/group/sub/project/-/tags/", "https://gitlab.example/group/sub/project.git", ""),
                     ("https://codeberg.org/owner/repo/tags?path=addons/my_plugin", "https://codeberg.org/owner/repo.git", "addons/my_plugin"),
                 })
        {
            Assertions.AssertBool(GitUrl.TryParse(page, out var url)).IsTrue();
            Assertions.AssertString(url!.Repository).IsEqual(repository);
            Assertions.AssertString(url.Path).IsEqual(path);
            Assertions.AssertObject(url.Ref).IsNull();
            Assertions.AssertBool(new UpdateSourceFactory().Create(page) is GitSource).IsTrue();
        }
        foreach (var url in new[] { "http://github.com/owner/repo/tags", "https://host/owner/tags", "https://host/-/tags", "https://host/owner/repo/-/-/tags" })
            Assertions.AssertBool(UpdateSourceFactory.IsSupported(url)).IsFalse();
        // releases stay release sources
        Assertions.AssertBool(new UpdateSourceFactory().Create("https://github.com/owner/repo/releases") is GitHubReleaseSource).IsTrue();
    }

    [TestCase]
    public async Task VersionTagsAreTheVersionsOfARepository()
    {
        string Commit(char c) => new(c, 40);
        var refs = string.Join('\n',
            $"{Commit('1')}\trefs/heads/main",
            $"{Commit('2')}\trefs/tags/1.2",
            $"{Commit('3')}\trefs/tags/1.3.0",
            $"{Commit('4')}\trefs/tags/release-2",
            $"{Commit('5')}\trefs/tags/v1.2.3",
            $"{Commit('6')}\trefs/tags/v1.3",
            $"{Commit('7')}\trefs/tags/v1.4",
            $"{Commit('8')}\trefs/tags/v1.4^{{}}",
            $"{Commit('9')}\trefs/tags/v2.0.0-beta.1");
        var git = new FakeGit("addons/plugin", refs);
        var source = (GitSource)new UpdateSourceFactory(new(), git).Create("https://github.com/owner/repo/tags");
        var listed = await source.ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "v1.4", "1.3.0", "v1.2.3", "1.2" });
        // an annotated tag installs the commit it points to, not the tag object
        Assertions.AssertString(((GitPackageRef)listed[0].Package).Commit).IsEqual(Commit('8'));
        Assertions.AssertString(((GitPackageRef)listed[0].Package).Repository).IsEqual("https://github.com/owner/repo.git");
        Assertions.AssertString(((GitPackageRef)listed[^1].Package).Commit).IsEqual(Commit('2'));
        Assertions.AssertInt((await source.ListAsync(Target(), new(AllowPrerelease: true), CancellationToken.None)).Count).IsEqual(5);

        // the newest tag is the update; the tag name announces it, so nothing is fetched
        var update = await source.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(update!.NewVersion).IsEqual("v1.4");
        Assertions.AssertBool(git.Calls.Any(c => c.Contains("fetch") || c.Contains("show"))).IsFalse();
    }

    [TestCase]
    public async Task RepositoryWithoutPathFindsThePluginFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = new GitPackageRef("https://github.com/owner/repo.git", "", FakeGit.Commit);
            var git = new FakeGit("addons/plugin");
            var source = new GitSource(new(), git, new("https://github.com/owner/repo.git", "", null));
            await source.FetchAsync(package, "plugin", Path.Combine(root, "plugin"), CancellationToken.None);
            Assertions.AssertBool(git.Calls.Any(c => c.Contains("--no-cone") && c.Contains("/addons/plugin/"))).IsTrue();
            Assertions.AssertBool(File.Exists(Path.Combine(root, "plugin/plugin.cfg"))).IsTrue();
            Assertions.AssertBool(File.Exists(Path.Combine(root, "plugin/project.godot"))).IsFalse();

            // a plugin at the repository root is still copied from there
            var top = new GitSource(new(), new FakeGit(""), new("https://github.com/owner/repo.git", "", null));
            await top.FetchAsync(package, "plugin", Path.Combine(root, "top"), CancellationToken.None);
            Assertions.AssertBool(File.Exists(Path.Combine(root, "top/plugin.cfg"))).IsTrue();

            // of several plugins, such as a project with samples, only the one named like the slug is installed
            var several = new FakeGit("src/project/addons/plugin",
                tree: "project.godot\0src/project/addons/other/plugin.cfg\0src/project/addons/plugin/plugin.cfg\0src/project/addons/plugin/sub/plugin.cfg\0samples/plugin/x/plugin.cfg\0");
            await new GitSource(new(), several, new("https://github.com/owner/repo.git", "", null))
                .FetchAsync(package, "plugin", Path.Combine(root, "several"), CancellationToken.None);
            Assertions.AssertBool(several.Calls.Any(c => c.Contains("--no-cone") && c.Contains("/src/project/addons/plugin/"))).IsTrue();
            Assertions.AssertBool(File.Exists(Path.Combine(root, "several/plugin.cfg"))).IsTrue();

            // no folder named like the slug, or several equally deep ones, need ?path=
            foreach (var (fake, slug) in new[]
                     {
                         (new FakeGit("addons/other"), "plugin"),
                         (new FakeGit("addons/plugin", tree: "addons/other/plugin.cfg\0addons/third/plugin.cfg\0"), "plugin"),
                         (new FakeGit("addons/plugin", tree: "a/plugin/plugin.cfg\0b/plugin/plugin.cfg\0"), "plugin"),
                     })
            {
                var refused = new GitSource(new(), fake, new("https://github.com/owner/repo.git", "", null));
                try
                {
                    await refused.FetchAsync(package, slug, Path.Combine(root, "refused"), CancellationToken.None);
                    throw new InvalidOperationException("An ambiguous repository was installed.");
                }
                catch (InvalidDataException ex) { Assertions.AssertString(ex.Message).Contains("?path="); }
            }

            // without a version tag, the branch's plugin.cfg is read from the plugin folder
            var branch = new FakeGit("addons/plugin", $"{FakeGit.Commit}\trefs/heads/main\n", version: "1.0.0");
            var head = await new GitSource(new(), branch, new("https://github.com/owner/repo.git", "", "main")).ListAsync(Target(), new(), CancellationToken.None);
            Assertions.AssertString(head.Single().NewVersion).IsEqual("1.0.0");
            Assertions.AssertBool(branch.Calls.Any(c => c.Contains("FETCH_HEAD:addons/plugin/plugin.cfg"))).IsTrue();
        }
        finally { Directory.Delete(root, true); }
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
    /// <summary>A repository with one plugin in <paramref name="folder"/> ("" at its root) next to a project.godot.</summary>
    private sealed class FakeGit(string folder = "addon", string? refs = null, string? tree = null, string version = "2.0.0") : IGitRunner
    {
        public const string Commit = "0123456789012345678901234567890123456789";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<GitOutcome> RunAsync(string directory, IReadOnlyList<string> args, CancellationToken ct, bool trace = false)
        {
            Calls.Add(args);
            var config = $"[plugin]\nversion=\"{version}\"\n";
            var prefix = folder.Length == 0 ? "" : folder + "/";
            if (args.Contains("checkout"))
            {
                // the sparse checkout leaves out what is outside the plugin folder; a copy of the root would still see this
                if (folder.Length > 0) File.WriteAllText(Path.Combine(directory, "project.godot"), "");
                Directory.CreateDirectory(Path.Combine(directory, folder));
                File.WriteAllText(Path.Combine(directory, prefix + "plugin.cfg"), config);
            }
            var output = args.Contains("--version") ? "git version 2.56.0"
                : args.Contains("ls-remote") ? refs ?? $"{Commit}\trefs/heads/main\n{Commit}\trefs/tags/v2.0.0\n"
                : args.Contains("ls-tree") ? tree ?? $"project.godot\0{prefix}plugin.cfg\0{prefix}plugin.gd\0"
                : args.Contains("show") ? config : "";
            return Task.FromResult(new GitOutcome(0, output, trace ? "fetch=shallow filter" : ""));
        }
    }
}
