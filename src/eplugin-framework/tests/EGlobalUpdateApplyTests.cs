#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Updates of plain plugins found in a local directory, staged and installed through <see cref="EGlobal"/> with a fake
/// editor: what the ePlugin Manager's Update button does, up to the build, which the update host only records.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalUpdateApplyTests : IDisposable
{
    private LifecycleFixture _fixture = null!;
    private RecordingUpdateHost _updates = null!;
    private string _packages = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _updates = new RecordingUpdateHost(_fixture);
        _fixture.Global.UpdateHost = _updates;
        _fixture.Global.InitializeUpdateJournals();
        // reads the update cache, as at startup
        _fixture.Global.ResumeUpdates();
        _fixture.Global.InitializeLocalSources();
        _packages = Path.Combine(_fixture.Root, "packages");
        Directory.CreateDirectory(_packages);
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Installed(string slug) => File.ReadAllText(Path.Combine(_fixture.Addon(slug), "plugin.cfg"));

    /// <summary>
    /// An enabled plain plugin at 1.0.0 and a ZIP of its 2.0.0 in the local directory; one with C# makes the update build
    /// the project.
    /// </summary>
    private void PluginWithNewerPackage(string slug, bool csharp = false)
    {
        _fixture.AddPlainPlugin(slug);
        _fixture.Host.Enabled.Add(slug);
        using var zip = ZipFile.Open(Path.Combine(_packages, $"{slug}-2.0.0.zip"), ZipArchiveMode.Create);
        foreach (var (path, content) in new[]
                 {
                     ($"addons/{slug}/plugin.cfg", $"[plugin]\nname=\"{slug}\"\nversion=\"2.0.0\"\nscript=\"plugin.gd\"\n"),
                     ($"addons/{slug}/plugin.gd", "extends EditorPlugin")
                 }.Concat(csharp ? [($"addons/{slug}/Helper.cs", "internal static class Helper;")] : []))
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write(content);
        }
    }

    private async Task Index()
    {
        _fixture.Global.AddLocalDirectory(_packages);
        for (var frame = 0; frame < 500 && _fixture.Global.IsIndexingLocalSources; frame++)
        {
            EGlobal.RunPosted();
            await Task.Delay(10);
        }
        Assertions.AssertBool(_fixture.Global.IsIndexingLocalSources).IsFalse();
    }

    private async Task<IReadOnlyList<UpdateGroupResult>> Install(bool askOnBuildFailure = false)
    {
        var staged = await _fixture.Global.StageUpdatesAsync(_fixture.Global.PendingUpdates, null, CancellationToken.None);
        Assertions.AssertThat(staged.Refused).IsEmpty();
        return await _fixture.Global.ApplyUpdatesAsync(staged.Packages, staged.Directory, false, askOnBuildFailure);
    }

    [TestCase]
    public async Task TheEditorVersionIsAskedBeforeStagingLeavesTheEditorThread()
    {
        // Validating a GDExtension (R22) needs the editor's version on the thread-pool thread that stages the packages,
        // where Godot must not be called, so it is asked on the editor thread first.
        GDExtensionConfig.ResetEditorVersion();
        PluginWithNewerPackage("tool");
        await Index();
        await _fixture.Global.StageUpdatesAsync(_fixture.Global.PendingUpdates, null, CancellationToken.None);
        Assertions.AssertBool(GDExtensionConfig.EditorVersionKnown).IsTrue();
    }

    [TestCase]
    public async Task AnUnquotedVersionIsComparedByItsValue()
    {
        // Godot reads version=1.0.0 without quotes as 1.0, the updater's own reader as written.
        PluginWithNewerPackage("tool");
        File.WriteAllText(Path.Combine(_fixture.Addon("tool"), "plugin.cfg"), "[plugin]\nname=\"tool\"\nversion=1.0.0\nscript=\"plugin.gd\"\n");
        await Index();

        var results = await Install();

        Assertions.AssertObject(results.Single().Outcome).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertString(Installed("tool")).Contains("version=\"2.0.0\"");
    }

    [TestCase]
    public async Task ALicenseAcceptedForAnUpdateIsRecordedWhenItCommits()
    {
        PluginWithNewerPackage("tool");
        // its build fails, so it is rolled back
        PluginWithNewerPackage("broken", csharp: true);
        _updates.BuildFails = () => Installed("broken").Contains("2.0.0");
        _fixture.Store.TryRecordLicenses([new AcceptedLicense("tool", "LICENSE", "1.0.0", DateTimeOffset.UtcNow, false),
            new AcceptedLicense("broken", "LICENSE", "1.0.0", DateTimeOffset.UtcNow, false)]);
        await Index();
        var staged = await _fixture.Global.StageUpdatesAsync(_fixture.Global.PendingUpdates, null, CancellationToken.None);

        var results = await _fixture.Global.ApplyUpdatesAsync(staged.Packages.Select(p => p with { AcceptedLicense = "LICENSE.md" }).ToArray(),
            staged.Directory, false);

        Assertions.AssertObject(results.Single(r => r.Packages.Any(p => p.Candidate.Slug == "tool")).Outcome).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertObject(results.Single(r => r.Packages.Any(p => p.Candidate.Slug == "broken")).Outcome).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("tool", "LICENSE.md")).IsTrue();
        Assertions.AssertString(_fixture.Store.GetLicense("tool")!.Version).IsEqual("2.0.0");
        // the installed version's license stays accepted
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("broken", "LICENSE")).IsTrue();
    }

    [TestCase]
    public async Task ALocalPackageIsStagedInstalledAndNoLongerOffered()
    {
        PluginWithNewerPackage("tool");
        await Index();
        Assertions.AssertThat(_fixture.Global.PendingUpdates.Select(c => $"{c.Slug} {c.NewVersion}").ToArray()).ContainsExactly("tool 2.0.0");

        var results = await Install();

        Assertions.AssertObject(results.Single().Outcome).IsEqual(UpdateOutcome.Completed);
        Assertions.AssertString(Installed("tool")).Contains("version=\"2.0.0\"");
        Assertions.AssertString(_fixture.Store.GetShared("tool")!.Version).IsEqual("2.0.0");
        Assertions.AssertThat(_fixture.Global.PendingUpdates).IsEmpty();
        // the plain plugin is off while its files are swapped
        Assertions.AssertThat(_updates.Toggles).ContainsExactly("tool False", "tool True");
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("tool")).IsTrue();
        // the editor imports when unfocused again afterwards; nothing needed a restart
        Assertions.AssertObject(_fixture.Host.UnfocusedImports).IsEqual((1, 1));
        Assertions.AssertInt(_fixture.Host.Restarts).IsEqual(0);
    }

    [TestCase]
    public async Task IndependentUpdatesAreInstalledInGroupsAndAFailingOneIsRolledBackAlone()
    {
        PluginWithNewerPackage("alpha");
        PluginWithNewerPackage("beta", csharp: true);
        await Index();
        // the project does not build with beta's new version
        _updates.BuildFails = () => Installed("beta").Contains("2.0.0");

        var results = await Install();

        Assertions.AssertThat(results.Select(r => (r.Packages.Single().Candidate.Slug, r.Outcome)).ToArray())
            .ContainsExactly(("alpha", (UpdateOutcome?)UpdateOutcome.Completed), ("beta", (UpdateOutcome?)UpdateOutcome.RolledBack));
        Assertions.AssertString(Installed("alpha")).Contains("version=\"2.0.0\"");
        Assertions.AssertString(Installed("beta")).Contains("version=\"1.0.0\"");
        Assertions.AssertBool(_fixture.Global.UpdateCache!.FailedUpdates["beta"].Any(f => f.Version == "2.0.0")).IsTrue();
        Assertions.AssertObject(_fixture.Store.GetLocal("beta")).IsNull();
    }

    [TestCase]
    public async Task AnUpdateFromThePluginsDetailsAsksWhetherToKeepAVersionThatDoesNotBuild()
    {
        PluginWithNewerPackage("tool", csharp: true);
        await Index();
        _updates.UiAvailable = true;
        _updates.BuildFails = () => Installed("tool").Contains("2.0.0");
        UpdateJournal? asked = null;
        _fixture.Global.UpdateDecisionNeeded += journal => asked = journal;

        var results = await Install(askOnBuildFailure: true);

        Assertions.AssertObject(results.Single().Outcome).IsEqual(UpdateOutcome.AwaitingDecision);
        Assertions.AssertString(Installed("tool")).Contains("version=\"2.0.0\"");
        // the dialog is asked for on the next frame, not inside the installation
        Assertions.AssertObject(asked).IsNull();
        EGlobal.RunPosted();
        Assertions.AssertObject(asked).IsNotNull();
        // another batch waits until this one is decided
        var refused = false;
        try { await _fixture.Global.StageUpdatesAsync([], null, CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        Assertions.AssertBool(refused).IsTrue();

        _fixture.Global.DecideUpdate(asked!, keep: false);

        Assertions.AssertString(Installed("tool")).Contains("version=\"1.0.0\"");
        Assertions.AssertObject(UpdateJournal.Load(asked!.Directory).State).IsEqual(UpdatePhase.RolledBack);
    }

    /// <summary>Does what an update transaction asks of the editor, without one: builds are recorded and succeed unless told.</summary>
    private sealed class RecordingUpdateHost(LifecycleFixture fixture) : IUpdateHost
    {
        public bool UiAvailable { get; set; }
        public string BuildFailurePolicy => "ask";
        public bool RestartAlways => false;
        public Func<bool> BuildFails { get; set; } = () => false;
        public List<string> Toggles { get; } = [];
        public bool IsEnabled(string slug) => fixture.Host.IsPluginEnabled(slug);
        public bool IsManaged(string slug) => false;
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) { }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { }
        public void Reconcile(UpdateJournal journal, bool rollback) { }
        public void SetPlainEnabled(string slug, bool enabled)
        {
            Toggles.Add($"{slug} {enabled}");
            fixture.Host.SetPluginEnabled(slug, enabled);
        }
        public void CloseScenes() { }
        public void Scan() { }
        public BuildOutcome Build() => BuildFails() ? new(1, ["error CS0103: missing"]) : new(0, []);
        public void RequestReload(UpdateJournal journal) { }
        public bool Verify(UpdatePluginJournal plugin) => true;
        public void Log(string message) => fixture.Log.Add("log: update: " + message);
    }
}
#endif
