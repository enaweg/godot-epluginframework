#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Updates of plugins that ship a GDExtension: reading <c>.gdextension</c> files, validating packages, replacing files
/// next to Godot's '~' runtime copies, and reloading in place or restarting.
/// </summary>
[TestSuite]
public class GDExtensionPluginTests
{
    private const string Slug = "native";
    private string _root = null!;
    private PluginStateStore _store = null!;
    private NativeHost _host = null!;
    private UpdateApplier _applier = null!;

    private string Installed => Path.Combine(_root, "addons", Slug);

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "gdextension-" + Guid.NewGuid().ToString("N"));
        WritePlugin(Installed, "1.0.0", "old library", reloadable: true);
        // the copy the editor on Windows loads instead of the library, and its debug symbols
        File.WriteAllText(Path.Combine(Installed, "bin/~native.dll"), "runtime copy");
        File.WriteAllText(Path.Combine(Installed, "bin/~native_0.pdb"), "runtime symbols");
        _store = new(Path.Combine(_root, "addons/eplugin-state.json"), new NullLogger());
        _store.Load();
        _store.TryCreateBaseline([new(Slug, "1.0.0", PersistedPluginState.Activated)]);
        _host = new NativeHost();
        _applier = new(_root, _store, _host, new MemoryStore());
        GDExtensionPluginWorkflow.EditorSession = "this-editor";
    }

    [AfterTest]
    public void Cleanup()
    {
        GDExtensionPluginWorkflow.EditorSession = "this-editor";
        Directory.Delete(_root, true);
    }

    private static void WritePlugin(string directory, string version, string library, bool reloadable, string extension = "native.gdextension")
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        File.WriteAllText(Path.Combine(directory, "plugin.cfg"), $"[plugin]\nname=\"Native\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n");
        File.WriteAllText(Path.Combine(directory, "plugin.gd"), "@tool\nextends EditorPlugin\n");
        File.WriteAllText(Path.Combine(directory, extension), Extension(reloadable));
        foreach (var name in new[] { "native.dll", "libnative.so", "libnative.dylib" }) File.WriteAllText(Path.Combine(directory, "bin", name), library);
    }

    private static string Extension(bool reloadable) => $"""
        [configuration]
        entry_symbol = "native_init"
        compatibility_minimum = "4.4"
        reloadable = {(reloadable ? "true" : "false")} ; hot reload

        [libraries]
        windows = "res://addons/native/bin/native.dll"
        linux = "bin/libnative.so"
        macos = "bin/libnative.dylib"
        """;

    private (ValidatedPackage Package, string Directory) Stage(string version, string library, bool reloadable, Action<string>? change = null,
        PluginKind installedKind = PluginKind.Unknown)
    {
        var directory = Path.Combine(_root, ".godot/eplugin/updates/" + Guid.NewGuid().ToString("N")[..8]);
        var stage = Path.Combine(directory, "staging", Slug);
        WritePlugin(stage, version, library, reloadable);
        change?.Invoke(stage);
        var candidate = new UpdateCandidate(Slug, "Native", "1.0.0", version, "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/native.zip", Slug));
        var validated = new AddonPackageValidator().Validate(new(new(Slug, "Native", "1.0.0", null, Installed, Kind: installedKind), candidate, stage));
        Assertions.AssertBool(validated.IsValid).OverrideFailureMessage(string.Join("; ", validated.Findings.Select(f => f.Message))).IsTrue();
        return (validated, directory);
    }

    [TestCase]
    public static void ReadsReloadableLibrariesAndDependencies()
    {
        var config = GDExtensionConfig.Parse("""
            [configuration]
            entry_symbol = "init"
            reloadable = true

            [libraries]
            windows.debug.x86_64 = "res://addons/native/bin/native.dll" ; comment
            "linux.x86_64" = "bin/libnative.so"

            [dependencies]
            windows.debug.x86_64 = {
                "res://addons/native/bin/dep.dll" : "",
                "bin/other.dll" : "sub"
            }
            linux.x86_64 = {}
            """);

        Assertions.AssertBool(config.Reloadable).IsTrue();
        Assertions.AssertArray(config.Libraries.Select(l => l.Feature + "=" + l.Path).ToArray())
            .ContainsExactly("windows.debug.x86_64=res://addons/native/bin/native.dll", "linux.x86_64=bin/libnative.so");
        Assertions.AssertArray(config.Dependencies.Select(l => l.Path).ToArray()).ContainsExactly("res://addons/native/bin/dep.dll", "bin/other.dll");
        Assertions.AssertBool(GDExtensionConfig.Parse("[configuration]\nreloadable = false\n").Reloadable).IsFalse();
        Assertions.AssertBool(GDExtensionConfig.Parse("[configuration]\n").Reloadable).IsFalse();
    }

    [TestCase]
    public static void ResolvesLibrariesOnlyInsideThePluginFolder()
    {
        Assertions.AssertString(GDExtensionConfig.InsidePlugin(Slug, "native.gdextension", "res://addons/native/bin/a.dll")).IsEqual("bin/a.dll");
        Assertions.AssertString(GDExtensionConfig.InsidePlugin(Slug, "ext/native.gdextension", "../bin/a.dll")).IsEqual("bin/a.dll");
        Assertions.AssertString(GDExtensionConfig.InsidePlugin(Slug, "ext/native.gdextension", "a.dll")).IsEqual("ext/a.dll");
        foreach (var outside in new[] { "../../other/a.dll", "res://bin/a.dll", "res://addons/other/a.dll", "C:/libs/a.dll", "/usr/lib/a.so", "uid://abc" })
            Assertions.AssertString(GDExtensionConfig.InsidePlugin(Slug, "native.gdextension", outside)).IsNull();
    }

    [TestCase]
    public static void ReloadsInPlaceOnlyWhenTheSameExtensionsStayReloadable()
    {
        var reloadable = GDExtensionConfig.Parse("[configuration]\nreloadable=true\n");
        var fixedLibrary = GDExtensionConfig.Parse("[configuration]\nreloadable=false\n");
        Dictionary<string, GDExtensionConfig?> Of(params (string, GDExtensionConfig?)[] entries) => entries.ToDictionary(e => e.Item1, e => e.Item2);
        Assertions.AssertBool(GDExtensionConfig.ReloadsInPlace(Of(("a.gdextension", reloadable)), Of(("a.gdextension", reloadable)))).IsTrue();
        Assertions.AssertBool(GDExtensionConfig.ReloadsInPlace(Of(("a.gdextension", reloadable)), Of(("a.gdextension", fixedLibrary)))).IsFalse();
        Assertions.AssertBool(GDExtensionConfig.ReloadsInPlace(Of(("a.gdextension", reloadable)), Of(("b.gdextension", reloadable)))).IsFalse();
        Assertions.AssertBool(GDExtensionConfig.ReloadsInPlace(Of(), Of(("a.gdextension", reloadable)))).IsFalse();
        Assertions.AssertBool(GDExtensionConfig.ReloadsInPlace(Of(("a.gdextension", null)), Of(("a.gdextension", reloadable)))).IsFalse();
    }

    [TestCase]
    public void ValidationFindsLibrariesOutsideTheFolderAndMissingOnes()
    {
        var outside = Validate(stage => File.WriteAllText(Path.Combine(stage, "native.gdextension"),
            Extension(true) + "\n[dependencies]\nwindows = { \"res://shared/dep.dll\" : \"\" }\n"));
        Assertions.AssertObject(outside.Kind).IsEqual(PluginKind.GDExtension);
        Assertions.AssertBool(outside.Findings.Any(f => f.Code == "R18" && f.Severity == FindingSeverity.Error && f.Message.Contains("res://shared/dep.dll"))).IsTrue();

        var missing = Validate(stage => { foreach (var library in Directory.GetFiles(Path.Combine(stage, "bin"))) File.Delete(library); });
        Assertions.AssertBool(missing.Findings.Any(f => f.Code == "R19" && f.Severity == FindingSeverity.Error)).IsTrue();

        var none = Validate(stage => File.WriteAllText(Path.Combine(stage, "native.gdextension"), "[configuration]\nreloadable=true\n[libraries]\nweb = \"bin/native.wasm\"\n"));
        Assertions.AssertBool(none.Findings.Any(f => f.Code == "R19" && f.Severity == FindingSeverity.Warning)).IsTrue();

        var restart = Validate(stage => File.WriteAllText(Path.Combine(stage, "native.gdextension"), Extension(false)));
        Assertions.AssertBool(restart.IsValid).IsTrue();
        Assertions.AssertString(restart.Findings.Single(f => f.Code == "R21").Message).Contains("restarts");
        Assertions.AssertString(Validate(null).Findings.Single(f => f.Code == "R21").Message).Contains("reloaded in place");
    }

    private ValidatedPackage Validate(Action<string>? change)
    {
        var stage = Path.Combine(_root, "validate", Slug);
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        WritePlugin(stage, "2.0.0", "new library", reloadable: true);
        change?.Invoke(stage);
        var candidate = new UpdateCandidate(Slug, "Native", "1.0.0", "2.0.0", null, null, null, new ZipPackageRef("https://example.org/native.zip", Slug));
        return new AddonPackageValidator().Validate(new(new(Slug, "Native", "1.0.0", null, Installed), candidate, stage));
    }

    [TestCase]
    public void FileSwapKeepsRuntimeCopiesAndRestoresTheBackup()
    {
        var staging = Path.Combine(_root, "staging", Slug);
        WritePlugin(staging, "2.0.0", "new library", reloadable: true);
        File.WriteAllText(Path.Combine(staging, "bin/added.txt"), "added");
        File.WriteAllText(Path.Combine(Installed, "removed.txt"), "removed");
        Directory.CreateDirectory(Path.Combine(Installed, "empty"));
        var backup = Path.Combine(_root, "backup", Slug);
        var discard = Path.Combine(_root, "discarded", Slug);
        // the editor keeps its runtime copy open, which keeps the folder from being moved on Windows
        using (new FileStream(Path.Combine(Installed, "bin/~native.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Assertions.AssertThrown(() => Directory.Move(Installed, backup)).IsInstanceOf<SystemException>();
            }
            Assertions.AssertInt(AddonFileSwap.Instance.Install(Installed, staging, backup, discard)).IsEqual(0);
        }

        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("new library");
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "bin/added.txt"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "removed.txt"))).IsFalse();
        Assertions.AssertBool(Directory.Exists(Path.Combine(Installed, "empty"))).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        Assertions.AssertBool(Directory.Exists(staging)).IsFalse();
        // the backup holds the old version complete, without the runtime copies
        Assertions.AssertString(File.ReadAllText(Path.Combine(backup, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertBool(File.Exists(Path.Combine(backup, "removed.txt"))).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(backup, "empty"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(backup, "bin/~native.dll"))).IsFalse();

        using (new FileStream(Path.Combine(Installed, "bin/~native.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assertions.AssertInt(AddonFileSwap.Instance.Restore(Installed, backup, discard)).IsEqual(0);

        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "removed.txt"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "bin/added.txt"))).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        Assertions.AssertBool(Directory.Exists(backup)).IsFalse();
    }

    [TestCase]
    public void AReloadableExtensionIsReloadedInPlaceWithoutRestart()
    {
        var (package, directory) = Stage("2.0.0", "new library", reloadable: true);

        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.Completed);

        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("new library");
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        Assertions.AssertArray(_host.Reloaded.ToArray()).ContainsExactly("res://addons/native/native.gdextension");
        Assertions.AssertInt(_host.Restarts).IsEqual(0);
        Assertions.AssertInt(_host.Builds).IsEqual(0);
        // disabled while its files were replaced, enabled again
        Assertions.AssertArray(_host.Toggles.ToArray()).ContainsExactly("native False", "native True");
        Assertions.AssertString(_store.GetShared(Slug)!.Version).IsEqual("2.0.0");
        Assertions.AssertBool(_host.Logs.Any(l => l.StartsWith("GDExtension plugin native: installing 2.0.0 (file-by-file swap", StringComparison.Ordinal))).IsTrue();
    }

    [TestCase]
    public void AnExtensionThatIsNotReloadableRestartsTheEditorAfterTheCommit()
    {
        var (package, directory) = Stage("2.0.0", "new library", reloadable: false);

        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.Completed);

        Assertions.AssertInt(_host.Reloaded.Count).IsEqual(0);
        Assertions.AssertInt(_host.Restarts).IsEqual(1);
        Assertions.AssertString(_store.GetShared(Slug)!.Version).IsEqual("2.0.0");
    }

    [TestCase]
    public void AFailedReloadRestartsTheEditorInstead()
    {
        _host.ReloadWorks = false;
        var (package, directory) = Stage("2.0.0", "new library", reloadable: true);

        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.Completed);

        Assertions.AssertInt(_host.Restarts).IsEqual(1);
    }

    [TestCase]
    public void ARollbackRestoresTheLibraryAndReloadsItAgain()
    {
        _host.Verified = false;
        var (package, directory) = Stage("2.0.0", "new library", reloadable: true, stage => File.WriteAllText(Path.Combine(stage, "added.txt"), "added"));

        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);

        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "added.txt"))).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        // reloaded into the new version, then back into the old one
        Assertions.AssertInt(_host.Reloaded.Count).IsEqual(2);
        Assertions.AssertInt(_host.Restarts).IsEqual(0);
        Assertions.AssertInt(_store.LocalAttempts.Count).IsEqual(0);
    }

    [TestCase]
    public void ARollbackBeforeAnyReloadNeedsNeitherReloadNorRestart()
    {
        _host.Verified = false;
        var (package, directory) = Stage("2.0.0", "new library", reloadable: false);

        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);

        // the editor never loaded the new library, so it still runs the restored one
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertInt(_host.Reloaded.Count).IsEqual(0);
        Assertions.AssertInt(_host.Restarts).IsEqual(0);
    }

    [TestCase]
    public void AFailureDuringTheSwapRollsBackFromTheBackup()
    {
        var (package, directory) = Stage("2.0.0", "new library", reloadable: false);
        _host.OnSwap = () => throw new IOException("disk full");
        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        Assertions.AssertInt(_host.Restarts).IsEqual(0);
    }

    [TestCase]
    public void AnEPluginWithAGDExtensionSwapsItsFilesAndRestartsIntoTheNewAssembly()
    {
        _host.Managed = true;
        var (package, directory) = Stage("2.0.0", "new library", reloadable: true,
            stage => File.WriteAllText(Path.Combine(stage, "Plugin.cs"), "// ePlugin"), PluginKind.EPlugin);
        Assertions.AssertObject(package.Kind).IsEqual(PluginKind.EPlugin);

        using (new FileStream(Path.Combine(Installed, "bin/~native.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.AwaitingReload);

        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("new library");
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        var plugin = UpdateJournal.Load(directory).Plugins.Single();
        Assertions.AssertString(plugin.Kind).IsEqual("EPlugin");
        Assertions.AssertBool(plugin.NativeExtension).IsTrue();
        // stays enabled, and the restart into the interim build loads the new library: nothing is reloaded in place
        Assertions.AssertInt(_host.Toggles.Count).IsEqual(0);
        Assertions.AssertInt(_host.Reloaded.Count).IsEqual(0);
        Assertions.AssertInt(_host.Builds).IsEqual(1);
        Assertions.AssertInt(_host.Restarts).IsEqual(1);

        // a rollback restores the files the same way and restarts again
        Assertions.AssertObject(_applier.Rollback(UpdateJournal.Load(directory), "test")).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertBool(File.Exists(Path.Combine(Installed, "Plugin.cs"))).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/~native.dll"))).IsEqual("runtime copy");
        Assertions.AssertInt(_host.Restarts).IsEqual(2);
    }

    [TestCase]
    public static void JournalsWithoutAKnownKindFallBackToWhatTheyRecorded()
    {
        // a journal written before kinds were recorded still rolls back with the folder swap of a plain plugin
        var journal = new UpdatePluginJournal { Slug = Slug, IsEPlugin = false };
        Assertions.AssertObject(PluginKinds.Workflow(journal)).IsInstanceOf<UnknownPluginWorkflow>();
        journal.Kind = "GDExtension";
        Assertions.AssertObject(PluginKinds.Workflow(journal)).IsInstanceOf<GDExtensionPluginWorkflow>();
        journal.Kind = "SomethingNewer";
        Assertions.AssertObject(PluginKinds.Workflow(journal)).IsInstanceOf<UnknownPluginWorkflow>();
    }

    [TestCase]
    public void ARestartedEditorThatLoadedTheNewLibraryReloadsOrRestartsAfterARollback()
    {
        // the editor restarted into the new version, then it failed: a reloadable old version is reloaded in place
        var (package, directory) = Stage("2.0.0", "new library", reloadable: false);
        _host.Verified = false;
        _host.BeforeVerify = () => GDExtensionPluginWorkflow.EditorSession = "restarted-editor";
        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertString(File.ReadAllText(Path.Combine(Installed, "bin/native.dll"))).IsEqual("old library");
        Assertions.AssertArray(_host.Reloaded.ToArray()).ContainsExactly("res://addons/native/native.gdextension");
        Assertions.AssertInt(_host.Restarts).IsEqual(0);

        // one that is not reloadable needs another restart
        GDExtensionPluginWorkflow.EditorSession = "this-editor";
        File.WriteAllText(Path.Combine(Installed, "native.gdextension"), Extension(false));
        (package, directory) = Stage("2.0.0", "new library", reloadable: false);
        Assertions.AssertObject(_applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);
        Assertions.AssertInt(_host.Restarts).OverrideFailureMessage(string.Join(" | ", _host.Logs)).IsEqual(1);
    }

    private sealed class MemoryStore : IUpdateStateStore { public UpdateCache State { get; } = new(); public void Save() { } }

    private sealed class NativeHost : IUpdateHost
    {
        public List<string> Reloaded { get; } = [];
        public List<string> Toggles { get; } = [];
        public List<string> Logs { get; } = [];
        public bool ReloadWorks = true;
        public bool Verified = true;
        public Action? OnSwap;
        public Action? BeforeVerify;
        public int Restarts;
        public int Builds;
        public bool UiAvailable => false;
        public string BuildFailurePolicy => "rollback";
        public bool RestartAlways => false;
        public bool IsEnabled(string slug) => true;
        public bool Managed;
        public bool IsManaged(string slug) => Managed;
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) { }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) => OnSwap?.Invoke();
        public void Reconcile(UpdateJournal journal, bool rollback) { }
        public void SetPlainEnabled(string slug, bool enabled) => Toggles.Add($"{slug} {enabled}");
        public void CloseScenes() { }
        public void Scan() { }
        public BuildOutcome Build() { Builds++; return new(0, []); }
        public void RequestReload(UpdateJournal journal) => Restarts++;
        public bool ReloadExtension(string resourcePath)
        {
            if (ReloadWorks) Reloaded.Add(resourcePath);
            return ReloadWorks;
        }
        public bool Verify(UpdatePluginJournal plugin)
        {
            BeforeVerify?.Invoke();
            return Verified;
        }
        public void Log(string message) => Logs.Add(message);
    }
}
#endif
