using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class LocalSourceTests
{
    private string _root = null!;
    [BeforeTest] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "local-source-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [AfterTest] public void Cleanup() { Directory.Delete(_root, true); }

    [TestCase]
    public void IndexReadsPluginCfgOfZipsInSubdirectoriesAndSkipsMissingDirectories()
    {
        var packages = Path.Combine(_root, "packages");
        // A sub-plugin below the plugin root belongs to the package and does not make it ambiguous.
        Zip(Path.Combine(packages, "plugin-1.1.zip"), ("addons/plugin/plugin.cfg", Config("1.1.0")), ("addons/plugin/plugin.gd", "extends EditorPlugin"),
            ("addons/plugin/tools/sub/plugin.cfg", Config("4.0.0", "Sub")));
        Zip(Path.Combine(packages, "nested", "deeper", "plugin-2.0.zip"), ("plugin/plugin.cfg", Config("2.0.0")), ("plugin/plugin.gd", "extends EditorPlugin"));
        Zip(Path.Combine(packages, "documents.zip"), ("readme.txt", "not a plugin"));
        File.WriteAllText(Path.Combine(packages, "broken.zip"), "not a zip");
        var missing = Path.Combine(_root, "unplugged-drive");

        // A nested directory listed as well must not index its archives twice.
        var index = LocalPackageIndexer.Build([packages, Path.Combine(packages, "nested"), missing], CancellationToken.None);

        Assertions.AssertInt(index.Packages.Count).IsEqual(2);
        Assertions.AssertInt(index.Archives).IsEqual(4);
        Assertions.AssertInt(index.Failures.Count).IsEqual(1);
        Assertions.AssertString(Path.GetFileName(index.Failures[0].Path)).IsEqual("broken.zip");
        Assertions.AssertBool(index.Directories.Single(d => d.Path == missing).Exists).IsFalse();
        var addon = index.Packages.Single(p => p.Root == "addons/plugin/");
        Assertions.AssertString(addon.Slug).IsEqual("plugin");
        Assertions.AssertString(addon.Version).IsEqual("1.1.0");
        var wrapped = index.Packages.Single(p => p.Root == "plugin/");
        Assertions.AssertString(wrapped.Slug).IsEqual("plugin");
        Assertions.AssertString(wrapped.Version).IsEqual("2.0.0");
    }

    [TestCase]
    public void IndexReportsWhatTheExtractorWouldRefuse()
    {
        var packages = Path.Combine(_root, "packages");
        Zip(Path.Combine(packages, "two-roots.zip"), ("addons/plugin/plugin.cfg", Config("2.0.0")), ("addons/other/plugin.cfg", Config("3.0.0")));
        Zip(Path.Combine(packages, "outside-root.zip"), ("addons/plugin/plugin.cfg", Config("2.0.0")), ("extras/deep/other/plugin.cfg", Config("3.0.0")));
        Zip(Path.Combine(packages, "archive-root.zip"), ("plugin.cfg", Config("2.0.0")), ("plugin.gd", "extends EditorPlugin"));
        Zip(Path.Combine(packages, "noversion.zip"), ("plugin/plugin.cfg", "[plugin]\nname=\"Plugin\"\nscript=\"plugin.gd\"\n"));
        Zip(Path.Combine(packages, "unsafe.zip"), ("plugin/plugin.cfg", Config("2.0.0")), ("../evil", "bad"));
        var index = LocalPackageIndexer.Build([packages], CancellationToken.None);
        Assertions.AssertInt(index.Packages.Count).IsEqual(0);
        Assertions.AssertArray(index.Failures.Select(f => Path.GetFileName(f.Path)).Order().ToArray())
            .IsEqual(new[] { "archive-root.zip", "noversion.zip", "outside-root.zip", "two-roots.zip", "unsafe.zip" });
    }

    [TestCase]
    public void LocalSourceMatchesTheRootFolderAndOffersTheNewestVersion()
    {
        var index = new LocalPackageIndex([
            Package("a.zip", "addons/plugin/", "1.5.0"),
            Package("b.zip", "plugin/", "2.0.0"),
            Package("c.zip", "repo/plugin/", "3.0.0-beta.1"),
            // The plugin.cfg of these may well name the plugin "Plugin"; only the folder decides.
            Package("d.zip", "addons/other/", "9.0.0"),
            Package("e.zip", "plugin-9.0/", "9.0.0")
        ], [], 5, []);
        var target = Target();
        var stable = LocalDirectorySource.Versions(index, target, new());
        Assertions.AssertArray(stable.Select(c => c.NewVersion).ToArray()).IsEqual(new[] { "2.0.0", "1.5.0" });
        Assertions.AssertString(((LocalZipPackageRef)stable[0].Package).Path).IsEqual("b.zip");
        Assertions.AssertString(LocalDirectorySource.Versions(index, target, new(AllowPrerelease: true))[0].NewVersion).IsEqual("3.0.0-beta.1");

        var updates = UpdateService.CheckLocal([target, Target("unrelated")], index, new());
        Assertions.AssertInt(updates.Count).IsEqual(1);
        Assertions.AssertString(updates[0].NewVersion).IsEqual("2.0.0");
        Assertions.AssertInt(UpdateService.CheckLocal([target with { InstalledVersion = "2.0.0" }], index, new()).Count).IsEqual(0);
    }

    [TestCase]
    public void SameVersionPrefersTheNewestFileAndLocalWinsOverRemote()
    {
        var older = Package("old.zip", "addons/plugin/", "2.0.0") with { ModifiedUtc = DateTime.UtcNow.AddDays(-1) };
        var newer = Package("new.zip", "addons/plugin/", "2.0.0");
        var local = LocalDirectorySource.Versions(new([older, newer], [], 2, []), Target(), new()).Single();
        Assertions.AssertString(local.SourceUrl).IsEqual("new.zip");
        var remote = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/p.zip", "plugin"));
        Assertions.AssertBool(UpdateService.Merge([remote], [local]).Single().Package is LocalZipPackageRef).IsTrue();
        var newerRemote = remote with { NewVersion = "2.1.0" };
        Assertions.AssertBool(UpdateService.Merge([newerRemote], [local]).Single().Package is ZipPackageRef).IsTrue();
    }

    [TestCase]
    public async Task VersionListCombinesSourcesAndWorksWithoutUpdateUrl()
    {
        var index = new LocalPackageIndex([Package("a.zip", "addons/plugin/", "1.5.0"), Package("b.zip", "addons/plugin/", "1.2.0")], [], 2, []);
        var service = new UpdateService(new Factory(), new SystemClock(), new MemoryStore());
        var local = await service.ListVersionsAsync(Target(), new(), CancellationToken.None, index);
        Assertions.AssertArray(local.Select(v => v.NewVersion).ToArray()).IsEqual(new[] { "1.5.0", "1.2.0" });
        var combined = await service.ListVersionsAsync(Target() with { UpdateUrl = "https://example.org/listed" }, new(), CancellationToken.None, index);
        Assertions.AssertArray(combined.Select(v => v.NewVersion).ToArray()).IsEqual(new[] { "2.0.0", "1.5.0", "1.2.0" });
        Assertions.AssertBool(combined.Single(v => v.NewVersion == "1.2.0").Package is LocalZipPackageRef).IsTrue();
        var offline = await service.ListVersionsAsync(Target() with { UpdateUrl = "https://example.org/offline" }, new(), CancellationToken.None, index);
        Assertions.AssertInt(offline.Count).IsEqual(2);
        var refused = false;
        try { await service.ListVersionsAsync(Target(), new(), CancellationToken.None); }
        catch (NotSupportedException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();
    }

    [TestCase]
    public async Task RemoteChecksSkipPluginsWithoutUpdateUrl()
    {
        var service = new UpdateService(new Factory(), new SystemClock(), new MemoryStore());
        var result = await service.CheckAsync([Target(), Target("remote") with { UpdateUrl = "https://example.org/listed" }], new(), CancellationToken.None);
        Assertions.AssertInt(result.Failures.Count).IsEqual(0);
        Assertions.AssertString(result.Updates.Single().Slug).IsEqual("remote");
    }

    [TestCase]
    public async Task LocalPackageIsStagedFromTheZipAndValidated()
    {
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        var zip = Path.Combine(_root, "packages", "plugin.zip");
        Zip(zip, ("addons/plugin/plugin.cfg", Config("2.0.0")), ("addons/plugin/plugin.gd", "extends EditorPlugin"));
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", null, installed);
        var candidate = LocalDirectorySource.Versions(LocalPackageIndexer.Build([Path.GetDirectoryName(zip)!], CancellationToken.None), target, new()).Single();
        var transaction = Path.Combine(_root, "transaction");
        var staged = await new PackageFetcher(new UpdateHttp(), new GitRunner()).FetchAsync([candidate], [target], transaction, null, CancellationToken.None);
        Assertions.AssertBool(staged.Single().IsValid).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(staged.Single().StagingDir, "plugin.gd"))).IsTrue();
        Assertions.AssertBool(File.Exists(zip)).IsTrue();

        // A candidate whose path does not match its source is refused rather than read from somewhere else.
        var forged = candidate with { Package = new LocalZipPackageRef(Path.Combine(_root, "elsewhere.zip")) };
        var refused = false;
        try { await new PackageFetcher(new UpdateHttp(), new GitRunner()).FetchAsync([forged], [target], Path.Combine(_root, "forged"), null, CancellationToken.None); }
        catch (InvalidDataException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();
    }

    [TestCase]
    public void SettingsKeepMissingDirectoriesAndRefuseDuplicatesAndRelativePaths()
    {
        var file = Path.Combine(_root, "config", "local-sources.json");
        var settings = new LocalSourceSettings(file);
        settings.Load();
        var missing = Path.Combine(_root, "gone");
        var existing = Path.Combine(_root, "packages");
        Directory.CreateDirectory(existing);
        Assertions.AssertBool(settings.Add(existing + Path.DirectorySeparatorChar)).IsTrue();
        Assertions.AssertBool(settings.Add(existing)).IsFalse();
        Assertions.AssertBool(settings.Add(missing)).IsTrue();
        var relative = false;
        try { settings.Add("relative/dir"); }
        catch (ArgumentException) { relative = true; }
        Assertions.AssertBool(relative).IsTrue();

        var reloaded = new LocalSourceSettings(file);
        reloaded.Load();
        Assertions.AssertArray(reloaded.Directories.ToArray()).IsEqual(new[] { existing, missing });
        Assertions.AssertBool(reloaded.Remove(existing)).IsTrue();
        reloaded.Load();
        Assertions.AssertArray(reloaded.Directories.ToArray()).IsEqual(new[] { missing });
        File.WriteAllText(file, "broken json");
        reloaded.Load();
        Assertions.AssertInt(reloaded.Directories.Count).IsEqual(0);
    }

    [TestCase]
    public void SettingsChangedByAnotherEditorAreKeptAndUnreadableFilesAreNeverOverwritten()
    {
        var file = Path.Combine(_root, "config", "local-sources.json");
        var first = new LocalSourceSettings(file); first.Load();
        var second = new LocalSourceSettings(file); second.Load();
        var one = Path.Combine(_root, "one"); var two = Path.Combine(_root, "two");
        Assertions.AssertBool(first.Add(one)).IsTrue();
        // The second editor still holds the list it loaded before; its change must not drop the first one's.
        Assertions.AssertBool(second.Add(two)).IsTrue();
        Assertions.AssertArray(second.Directories.ToArray()).IsEqual(new[] { one, two });
        Assertions.AssertBool(first.Remove(two)).IsTrue();
        Assertions.AssertArray(first.Directories.ToArray()).IsEqual(new[] { one });

        foreach (var content in new[] { "broken json", "{\"schema\": 2, \"directories\": []}" })
        {
            File.WriteAllText(file, content);
            first.Load();
            Assertions.AssertString(first.Problem).IsNotNull();
            var refused = false;
            try { first.Add(two); }
            catch (InvalidOperationException) { refused = true; }
            Assertions.AssertBool(refused).IsTrue();
            Assertions.AssertString(File.ReadAllText(file)).IsEqual(content);
        }
        File.Delete(file);
        first.Load();
        Assertions.AssertString(first.Problem).IsNull();
        Assertions.AssertBool(first.Add(two)).IsTrue();
    }

    private static PluginUpdateTarget Target(string slug = "plugin") => new(slug, "Plugin", "1.0.0", null, "/unused");
    private static LocalPackage Package(string zip, string root, string version) =>
        new(zip, root, SafeZipExtractor.RootSlug(root)!, version, DateTime.UtcNow);
    private static void Zip(string path, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
    }
    private static string Config(string version, string name = "Plugin") => $"[plugin]\nname=\"{name}\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n";
    private sealed class Factory : IUpdateSourceFactory
    {
        public IUpdateSource Create(string url) => new Source(url.Contains("offline"));
    }
    private sealed class Source(bool offline) : IUpdateSource, IVersionListSource
    {
        private static UpdateCandidate Candidate(PluginUpdateTarget target, string version) => new(target.Slug, target.Name, target.InstalledVersion, version,
            target.UpdateUrl!, null, null, new ZipPackageRef("https://example.org/package.zip", target.Slug));
        public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) => Task.FromResult<UpdateCandidate?>(Candidate(target, "2.0.0"));
        public Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) => offline
            ? Task.FromException<IReadOnlyList<UpdateCandidate>>(new IOException("offline"))
            : Task.FromResult<IReadOnlyList<UpdateCandidate>>([Candidate(target, "2.0.0"), Candidate(target, "1.2.0")]);
    }
    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateCache State { get; } = new();
        public void Save() { }
    }
}
