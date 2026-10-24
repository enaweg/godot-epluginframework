#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Install from ZIP: reading which add-on a ZIP file holds, staging and validating it as a new add-on, installing it, and
/// the ePlugin Manager's decision what a chosen file installs.
/// </summary>
[TestSuite]
public class AddonInstallTests
{
    private string _root = null!;
    private string Addons => Path.Combine(_root, "addons");

    [BeforeTest] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "addon-install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [AfterTest] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestCase]
    public void AnAssetLibraryZipInstallsItsPluginFolderOnly()
    {
        var zip = Zip(("repo-main/addons/plugin/plugin.cfg", Config("2.0")), ("repo-main/addons/plugin/plugin.gd", "extends EditorPlugin"),
            ("repo-main/README.md", "read me"), ("repo-main/icon.svg", "<svg/>"), ("repo-main/addons/plugin/", ""));

        var archive = AddonArchive.Read(zip);

        Assertions.AssertString(archive.Slug).IsEqual("plugin");
        Assertions.AssertString(archive.Root).IsEqual("repo-main/addons/plugin/");
        Assertions.AssertString(archive.Name).IsEqual("Plugin");
        Assertions.AssertString(archive.Version).IsEqual("2.0");
        Assertions.AssertBool(archive.IsExtensionAddon).IsFalse();
        Assertions.AssertInt(archive.Files).IsEqual(2);
        Assertions.AssertInt(archive.LeftOut).IsEqual(2);
    }

    [TestCase]
    public void AGDExtensionWithoutPluginCfgIsNamedByItsAddonsFolder()
    {
        var archive = AddonArchive.Read(Zip(("addons/servo/bin/servo.gdextension", "[configuration]"), ("addons/servo/bin/libservo.so", "library"),
            ("demo/main.tscn", "scene")));

        Assertions.AssertString(archive.Slug).IsEqual("servo");
        Assertions.AssertString(archive.Root).IsEqual("addons/servo/");
        Assertions.AssertString(archive.Name).IsEqual("servo");
        Assertions.AssertString(archive.Version).IsEqual("");
        Assertions.AssertBool(archive.IsExtensionAddon).IsTrue();
        Assertions.AssertInt(archive.LeftOut).IsEqual(1);
    }

    [TestCase]
    public void ArchivesTheUpdateSystemCouldNotUpdateAreRefused()
    {
        string? Refusal(params (string Path, string Content)[] files)
        {
            try { AddonArchive.Read(Zip(files)); return null; }
            catch (InvalidDataException ex) { return ex.Message; }
        }

        // nothing names the folder of a plugin at the archive root
        Assertions.AssertString(Refusal(("plugin.cfg", Config("1.0.0")))).Contains("root of the ZIP file");
        Assertions.AssertString(Refusal(("addons/a/plugin.cfg", Config("1.0.0")), ("addons/b/plugin.cfg", Config("1.0.0")))).Contains("several plugin roots");
        Assertions.AssertString(Refusal(("addons/a/plugin.cfg", "[plugin]\nname=\"A\"\nversion=\"latest\"\nscript=\"a.gd\"\n"))).Contains("no comparable version");
        Assertions.AssertString(Refusal(("addons/.a/plugin.cfg", Config("1.0.0")))).Contains("hidden");
        Assertions.AssertString(Refusal(("readme.txt", "no add-on"))).Contains("neither a plugin.cfg nor a GDExtension");
        Assertions.AssertString(Refusal(("servo/servo.gdextension", "[configuration]"))).Contains("not in an addons/<name>/ folder");
        Assertions.AssertString(Refusal(("addons/a/a.gdextension", "[configuration]"), ("addons/b/b.gdextension", "[configuration]"))).Contains("several GDExtensions: a, b");
        Assertions.AssertString(Refusal(("../evil/plugin.cfg", Config("1.0.0")))).IsNotNull();
        var text = Path.Combine(_root, "plugin.txt");
        File.WriteAllText(text, "");
        Assertions.AssertThrown(() => AddonArchive.Read(text)).IsInstanceOf<InvalidDataException>();
    }

    [TestCase]
    public void ANewPluginIsValidatedWithoutAnInstalledVersion()
    {
        var package = Stage(Zip(("addons/plugin/plugin.cfg", Config("1.0.0")), ("addons/plugin/plugin.gd", "extends EditorPlugin")));

        Assertions.AssertBool(package.IsValid).IsTrue();
        Assertions.AssertObject(package.Kind).IsEqual(PluginKind.GDScript);
        Assertions.AssertBool(package.ContainsCSharp).IsFalse();
        // there is no installed version, name or update site to compare with
        Assertions.AssertThat(package.Findings.Select(f => f.Code).ToArray()).NotContains("R6", "R7", "R8", "R9", "R13", "R14");
        Assertions.AssertBool(File.Exists(Path.Combine(package.StagingDir, "plugin.gd"))).IsTrue();
    }

    [TestCase]
    public void ANewAddonDoesNotReplaceAFolderOfTheProject()
    {
        Directory.CreateDirectory(Path.Combine(Addons, "plugin"));

        var package = Stage(Zip(("addons/plugin/plugin.cfg", Config("1.0.0")), ("addons/plugin/plugin.gd", "extends EditorPlugin")));

        Assertions.AssertBool(package.IsValid).IsFalse();
        Assertions.AssertString(package.Findings.Single(f => f.Severity == FindingSeverity.Error).Code).IsEqual("R26");
    }

    [TestCase]
    public void ANewGDExtensionWithoutPluginCfgHasAnUnknownVersion()
    {
        // Tests do not run on the editor thread, which staging asks for the editor's version.
        GDExtensionConfig.EditorVersion = (4, 4, 0);
        ValidatedPackage package;
        try
        {
            package = Stage(Zip(("addons/servo/servo.gdextension", Extension()), ("addons/servo/bin/servo.dll", "library"),
                ("addons/servo/bin/libservo.so", "library"), ("addons/servo/bin/libservo.dylib", "library")));
        }
        finally { GDExtensionConfig.ResetEditorVersion(); }

        Assertions.AssertBool(package.IsValid).IsTrue();
        Assertions.AssertObject(package.Kind).IsEqual(PluginKind.GDExtensionAddon);
        Assertions.AssertString(package.Findings.Single(f => f.Code == "R6").Message).Contains("version stays unknown");
        // how an update reloads libraries does not apply to a new add-on
        Assertions.AssertBool(package.Findings.Any(f => f.Code is "R21" or "R24")).IsFalse();
    }

    [TestCase]
    public void APluginWithoutCSharpIsMovedIntoAddonsWithoutABuild()
    {
        var package = Stage(Zip(("addons/plugin/plugin.cfg", Config("1.0.0")), ("addons/plugin/plugin.gd", "extends EditorPlugin")));
        var host = new FakeInstallHost();

        var result = new AddonInstaller(Addons, _root, host).Install(package, Transaction);

        Assertions.AssertBool(result.Installed).IsTrue();
        Assertions.AssertBool(result.Built).IsFalse();
        Assertions.AssertBool(File.Exists(Path.Combine(Addons, "plugin", "plugin.gd"))).IsTrue();
        Assertions.AssertBool(Directory.Exists(package.StagingDir)).IsFalse();
        Assertions.AssertInt(host.Builds).IsEqual(0);
        Assertions.AssertInt(host.Scans).IsEqual(1);
        Assertions.AssertString(host.Logged.Single()).IsEqual("Installed Plugin 1.0.0 into res://addons/plugin.");
    }

    [TestCase]
    public void ACSharpPluginIsBuiltOnceInstalled()
    {
        var package = Stage(CSharpZip());
        var host = new FakeInstallHost();

        var result = new AddonInstaller(Addons, _root, host).Install(package, Transaction);

        Assertions.AssertBool(result.Installed).IsTrue();
        Assertions.AssertBool(result.Built).IsTrue();
        Assertions.AssertInt(host.Builds).IsEqual(1);
        Assertions.AssertInt(host.Scans).IsEqual(1);
    }

    [TestCase]
    public void ACSharpPluginThatDoesNotCompileIsRemovedAgain()
    {
        var package = Stage(CSharpZip());
        var file = Path.Combine(Addons, "plugin", "Plugin.cs");
        var host = new FakeInstallHost { Build = new(1, [$"{file}(3,5): error CS1002: ; expected [{_root}/Project.csproj]"]) };

        var result = new AddonInstaller(Addons, _root, host).Install(package, Transaction);

        Assertions.AssertBool(result.Installed).IsFalse();
        Assertions.AssertString(result.Failure).IsEqual("Plugin does not compile in this project (1 error), e.g. addons/plugin/Plugin.cs(3): CS1002: ; expected. It was removed again.");
        Assertions.AssertBool(Directory.Exists(Path.Combine(Addons, "plugin"))).IsFalse();
        Assertions.AssertBool(File.Exists(result.BuildLog!)).IsTrue();
        Assertions.AssertInt(host.Scans).IsEqual(0);
    }

    [TestCase]
    public void APluginIsRemovedAgainWhenTheProjectDoesNotBuildWithIt()
    {
        var package = Stage(CSharpZip());
        var host = new FakeInstallHost { Build = new(1, [$"{Path.Combine(_root, "Main.cs")}(1,1): error CS0103: The name 'x' does not exist [{_root}/Project.csproj]"]) };

        var result = new AddonInstaller(Addons, _root, host).Install(package, Transaction);

        Assertions.AssertBool(result.Installed).IsFalse();
        Assertions.AssertString(result.Failure).StartsWith("The project does not build with Plugin: 1 error outside the plugin, e.g. Main.cs(1): CS0103");
        Assertions.AssertBool(Directory.Exists(Path.Combine(Addons, "plugin"))).IsFalse();
    }

    [TestCase]
    public void APluginIsRemovedAgainWhenTheBuildCannotRun()
    {
        var package = Stage(CSharpZip());
        var host = new FakeInstallHost { Throws = new InvalidOperationException("dotnet crashed") };

        var result = new AddonInstaller(Addons, _root, host).Install(package, Transaction);

        Assertions.AssertBool(result.Installed).IsFalse();
        Assertions.AssertString(result.Failure).Contains("It was removed again.");
        Assertions.AssertBool(Directory.Exists(Path.Combine(Addons, "plugin"))).IsFalse();
        Assertions.AssertString(File.ReadAllText(result.BuildLog!)).Contains("dotnet crashed");
    }

    [TestCase]
    public void AFolderCreatedSinceValidationIsNotReplaced()
    {
        var package = Stage(CSharpZip());
        Directory.CreateDirectory(Path.Combine(Addons, "plugin"));
        var host = new FakeInstallHost();

        Assertions.AssertThrown(() => new AddonInstaller(Addons, _root, host).Install(package, Transaction)).IsInstanceOf<IOException>();
        Assertions.AssertBool(Directory.Exists(package.StagingDir)).IsTrue();
        Assertions.AssertInt(host.Builds).IsEqual(0);
    }

    [TestCase]
    public void AZipFileOfAnInstalledPluginInstallsItsVersion()
    {
        Assertions.AssertBool(ZipInstall.Choose(Archive("2.0.0"), null, null).IsNew).IsTrue();
        // a folder that differs from an installed one only in case is refused on every system
        Assertions.AssertString(ZipInstall.Choose(Archive("2.0.0"), null, null, "Plugin").Refused).Contains("differs from the installed res://addons/Plugin only in case");

        var plugin = new PluginInfo("plugin", "Plugin", PluginKind.GDScript, true) { Version = "1.0.0" };
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", null, Path.Combine(Addons, "plugin"));

        var update = ZipInstall.Choose(Archive("2.0.0"), new PluginRow(plugin, null), target).Version!;
        Assertions.AssertString(update.Action).IsEqual("Update");
        Assertions.AssertObject(update.Candidate!.Package).IsEqual(new LocalZipPackageRef("/downloads/plugin.zip"));
        Assertions.AssertString(update.Candidate.InstalledVersion).IsEqual("1.0.0");
        Assertions.AssertString(ZipInstall.Choose(Archive("0.9.0"), new PluginRow(plugin, null), target).Version!.Action).IsEqual("Downgrade");
        Assertions.AssertString(ZipInstall.Choose(Archive("1.0.0"), new PluginRow(plugin, null), target).Version!.Action).IsEqual("Reinstall");

        // like a version chosen in the details: only an enabled plugin's, never an older framework
        Assertions.AssertString(ZipInstall.Choose(Archive("2.0.0"), new PluginRow(plugin with { Enabled = false }, null), null).Refused).Contains("Enable the plugin");
        var framework = new PluginInfo("plugin", "ePlugin Framework", PluginKind.Framework, true) { Version = "1.0.0" };
        Assertions.AssertString(ZipInstall.Choose(Archive("0.9.0"), new PluginRow(framework, null), target).Refused).Contains("cannot be downgraded");
        Assertions.AssertString(ZipInstall.Choose(Archive("2.0.0"), new PluginRow(plugin with { Missing = true }, null), null).Refused).Contains("Needs attention");
        // a GDExtension without plugin.cfg names no version to install over the installed one
        Assertions.AssertString(ZipInstall.Choose(Archive("") with { IsExtensionAddon = true }, new PluginRow(plugin, null), target).Refused).Contains("names no version");
    }

    [TestCase]
    public void TheConfirmationSaysWhatIsInstalled()
    {
        var package = Stage(CSharpZip());
        var archive = Archive("1.0.0") with { Name = "Plugin [b]", LeftOut = 3 };

        var text = ZipInstall.Describe(package, archive);

        Assertions.AssertString(text).StartsWith("Install [b]Plugin [lb]b] 1.0.0[/b] into res://addons/plugin?");
        Assertions.AssertString(text).Contains("[b]Type:[/b] C# plugin or ePlugin");
        Assertions.AssertString(text).Contains("3 other files of the ZIP file lie outside it and are not installed");
        Assertions.AssertString(text).Contains("removed again");
    }

    private string Transaction => Path.Combine(_root, ".godot", "eplugin", "updates", "install");

    private ValidatedPackage Stage(string zip)
    {
        var archive = AddonArchive.Read(zip);
        var target = new PluginUpdateTarget(archive.Slug, archive.Name, "", null, AddonInstaller.Target(Addons, archive.Slug),
            Kind: archive.IsExtensionAddon ? PluginKind.GDExtensionAddon : PluginKind.Unknown);
        return AddonInstaller.Stage(archive, target, Transaction, CancellationToken.None);
    }

    private static AddonArchive Archive(string version) => new("/downloads/plugin.zip", "addons/plugin/", "plugin", "Plugin", version, false, 2, 0);

    private string CSharpZip() => Zip(("addons/plugin/plugin.cfg", Config("1.0.0", "Plugin.cs")),
        ("addons/plugin/Plugin.cs", "#if TOOLS\n[Godot.Tool] public partial class Plugin : Godot.EditorPlugin { }\n#endif"));

    private string Zip(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
        return path;
    }

    private static string Config(string version, string script = "plugin.gd") =>
        $"[plugin]\nname=\"Plugin\"\nversion=\"{version}\"\nscript=\"{script}\"\nupdate_url=\"https://example.org/owner/plugin/releases\"\n";

    private static string Extension() => """
        [configuration]
        entry_symbol = "servo_init"
        compatibility_minimum = "4.4"
        reloadable = true

        [libraries]
        windows = "bin/servo.dll"
        linux = "bin/libservo.so"
        macos = "bin/libservo.dylib"
        """;

    private sealed class FakeInstallHost : IAddonInstallHost
    {
        public BuildOutcome Build { get; init; } = new(0, ["Build succeeded."]);
        public Exception? Throws { get; init; }
        public int Builds { get; private set; }
        public int Scans { get; private set; }
        public List<string> Logged { get; } = [];

        BuildOutcome IAddonInstallHost.Build()
        {
            Builds++;
            if (Throws is not null) throw Throws;
            return Build;
        }

        public void Scan() => Scans++;
        public void Log(string message) => Logged.Add(message);
    }
}
#endif
