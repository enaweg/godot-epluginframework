#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    public static void GitUrlRejectsUnsafeTransportsAndPaths()
    {
        foreach (var url in new[] { "https://github.com/a/b.git?path=addons/plugin#main", "ssh://git@host/project.git#v1.0", "git@host:owner/repo.git?path=plugin" })
            Assertions.AssertBool(GitUrl.TryParse(url, out _)).IsTrue();
        foreach (var url in new[] { "file:///tmp/repo", "ext::evil", "-remote", "https://host/repo.git?path=../evil", "https://host/repo.git?path=/evil", "https://host/repo.git?path=a%0Ab", "https://host/repo.git#-option" })
            Assertions.AssertBool(GitUrl.TryParse(url, out _)).IsFalse();
    }

    [TestCase]
    public static async Task GitHubChoosesNextBetterVersionAndPluginAsset()
    {
        // A stable install is offered the highest stable release, a prerelease install the highest release of any kind.
        // Drafts are never offered.
        using var client = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[{"name":"plugin.zip","browser_download_url":"https://example.org/plugin.zip"}]},
             {"tag_name":"2.0.0-beta.1","draft":false,"prerelease":true,"assets":[{"name":"plugin.zip","browser_download_url":"https://example.org/beta.zip"}]},
             {"tag_name":"3.0.0","draft":true,"assets":[]}]
            """)));
        var source = new GitHubReleaseSource(new(client), "owner", "repo");
        var result = await source.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(result!.NewVersion).IsEqual("v1.2.0");
        Assertions.AssertString(((ZipPackageRef)result.Package).Url).IsEqual("https://example.org/plugin.zip");
        var beta = await source.CheckAsync(Target() with { InstalledVersion = "1.0.0-beta.1" }, new(), CancellationToken.None);
        Assertions.AssertString(beta!.NewVersion).IsEqual("2.0.0-beta.1");
        Assertions.AssertString(((ZipPackageRef)beta.Package).Url).IsEqual("https://example.org/beta.zip");
        Assertions.AssertObject(await new GitHubReleaseSource(new(client), "owner", "repo", true).CheckAsync(Target(), new(), CancellationToken.None)).IsNull();
    }

    [TestCase]
    public static async Task ReleasePagesKeepGitInsideRepositoryNames()
    {
        // Only a trailing ".git" is no part of the name.
        var requested = new List<string>();
        using var client = new HttpClient(new Handler(request => { requested.Add(request.RequestUri!.AbsoluteUri); return Json("[]"); }));
        var factory = new UpdateSourceFactory(new(client));
        foreach (var url in new[] { "https://github.com/owner/foo.github-tools/releases", "https://github.com/owner/foo.github-tools.git/releases",
                     "https://codeberg.org/owner/foo.github-tools/releases", "https://codeberg.org/owner/foo.github-tools.git/releases" })
            await factory.Create(url).CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertInt(requested.Count).IsEqual(4);
        foreach (var request in requested) Assertions.AssertString(request).Contains("/owner/foo.github-tools/releases");
    }

    [TestCase]
    public static async Task GitLabSupportsSubgroupsAndSourceArchives()
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
    public static async Task ForgejoReleasesUseTheirAssetOrSourceArchive()
    {
        // Codeberg and other Forgejo or Gitea hosts publish releases at GitHub's path, read through their own API.
        var requested = new List<string>();
        using var client = new HttpClient(new Handler(request => { requested.Add(request.RequestUri!.AbsoluteUri); return Json("""
            [{"tag_name":"v1.3.0","draft":false,"prerelease":false,"html_url":"https://codeberg.org/owner/repo/releases/tag/v1.3.0",
              "zipball_url":"https://codeberg.org/owner/repo/archive/v1.3.0.zip",
              "assets":[{"name":"addon.zip","browser_download_url":"https://codeberg.org/owner/repo/releases/download/v1.3.0/addon.zip"},
                        {"name":"demo.tar.xz","browser_download_url":"https://codeberg.org/owner/repo/releases/download/v1.3.0/demo.tar.xz"}]},
             {"tag_name":"v1.2.0","draft":false,"prerelease":false,"zipball_url":"https://codeberg.org/owner/repo/archive/v1.2.0.zip","assets":[]},
             {"tag_name":"v4.0.0","draft":true,"assets":[]},
             {"tag_name":"nightly","draft":false,"assets":[]}]
            """); }));
        var source = new UpdateSourceFactory(new UpdateHttp(client)).Create("https://codeberg.org/owner/repo/releases");
        Assertions.AssertBool(source is ForgejoReleaseSource).IsTrue();
        var result = await source.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(requested[0]).IsEqual("https://codeberg.org/api/v1/repos/owner/repo/releases?limit=50&page=1");
        Assertions.AssertString(result!.NewVersion).IsEqual("v1.3.0");
        Assertions.AssertString(result.ReleaseUrl!).IsEqual("https://codeberg.org/owner/repo/releases/tag/v1.3.0");
        Assertions.AssertString(((ZipPackageRef)result.Package).Url).IsEqual("https://codeberg.org/owner/repo/releases/download/v1.3.0/addon.zip");
        var listed = await ((IVersionListSource)source).ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).ContainsExactly("v1.3.0", "v1.2.0");
        Assertions.AssertString(((ZipPackageRef)listed[1].Package).Url).IsEqual("https://codeberg.org/owner/repo/archive/v1.2.0.zip");

        // Gitea serves the same API as Forgejo.
        Assertions.AssertBool(new UpdateSourceFactory().Create("https://gitea.com/owner/repo/releases") is ForgejoReleaseSource).IsTrue();
        // A release page pins the source; other paths below /releases are no update sites.
        var pinned = new UpdateSourceFactory(new UpdateHttp(client)).Create("https://git.example/owner/repo/releases/tag/v1.3.0");
        Assertions.AssertObject(await pinned.CheckAsync(Target(), new(), CancellationToken.None)).IsNull();
        foreach (var url in new[] { "https://git.example/owner/repo/releases/download/v1/a.zip", "https://git.example/owner/releases", "http://codeberg.org/owner/repo/releases" })
            Assertions.AssertBool(UpdateSourceFactory.IsSupported(url)).IsFalse();
    }

    [TestCase]
    public static async Task RedirectDropsCredentialsAndRefusesHttp()
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
    public static void ForgeTokensGoOnlyToTheHostsTheyBelongTo()
    {
        // an update_url comes from a plugin's plugin.cfg: a host it names must not receive a token meant for another
        var environment = new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = "github", ["GITLAB_TOKEN"] = "gitlab", ["GITEA_TOKEN"] = "gitea",
            ["EPLUGIN_GITLAB_HOSTS"] = " git.example.org ,, other.example", ["EPLUGIN_FORGEJO_HOSTS"] = ""
        };
        string? Read(string name) => environment.GetValueOrDefault(name);
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.GitHub, "api.github.com", Read)).IsEqual("github");
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.GitHub, "github.example", Read)).IsNull();
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.GitLab, "gitlab.com", Read)).IsEqual("gitlab");
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.GitLab, "GIT.example.org", Read)).IsEqual("gitlab");
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.GitLab, "evil.example", Read)).IsNull();
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.Forgejo, "codeberg.org", Read)).IsEqual("gitea");
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.Forgejo, "git.example.org", Read)).IsNull();
        environment["EPLUGIN_FORGEJO_TOKEN"] = "forgejo";
        Assertions.AssertString(ForgeTokens.For(ForgeTokens.Forge.Forgejo, "codeberg.org", Read)).IsEqual("forgejo");
    }

    [TestCase]
    public static async Task DownloadFailsOnlyWhenNothingArrives()
    {
        var folder = Path.Combine(Path.GetTempPath(), "eplugin-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // four bytes 150 ms apart take longer than the stall timeout, but something arrives within it each time
            using var steady = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(4, false)) }));
            var complete = Path.Combine(folder, "complete.zip");
            await new UpdateHttp(steady) { StallTimeout = TimeSpan.FromMilliseconds(400) }
                .DownloadAsync("https://example.org/plugin.zip", complete, null, CancellationToken.None);
            Assertions.AssertThat(new FileInfo(complete).Length).IsEqual(4);

            using var stalled = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(1, true)) }));
            string? error = null;
            try
            {
                await new UpdateHttp(stalled) { StallTimeout = TimeSpan.FromMilliseconds(400) }
                    .DownloadAsync("https://example.org/plugin.zip", Path.Combine(folder, "stalled.zip"), null, CancellationToken.None);
            }
            catch (IOException ex) { error = ex.Message; }
            Assertions.AssertString(error).Contains("stalled");
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestCase]
    public static async Task RateLimitFallsBackToLatestRedirect()
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
    public static async Task BranchCheckUsesRawMetadataAndTagsArePinned()
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
    public static async Task SparseFetchLimitsCheckoutAndCleansWorkingTree()
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
    public static async Task SourcesListEveryPublishedVersion()
    {
        using var github = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[{"name":"plugin.zip","browser_download_url":"https://example.org/1.2.zip"}]},
             {"tag_name":"v1.1.0","draft":false,"prerelease":false,"assets":[]},
             {"tag_name":"2.0.0-beta.1","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"3.0.0","draft":true,"assets":[]}]
            """)));
        var listed = await new GitHubReleaseSource(new(github), "owner", "repo").ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).ContainsExactly("v1.2.0", "v1.1.0", "2.0.0-beta.1");
        Assertions.AssertString(((ZipPackageRef)listed[1].Package).Url).IsEqual("https://github.com/owner/repo/archive/refs/tags/v1.1.0.zip");

        using var gitlab = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"1.3.0","upcoming_release":false,"assets":{"links":[],"sources":[{"format":"zip","url":"https://git.example/1.3.zip"}]}},
             {"tag_name":"1.2.0","upcoming_release":false,"assets":{"links":[],"sources":[]}}]
            """)));
        var gitlabVersions = await new GitLabReleaseSource(new(gitlab), "git.example", "team/project").ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(gitlabVersions.Select(c => c.NewVersion).ToArray()).ContainsExactly("1.3.0");

        var tags = await new GitSource(new(), new FakeGit(), new("https://host/repo.git", "addon", null)).ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(tags.Single().NewVersion).IsEqual("v2.0.0");
        Assertions.AssertString(((GitPackageRef)tags[0].Package).Commit).IsEqual(FakeGit.Commit);
    }

    [TestCase]
    public static async Task ReleasesNameTheirVersionInTheTitleAndReleasesOfOneVersionAreAlternatives()
    {
        // GodotTouchInputManager: tag 4.x-v2.1.2 for Godot 4, published after v2.1.2 for Godot 3
        using var github = new HttpClient(new Handler(_ => Json("""
            [{"tag_name":"4.x-v2.1.2","name":"4.x - v2.1.2","draft":false,"assets":[{"name":"GDTIM-4.x-v2_1_2.zip","browser_download_url":"https://example.org/4.zip"}]},
             {"tag_name":"v2.1.2","name":"3.x - v2.1.2","draft":false,"assets":[{"name":"GDTIM-3.x-v2_1_2.zip","browser_download_url":"https://example.org/3.zip"}]},
             {"tag_name":"tap-rework","name":"v1.1.0 - Tap rework","draft":false,"assets":[]},
             {"tag_name":"nightly","name":"Nightly build","draft":false,"assets":[]}]
            """)));
        var source = new GitHubReleaseSource(new(github), "owner", "repo");

        var candidate = (await source.CheckAsync(Target(), new(), CancellationToken.None))!;
        Assertions.AssertString(candidate.NewVersion).IsEqual("v2.1.2");
        Assertions.AssertString(candidate.ResolvedRevision!).IsEqual("4.x-v2.1.2");
        var package = (ZipPackageRef)candidate.Package;
        Assertions.AssertString(package.Url).IsEqual("https://example.org/4.zip");
        Assertions.AssertArray(package.Alternatives!.ToArray()).ContainsExactly("https://example.org/3.zip");

        var listed = await source.ListAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).ContainsExactly("v2.1.2", "v1.1.0");
        Assertions.AssertString(((ZipPackageRef)listed[1].Package).Url).IsEqual("https://github.com/owner/repo/archive/refs/tags/tap-rework.zip");
        Assertions.AssertObject(((ZipPackageRef)listed[1].Package).Alternatives).IsNull();
        // a tag that is a version keeps its pre-release; the title is read from its last word
        Assertions.AssertString(Version("""{"tag_name":"v2.0.0-beta","name":"4.x - v9.0.0"}""")).IsEqual("v2.0.0-beta");
        Assertions.AssertString(Version("""{"tag_name":"x","name":"Godot 4.2 - 1.3.0"}""")).IsEqual("1.3.0");
        Assertions.AssertString(Version("""{"tag_name":"tap-rework","name":"v1.1.0 - Tap rework"}""")).IsEqual("v1.1.0");
        // the first version from the start; one right after the word Godot is the engine's
        Assertions.AssertString(Version("""{"tag_name":"untested","name":"GodotRx 1.0.1 for Godot 4.2"}""")).IsEqual("1.0.1");
        Assertions.AssertString(Version("""{"tag_name":"2.0.2.4_4","name":"QuestSystem v2.0.2 for Godot 4.4+"}""")).IsEqual("v2.0.2");
        Assertions.AssertObject(Version("""{"tag_name":"v0.6.0.gd4","name":"v0.6.0.gd4 (Godot 4.2-4.3) | Intersection prefabs"}""")).IsNull();
        // words of the tag, split at '_', when the title names none
        Assertions.AssertString(Version("""{"tag_name":"v1.1.6_godot_4.6","name":"Latest"}""")).IsEqual("v1.1.6");
        // and at '-' when a word is no version, which a prerelease is
        Assertions.AssertString(Version("""{"tag_name":"godot4-v1.4.4","name":"godot4-v1.4.4 - Touch input fix"}""")).IsEqual("v1.4.4");
        Assertions.AssertString(Version("""{"tag_name":"v0.7.4-godot-4.2+","name":"v0.7.4-godot-4.2+"}""")).IsEqual("v0.7.4");
        Assertions.AssertString(Version("""{"tag_name":"next","name":"Next v2.0.0-beta"}""")).IsEqual("v2.0.0-beta");
        Assertions.AssertObject(Version("""{"tag_name":"latest","name":"Latest build"}""")).IsNull();
        // a number after the version: a release candidate's number, or the Godot version a build is for
        Assertions.AssertObject(Version("""{"tag_name":"1.0.0rc7","name":"Release candidate 1.0.0 - 7"}""")).IsNull();
        Assertions.AssertObject(Version("""{"tag_name":"gdscript-4.4-v1","name":"Thumbstick Plugin v1.2.1 [GD Script - 4.4]"}""")).IsNull();

        static string? Version(string release) =>
            ReleaseAssets.TryVersion(System.Text.Json.JsonDocument.Parse(release).RootElement, out var version) ? version.ToString() : null;
    }

    [TestCase]
    public static async Task StagingInstallsTheFirstReleaseOfAVersionThatValidationAccepts()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "plugin.cfg"), "[plugin]\nname=\"Plugin\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n");
            var archives = new Dictionary<string, byte[]>
            {
                ["https://example.org/3.zip"] = PluginZip("tool\nextends EditorPlugin\nexport var speed = 1\n"),
                ["https://example.org/4.zip"] = PluginZip("@tool\nextends EditorPlugin\n@export var speed = 1\n"),
            };
            var requested = new List<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                requested.Add(request.RequestUri!.ToString());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(archives[request.RequestUri!.ToString()]) };
            }));
            var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null,
                new ZipPackageRef("https://example.org/3.zip", "plugin", ["https://example.org/4.zip"]));
            var targets = new[] { new PluginUpdateTarget("plugin", "Plugin", "1.0.0", null, installed) };
            var fetcher = new PackageFetcher(new UpdateHttp(client), new FakeGit());

            var batch = await fetcher.FetchAsync([candidate], targets, Path.Combine(root, "transaction"), null, CancellationToken.None);

            Assertions.AssertThat(requested).ContainsExactly("https://example.org/3.zip", "https://example.org/4.zip");
            var staged = batch.Packages.Single();
            Assertions.AssertString(File.ReadAllText(Path.Combine(staged.StagingDir, "plugin.gd"))).StartsWith("@tool");
            Assertions.AssertString(staged.Findings.Single(f => f.Code == "R28").Message).EndsWith("https://example.org/4.zip");

            // every package refused: the announced one's reason
            var refused = candidate with { Package = new ZipPackageRef("https://example.org/3.zip", "plugin", ["https://example.org/3.zip?again"]) };
            archives["https://example.org/3.zip?again"] = archives["https://example.org/3.zip"];
            string? reason = null;
            try { await fetcher.FetchAsync([refused], targets, Path.Combine(root, "transaction"), null, CancellationToken.None); }
            catch (InvalidDataException ex) { reason = ex.Message; }
            Assertions.AssertString(reason).Contains("plugin.gd has the line \"tool\"");
        }
        finally { Directory.Delete(root, true); }

        static byte[] PluginZip(string script)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var (name, text) in new[] { ("addons/plugin/plugin.cfg", "[plugin]\nname=\"Plugin\"\nversion=\"2.0.0\"\nscript=\"plugin.gd\"\n"), ("addons/plugin/plugin.gd", script) })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(text);
                }
            return stream.ToArray();
        }
    }

    [TestCase]
    public static void PackagesInGodot3ScriptOrWithGDNativeAreMadeForGodot3()
    {
        var root = Path.Combine(Path.GetTempPath(), "godot3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? Evidence(string name, string text)
            {
                var file = Path.Combine(root, name);
                File.WriteAllText(file, text);
                try { return AddonPackageValidator.Godot3Evidence(root, [file]); }
                finally { File.Delete(file); }
            }
            Assertions.AssertString(Evidence("a.gd", "extends Node\r\nonready var label = $Label\r\n")).Contains("\"onready var\"");
            Assertions.AssertString(Evidence("a.gd", "export(int, 1, 10) var speed = 1\n")).Contains("\"export(int, 1, 10) var\"");
            Assertions.AssertString(Evidence("a.gd", "tool\r\nextends EditorPlugin\n")).Contains("\"tool\"");
            Assertions.AssertString(Evidence("native.gdnlib", "[entry]\n")).IsEqual("native.gdnlib is a GDNative library");
            // Godot 4, and words that only look like Godot 3
            Assertions.AssertObject(Evidence("a.gd", "@tool\nextends Node\n@export var speed := 1\n@onready var label = $Label\n# export var old\nvar tool = 1\n")).IsNull();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public static async Task PinnedGitSourcesListTheCommitTheyPointToNow()
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
    public static void TagPagesNameTheirRepository()
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
    public static async Task VersionTagsAreTheVersionsOfARepository()
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
        Assertions.AssertArray(listed.Select(c => c.NewVersion).ToArray()).ContainsExactly("v2.0.0-beta.1", "v1.4", "1.3.0", "v1.2.3", "1.2");
        // an annotated tag installs the commit it points to, not the tag object
        Assertions.AssertString(((GitPackageRef)listed[1].Package).Commit).IsEqual(Commit('8'));
        Assertions.AssertString(((GitPackageRef)listed[1].Package).Repository).IsEqual("https://github.com/owner/repo.git");
        Assertions.AssertString(((GitPackageRef)listed[^1].Package).Commit).IsEqual(Commit('2'));

        // the newest stable tag is the update of a stable install; the tag name announces it, so nothing is fetched
        var update = await source.CheckAsync(Target(), new(), CancellationToken.None);
        Assertions.AssertString(update!.NewVersion).IsEqual("v1.4");
        var beta = await source.CheckAsync(Target() with { InstalledVersion = "1.4-rc.1" }, new(), CancellationToken.None);
        Assertions.AssertString(beta!.NewVersion).IsEqual("v2.0.0-beta.1");
        Assertions.AssertBool(git.Calls.Any(c => c.Contains("fetch") || c.Contains("show"))).IsFalse();
    }

    [TestCase]
    public static async Task RepositoryWithoutPathFindsThePluginFolder()
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

    [TestCase]
    public static async Task DownloadedAndGitPackagesAreStagedAndUnfitOnesAreRefusedAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "plugin.cfg"), "[plugin]\nname=\"Plugin\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n");
            var archive = Path.Combine(root, "plugin.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in new[] { ("addons/plugin/plugin.cfg", "[plugin]\nname=\"Plugin\"\nversion=\"2.0.0\"\nscript=\"plugin.gd\"\n"), ("addons/plugin/plugin.gd", "extends EditorPlugin") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(text);
                }
            }
            var requested = new List<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                requested.Add(request.RequestUri!.ToString());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archive)) };
            }));
            var zipped = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
            var git = new UpdateCandidate("git", "Git", "1.0.0", "2.0.0", "https://host/repo.git?path=addon", null, FakeGit.Commit, new GitPackageRef("https://host/repo.git", "addon", FakeGit.Commit));
            var unknown = git with { Slug = "unknown", Package = new OtherPackageRef() };
            var forged = git with { Slug = "forged", SourceUrl = "https://host/other.git?path=addon" };
            UpdateCandidate[] candidates = [zipped, git, unknown, forged];
            var targets = candidates.Select(c => new PluginUpdateTarget(c.Slug, c.PluginName, "1.0.0", null, installed)).ToArray();
            var progress = new List<double>();

            var batch = await new PackageFetcher(new UpdateHttp(client), new FakeGit()).FetchAsync(candidates, targets,
                Path.Combine(root, "transaction"), new InlineProgress(progress.Add), CancellationToken.None);

            Assertions.AssertString(batch.Packages.Single().Candidate.Slug).IsEqual("plugin");
            Assertions.AssertBool(File.Exists(Path.Combine(batch.Packages[0].StagingDir, "plugin.gd"))).IsTrue();
            Assertions.AssertThat(requested).ContainsExactly("https://example.org/plugin.zip");
            Assertions.AssertBool(File.Exists(Path.Combine(root, "transaction", "download.tmp"))).IsFalse();
            var reasons = batch.Refused.ToDictionary(r => r.Candidate.Slug, r => r.Reason);
            // the fake repository's plugin.cfg has a version only, so validation refuses what git staged
            Assertions.AssertString(reasons["git"]).StartsWith("Package for 'git' was refused:");
            Assertions.AssertString(reasons["unknown"]).Contains("Unsupported package reference.");
            Assertions.AssertString(reasons["forged"]).Contains("Git package does not match its declared source.");
            Assertions.AssertBool(Directory.Exists(Path.Combine(root, "transaction", "staging", "git"))).IsFalse();
            Assertions.AssertBool(Math.Abs(progress[^1] - 1.0) < 1e-9).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public static async Task GitLabBranchChecksReadTheRawPluginCfg()
    {
        var requested = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return Json("[plugin]\nversion=\"2.0.0\"\n");
        }));
        var git = new FakeGit();

        var candidate = await new GitSource(new(client), git, new("https://gitlab.com/group/project.git", "addon", "main"))
            .CheckAsync(Target(), new(), CancellationToken.None);

        Assertions.AssertString(candidate!.NewVersion).IsEqual("2.0.0");
        Assertions.AssertThat(requested).ContainsExactly($"https://gitlab.com/group/project/-/raw/{FakeGit.Commit}/addon/plugin.cfg");
        Assertions.AssertBool(git.Calls.Any(c => c.Contains("fetch"))).IsFalse();
    }

    [TestCase]
    public static async Task GitServersWithoutUsableCommitsOrPartialFetchAreRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-refused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = new GitPackageRef("https://host/repo.git", "addon", FakeGit.Commit);
            foreach (var (fake, message) in new[]
                     {
                         (new FakeGit(capabilities: ""), "does not advertise partial-fetch support"),
                         (new FakeGit(fetchError: "warning: filtering not recognized by server, ignoring"), "ignored the partial-fetch filter")
                     })
            {
                try
                {
                    await new GitSource(new(), fake, new("https://host/repo.git", "addon", null)).FetchAsync(package, "plugin", Path.Combine(root, "staged"), CancellationToken.None);
                    throw new InvalidOperationException("A full repository download was accepted.");
                }
                catch (IOException ex) { Assertions.AssertString(ex.Message).Contains(message); }
            }

            // a repository without version tags whose HEAD cannot be resolved
            try
            {
                await new GitSource(new(), new FakeGit(refs: ""), new("https://host/repo.git", "addon", null)).CheckAsync(Target(), new(), CancellationToken.None);
                throw new InvalidOperationException("A repository without a commit was checked.");
            }
            catch (IOException ex) { Assertions.AssertString(ex.Message).Contains("no usable commit"); }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public static async Task DeletingTheGitWorkFolderLeavesLinkedFoldersAlone()
    {
        // A checked-out repository may link a folder outside it; git writes its object files read-only, so the work folder
        // is made writable before it is deleted, which must not reach through the link.
        var root = Path.Combine(Path.GetTempPath(), "git-link-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "kept.txt");
        File.WriteAllText(file, "");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var package = new GitPackageRef("https://host/repo.git", "addon", FakeGit.Commit);
            await new GitSource(new(), new FakeGit(link: outside), new("https://host/repo.git", "addon", null))
                .FetchAsync(package, "plugin", Path.Combine(root, "staged"), CancellationToken.None);
            Assertions.AssertBool(File.Exists(file)).IsTrue();
            Assertions.AssertBool((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0).IsTrue();
            Assertions.AssertBool(Directory.EnumerateDirectories(root, "git-work-*").Any()).IsFalse();
        }
        finally { AddonFiles.DeleteTree(root); }
    }

    [TestCase]
    public static async Task TheGitRunnerReportsOutputErrorsAndExitCodes()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = new GitRunner();
            var version = await runner.RunAsync(root, ["--version"], CancellationToken.None, trace: true);
            Assertions.AssertInt(version.ExitCode).IsEqual(0);
            Assertions.AssertString(version.Output).StartsWith("git version");

            // not a repository
            var failed = await runner.RunAsync(root, ["rev-parse", "HEAD"], CancellationToken.None);
            Assertions.AssertInt(failed.ExitCode).IsNotEqual(0);
            Assertions.AssertString(failed.Error).IsNotEmpty();
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
    /// <summary>A response body that sends one byte every 150 ms, then ends or, when it <paramref name="stalls"/>, sends nothing more.</summary>
    private sealed class SlowStream(int bytes, bool stalls) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (bytes == 0)
            {
                if (stalls) await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            await Task.Delay(150, ct);
            bytes--;
            buffer.Span[0] = 1;
            return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    /// <summary>A repository with one plugin in <paramref name="folder"/> ("" at its root) next to a project.godot.</summary>
    private sealed record OtherPackageRef : UpdatePackageRef;
    /// <param name="capabilities">What ls-remote reports with packet tracing; without "filter" partial fetches are refused.</param>
    /// <param name="link">A folder the checkout links to from its root, read-only files and all.</param>
    private sealed class FakeGit(string folder = "addon", string? refs = null, string? tree = null, string version = "2.0.0",
        string capabilities = "fetch=shallow filter", string fetchError = "", string? link = null) : IGitRunner
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
                if (link is not null) Directory.CreateSymbolicLink(Path.Combine(directory, "linked"), link);
            }
            var output = args.Contains("--version") ? "git version 2.56.0"
                : args.Contains("ls-remote") ? refs ?? $"{Commit}\trefs/heads/main\n{Commit}\trefs/tags/v2.0.0\n"
                : args.Contains("ls-tree") ? tree ?? $"project.godot\0{prefix}plugin.cfg\0{prefix}plugin.gd\0"
                : args.Contains("show") ? config : "";
            return Task.FromResult(new GitOutcome(0, output, trace ? capabilities : args.Contains("fetch") ? fetchError : ""));
        }
    }
}
#endif
