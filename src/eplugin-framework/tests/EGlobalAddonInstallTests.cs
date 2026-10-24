#if TOOLS
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// A new add-on installed from a ZIP file through <see cref="EGlobal"/> with a fake editor: what the ePlugin Manager's
/// Install from ZIP does once its confirmation was accepted.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalAddonInstallTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _fixture.Global.InitializeUpdateJournals();
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Zip(string slug, bool csharp = false)
    {
        var path = Path.Combine(_fixture.Root, slug + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in new[]
                 {
                     ($"addons/{slug}/plugin.cfg", $"[plugin]\nname=\"{slug}\"\nversion=\"1.0.0\"\nscript=\"plugin.gd\"\n"),
                     ($"addons/{slug}/plugin.gd", "extends EditorPlugin")
                 }.Concat(csharp ? [($"addons/{slug}/Helper.cs", "internal static class Helper;")] : []))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private async Task<AddonInstallResult> Install(string zip)
    {
        var staged = await _fixture.Global.StageNewAddonAsync(AddonArchive.Read(zip), CancellationToken.None);
        Assertions.AssertBool(staged.Package.IsValid).IsTrue();
        return _fixture.Global.InstallNewAddon(staged.Package, staged.Directory);
    }

    [TestCase]
    public async Task ANewPluginIsInstalledDisabledAndListed()
    {
        var scans = _fixture.Host.Scans;

        var result = await Install(Zip("fresh"));

        Assertions.AssertBool(result.Installed).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(_fixture.Addon("fresh"), "plugin.gd"))).IsTrue();
        Assertions.AssertInt(_fixture.Host.Scans).IsEqual(scans + 1);
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("fresh")).IsFalse();
        Assertions.AssertBool(_fixture.Global.CollectPlugins().Single(p => p.Slug == "fresh").Enabled).IsFalse();
        Assertions.AssertThat(_fixture.Operations).NotContains("build");
        // a new add-on writes no plugin state; it is recorded once it is enabled
        Assertions.AssertObject(_fixture.Store.GetShared("fresh")).IsNull();
    }

    [TestCase]
    public async Task ACSharpPluginIsRemovedAgainWhenTheProjectDoesNotBuild()
    {
        _fixture.Cli.BuildFails = true;

        var result = await Install(Zip("fresh", csharp: true));

        Assertions.AssertBool(result.Installed).IsFalse();
        Assertions.AssertString(result.Failure).IsEqual("The project does not build with fresh; see the build log. It was removed again.");
        Assertions.AssertBool(Directory.Exists(_fixture.Addon("fresh"))).IsFalse();
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("build");
    }

    [TestCase]
    public async Task NoAddonIsInstalledWhileAnUpdateRuns()
    {
        var zip = Zip("fresh");
        var staged = await _fixture.Global.StageNewAddonAsync(AddonArchive.Read(zip), CancellationToken.None);
        var journal = new UpdateJournal { Id = "running", Directory = Path.Combine(_fixture.Root, ".godot", "eplugin", "updates", "running"), AttemptId = Guid.NewGuid() };
        journal.Plugins.Add(new() { Slug = "other", OldVersion = "1.0.0", NewVersion = "2.0.0" });
        Directory.CreateDirectory(journal.Directory);
        journal.Save(UpdatePhase.Swapped);

        Assertions.AssertThrown(() => _fixture.Global.InstallNewAddon(staged.Package, staged.Directory)).IsInstanceOf<InvalidOperationException>();
        Assertions.AssertBool(Directory.Exists(_fixture.Addon("fresh"))).IsFalse();
    }
}
#endif
