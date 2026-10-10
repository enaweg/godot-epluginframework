using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateSiteTests
{
    private const string Site = "https://github.com/fork/plugin/releases";
    private const string Config = "https://github.com/owner/plugin/releases";
    private const string Offline = "https://github.com/offline/plugin/releases";
    private string _root = null!;

    [BeforeTest] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "update-sites-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [AfterTest] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string SitesFile => Path.Combine(_root, "eplugin-update-sites.json");

    private static PluginUpdateTarget Target(string? config = Config, string? site = Site) =>
        new("plugin", "Plugin", "1.0.0", config, "/unused", OverrideUrl: site);

    [TestCase]
    public void SitesAreSavedAddedEditedAndRemoved()
    {
        var settings = new UpdateSiteSettings(SitesFile);
        settings.Load();
        Assertions.AssertBool(settings.Set("zeta", "  " + Site + " ")).IsTrue();
        Assertions.AssertBool(settings.Set("alpha", "git@host:owner/plugin.git?path=addons/alpha#main")).IsTrue();
        Assertions.AssertBool(settings.Set("zeta", Site)).IsFalse();
        Assertions.AssertBool(settings.Set("zeta", Config)).IsTrue();

        var reloaded = new UpdateSiteSettings(SitesFile);
        reloaded.Load();
        Assertions.AssertArray(reloaded.Sites.Select(s => s.Slug).ToArray()).IsEqual(new[] { "alpha", "zeta" });
        Assertions.AssertString(reloaded.UrlOf("zeta")).IsEqual(Config);
        Assertions.AssertString(reloaded.UrlOf("missing")).IsNull();

        Assertions.AssertBool(reloaded.Remove("alpha")).IsTrue();
        Assertions.AssertBool(reloaded.Remove("alpha")).IsFalse();
        Assertions.AssertBool(File.Exists(SitesFile)).IsTrue();
        // the file goes with its last site, so a project without update sites has none
        Assertions.AssertBool(reloaded.Remove("zeta")).IsTrue();
        Assertions.AssertBool(File.Exists(SitesFile)).IsFalse();
    }

    [TestCase]
    public void UnsupportedUrlsAndSlugsAreRefused()
    {
        var settings = new UpdateSiteSettings(SitesFile);
        settings.Load();
        foreach (var url in new[] { "", "not a url", "http://github.com/owner/plugin/releases", "https://example.org/plugin.zip" })
            Assertions.AssertThrown(() => settings.Set("plugin", url)).IsInstanceOf<ArgumentException>();
        foreach (var slug in new[] { "", "../plugin", "a/b" })
            Assertions.AssertThrown(() => settings.Set(slug, Site)).IsInstanceOf<ArgumentException>();
        Assertions.AssertBool(File.Exists(SitesFile)).IsFalse();
    }

    [TestCase]
    public void UnreadableOrNewerFilesAreNeverOverwritten()
    {
        File.WriteAllText(SitesFile, "{ broken");
        var broken = new UpdateSiteSettings(SitesFile);
        broken.Load();
        Assertions.AssertString(broken.Problem).Contains("Repair or delete");
        Assertions.AssertThrown(() => broken.Set("plugin", Site)).IsInstanceOf<InvalidOperationException>();
        Assertions.AssertString(File.ReadAllText(SitesFile)).IsEqual("{ broken");

        File.WriteAllText(SitesFile, """{ "schema": 2, "sites": [ { "slug": "plugin", "url": "https://github.com/a/b/releases" } ] }""");
        var newer = new UpdateSiteSettings(SitesFile);
        newer.Load();
        Assertions.AssertString(newer.Problem).Contains("newer ePlugin version");
        // what it can read is still used
        Assertions.AssertString(newer.UrlOf("plugin")).IsEqual("https://github.com/a/b/releases");
        Assertions.AssertThrown(() => newer.Remove("plugin")).IsInstanceOf<InvalidOperationException>();
    }

    [TestCase]
    public void HandEditedEntriesAreKeptAsTheyAre()
    {
        File.WriteAllText(SitesFile, """
            { "schema": 1, "sites": [ { "slug": "plugin", "url": "not supported" }, { "slug": "../evil", "url": "x" }, { "slug": "other", "url": " " } ] }
            """);
        var settings = new UpdateSiteSettings(SitesFile);
        settings.Load();
        Assertions.AssertString(settings.Problem).IsNull();
        // an unsupported URL stays, so checks fall back to plugin.cfg; invalid slugs and empty URLs are dropped
        Assertions.AssertArray(settings.Sites.Select(s => s.Slug).ToArray()).IsEqual(new[] { "plugin" });
        Assertions.AssertBool(UpdateSourceFactory.IsSupported(settings.UrlOf("plugin"))).IsFalse();
    }

    [TestCase]
    public void TheProjectSiteIsPreferredAndPluginCfgIsTheFallback()
    {
        Assertions.AssertArray(Target().UpdateUrls.ToArray()).IsEqual(new[] { Site, Config });
        Assertions.AssertArray(Target(site: null).UpdateUrls.ToArray()).IsEqual(new[] { Config });
        Assertions.AssertArray(Target(config: null).UpdateUrls.ToArray()).IsEqual(new[] { Site });
        Assertions.AssertArray(Target(config: Site).UpdateUrls.ToArray()).IsEqual(new[] { Site });
        Assertions.AssertInt(Target(config: null, site: null).UpdateUrls.Count).IsEqual(0);
    }

    [TestCase]
    public async Task ChecksUseTheProjectSiteAndFallBackWhenItFails()
    {
        var store = new MemoryStore();
        var service = new UpdateService(new Factory(), new SystemClock(), store);
        var working = await service.CheckAsync([Target()], new(), CancellationToken.None);
        Assertions.AssertString(working.Updates.Single().SourceUrl).IsEqual(Site);
        Assertions.AssertInt(working.Fallbacks!.Count).IsEqual(0);

        foreach (var broken in new[] { Offline, "not supported" })
        {
            var fallback = await service.CheckAsync([Target(site: broken)], new(), CancellationToken.None);
            Assertions.AssertString(fallback.Updates.Single().SourceUrl).IsEqual(Config);
            Assertions.AssertInt(fallback.Failures.Count).IsEqual(0);
            Assertions.AssertString(fallback.Fallbacks!.Single().Message).Contains(broken);
        }

        var failed = await service.CheckAsync([Target(config: Offline, site: "not supported")], new(), CancellationToken.None);
        Assertions.AssertInt(failed.Updates.Count).IsEqual(0);
        Assertions.AssertString(failed.Failures.Single().Message).Contains("not supported");
        Assertions.AssertString(failed.Failures.Single().Message).Contains(Offline);
    }

    [TestCase]
    public async Task VersionListsFallBackAndCacheTheSiteTheyCameFrom()
    {
        var store = new MemoryStore();
        var service = new UpdateService(new Factory(), new SystemClock(), store);
        var target = Target(site: Offline);
        var versions = await service.ListVersionsAsync(target, new(), CancellationToken.None);
        Assertions.AssertString(versions.First().SourceUrl).IsEqual(Config);
        Assertions.AssertString(store.State.Results.Single().SourceUrl).IsEqual(Config);
        // a cached update counts for the plugin whichever of its sites it came from
        Assertions.AssertInt(UpdateScheduler.CurrentCached(store.State, [target], false).Count).IsEqual(1);
        Assertions.AssertInt(UpdateScheduler.CurrentCached(store.State, [Target(config: null, site: Site)], false).Count).IsEqual(0);
    }

    [TestCase]
    public void ManagerAcceptsUpdatesFromEitherSiteAndShowsTheProjectSite()
    {
        var plugin = new PluginInfo("plugin", "Plugin", PluginKind.GDScript, true) { Version = "1.0.0", UpdateUrl = Config, UpdateSite = Site };
        var fromSite = Candidate(Site);
        var model = new PluginManagerViewModel([plugin], [fromSite], [Target()], null, _ => []);
        Assertions.AssertBool(model.Updates.Single().Findings.Any(f => f.Code == "source_changed")).IsFalse();
        var gone = new PluginManagerViewModel([plugin], [Candidate("https://github.com/old/plugin/releases")], [Target()], null, _ => []);
        Assertions.AssertBool(gone.Updates.Single().Findings.Any(f => f.Code == "source_changed")).IsTrue();

        var details = PluginManagerViewModel.Describe(new(plugin, null));
        Assertions.AssertString(details).Contains($"[b]Update site:[/b] [url={Site}]");
        Assertions.AssertString(details).Contains("The project sets this update site");
        Assertions.AssertString(details).Contains($"[b]plugin.cfg update_url:[/b] [url={Config}]");
        // a plugin without an update_url is updatable through the project's site alone
        var siteOnly = new PluginRow(plugin with { UpdateUrl = null }, null);
        Assertions.AssertBool(siteOnly.IsUpdatable).IsTrue();
        Assertions.AssertString(PluginManagerViewModel.Describe(siteOnly)).NotContains("plugin.cfg update_url:");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DialogSceneHasTheNodesItsScriptBinds()
    {
        var dialog = Godot.GD.Load<Godot.PackedScene>("res://addons/ePlugin/Internal/Manager/UpdateSitesDialog.tscn").Instantiate<UpdateSitesDialog>();
        try
        {
            foreach (var name in new[] { "%SiteTree", "%SiteStatus", "%AddSiteButton", "%EditSiteButton", "%RemoveSiteButton", "%SiteEditor", "%SiteSlug", "%SiteUrl", "%SiteHint" })
                Assertions.AssertObject(dialog.GetNodeOrNull(name)).IsNotNull();
            var manager = Godot.GD.Load<Godot.PackedScene>(EPluginManagerDialog.ScenePath).Instantiate<EPluginManagerDialog>();
            try
            {
                Assertions.AssertObject(manager.GetNodeOrNull("%UpdateSitesButton")).IsNotNull();
                Assertions.AssertObject(manager.GetNodeOrNull("%UpdateSitesDialog")).IsInstanceOf<UpdateSitesDialog>();
            }
            finally { manager.Free(); }
        }
        finally { dialog.Free(); }
    }

    private static UpdateCandidate Candidate(string sourceUrl) =>
        new("plugin", "Plugin", "1.0.0", "2.0.0", sourceUrl, null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));

    /// <summary>The offline site fails, an unsupported URL is refused like by the real factory, any other site works.</summary>
    private sealed class Factory : IUpdateSourceFactory
    {
        public IUpdateSource Create(string url) => url.StartsWith("https://", StringComparison.Ordinal) ? new Source() : new UnsupportedUpdateSource(url);
    }
    private sealed class Source : IUpdateSource, IVersionListSource
    {
        public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
            target.UpdateUrl == Offline ? Task.FromException<UpdateCandidate?>(new IOException("offline"))
                : Task.FromResult<UpdateCandidate?>(Candidate(target.UpdateUrl!));
        public Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
            target.UpdateUrl == Offline ? Task.FromException<IReadOnlyList<UpdateCandidate>>(new IOException("offline"))
                : Task.FromResult<IReadOnlyList<UpdateCandidate>>([Candidate(target.UpdateUrl!)]);
    }
    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateCache State { get; } = new();
        public void Save() { }
    }
}
