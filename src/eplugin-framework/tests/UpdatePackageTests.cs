using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdatePackageTests
{
    private string _root = null!;
    [BeforeTest] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "package-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [AfterTest] public void Cleanup() { Directory.Delete(_root, true); }

    [TestCase]
    public void ExtractionSelectsOnlyAddonAndRefusesTraversalOrAmbiguity()
    {
        var zip = Zip(("repo/addons/plugin/plugin.cfg", Config("2.0.0")), ("repo/addons/plugin/plugin.gd", "extends EditorPlugin"), ("repo/other.txt", "ignore"));
        var stage = Path.Combine(_root, "plugin");
        SafeZipExtractor.Extract(zip, "plugin", stage, CancellationToken.None);
        Assertions.AssertBool(File.Exists(Path.Combine(stage, "plugin.gd"))).IsTrue();
        Assertions.AssertInt(Directory.GetFiles(stage).Length).IsEqual(2);
        foreach (var path in new[] { "../evil", "a/../../evil", "/evil", "C:\\evil", "a\\..\\evil", "CON.txt", "trailing. " })
        {
            var bad = Zip(("plugin.cfg", Config("2.0.0")), (path, "bad"));
            Assertions.AssertBool(Refused(() => SafeZipExtractor.Extract(bad, "plugin", Path.Combine(_root, Guid.NewGuid().ToString()), CancellationToken.None))).IsTrue();
        }
        var ambiguous = Zip(("one/plugin.cfg", Config("2.0.0")), ("two/plugin.cfg", Config("2.0.0")));
        Assertions.AssertBool(Refused(() => SafeZipExtractor.Extract(ambiguous, "plugin", Path.Combine(_root, "ambiguous"), CancellationToken.None))).IsTrue();
    }

    [TestCase]
    public void ExtractionTakesTheOneRootPluginWithItsSubPlugins()
    {
        bool Extracts(string zip, bool requireSlugFolder = false) =>
            !Refused(() => SafeZipExtractor.Extract(zip, "plugin", Path.Combine(_root, Guid.NewGuid().ToString("N")), CancellationToken.None, requireSlugFolder));
        var withSub = Zip(("repo/addons/plugin/plugin.cfg", Config("2.0.0")), ("repo/addons/plugin/sub/plugin.cfg", Config("1.0.0")));
        var stage = Path.Combine(_root, "plugin");
        SafeZipExtractor.Extract(withSub, "plugin", stage, CancellationToken.None, requireSlugFolder: true);
        Assertions.AssertBool(File.Exists(Path.Combine(stage, "sub", "plugin.cfg"))).IsTrue();

        // Another plugin next to the root, or anywhere outside it, makes the package ambiguous.
        Assertions.AssertBool(Extracts(Zip(("addons/plugin/plugin.cfg", Config("2.0.0")), ("addons/other/plugin.cfg", Config("2.0.0"))))).IsFalse();
        Assertions.AssertBool(Extracts(Zip(("addons/plugin/plugin.cfg", Config("2.0.0")), ("tests/addons/gdUnit4/plugin.cfg", Config("2.0.0"))))).IsFalse();
        Assertions.AssertBool(Extracts(Zip(("repo/addons/other/plugin.cfg", Config("2.0.0"))))).IsFalse();
        // A download may wrap the plugin in one folder of any name or have it at the archive root; a local package may not.
        foreach (var loose in new[] { Zip(("plugin-2.0/plugin.cfg", Config("2.0.0"))), Zip(("plugin.cfg", Config("2.0.0"))) })
        {
            Assertions.AssertBool(Extracts(loose)).IsTrue();
            Assertions.AssertBool(Extracts(loose, requireSlugFolder: true)).IsFalse();
        }
    }

    [TestCase]
    public void SymlinksAndGitFilesAreSkippedButGitmodulesRefused()
    {
        var zip = Zip(("plugin.cfg", Config("2.0.0")), (".git/config", "bad"), ("link", "../outside"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.GetEntry("link")!.ExternalAttributes = 0xA000 << 16;
        var stage = Path.Combine(_root, "plugin");
        SafeZipExtractor.Extract(zip, "plugin", stage, CancellationToken.None);
        Assertions.AssertBool(File.Exists(Path.Combine(stage, "link"))).IsFalse();
        Assertions.AssertBool(Directory.Exists(Path.Combine(stage, ".git"))).IsFalse();
        var forbidden = Zip(("plugin.cfg", Config("2.0.0")), (".gitmodules", "bad"));
        Assertions.AssertBool(Refused(() => SafeZipExtractor.Extract(forbidden, "plugin", Path.Combine(_root, "refused"), CancellationToken.None))).IsTrue();
    }

    [TestCase]
    public void ValidatorChecksVersionScriptNativePayloadAndBlockedState()
    {
        var installed = Path.Combine(_root, "installed");
        var stage = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.gd"), "extends EditorPlugin");
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", candidate.SourceUrl, installed);
        var validator = new AddonPackageValidator();
        Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).IsValid).IsTrue();
        File.WriteAllText(Path.Combine(stage, "native.gdextension"), "native");
        Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).Findings.Any(f => f.Code == "R10")).IsTrue();
        File.Delete(Path.Combine(stage, "native.gdextension"));
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("1.0.0").Replace("plugin.gd", "../outside.gd"));
        var refused = validator.Validate(new(target with { IsBlocked = true }, candidate, stage));
        foreach (var rule in new[] { "R4", "R6", "R17" }) Assertions.AssertBool(refused.Findings.Any(f => f.Code == rule && f.Severity == FindingSeverity.Error)).IsTrue();
    }

    [TestCase]
    public void DowngradeNeedsExplicitChoiceAndNeverAppliesToTheFramework()
    {
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("2.0.0"));
        foreach (var slug in new[] { "plugin", "ePlugin" })
        {
            var stage = Path.Combine(_root, slug);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("1.0.0"));
            File.WriteAllText(Path.Combine(stage, "plugin.gd"), "extends EditorPlugin");
            var candidate = new UpdateCandidate(slug, "Plugin", "2.0.0", "1.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));
            var target = new PluginUpdateTarget(slug, "Plugin", "2.0.0", candidate.SourceUrl, installed);
            var validator = new AddonPackageValidator();
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).IsValid).IsFalse();
            var chosen = validator.Validate(new(target, candidate, stage, AllowDowngrade: true));
            Assertions.AssertBool(chosen.IsValid).IsEqual(slug == "plugin");
            if (slug == "plugin") Assertions.AssertBool(chosen.Findings.Any(f => f.Code == "R6" && f.Severity == FindingSeverity.Info)).IsTrue();
            File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage, AllowDowngrade: true)).IsValid).IsFalse();
        }
    }

    [TestCase]
    public void SameVersionNeedsExplicitReinstall()
    {
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("2.0.0"));
        foreach (var slug in new[] { "plugin", "ePlugin" })
        {
            var stage = Path.Combine(_root, slug);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0"));
            File.WriteAllText(Path.Combine(stage, "plugin.gd"), "extends EditorPlugin");
            var candidate = new UpdateCandidate(slug, "Plugin", "2.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));
            var target = new PluginUpdateTarget(slug, "Plugin", "2.0.0", candidate.SourceUrl, installed);
            var validator = new AddonPackageValidator();
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).IsValid).IsFalse();
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage, AllowDowngrade: true)).IsValid).IsFalse();
            var reinstall = validator.Validate(new(target, candidate, stage, AllowReinstall: true));
            Assertions.AssertBool(reinstall.IsValid).IsTrue();
            Assertions.AssertBool(reinstall.Findings.Any(f => f.Code == "R6" && f.Severity == FindingSeverity.Info)).IsTrue();
            // a reinstall never permits a downgrade
            File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("1.0.0"));
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage, AllowReinstall: true)).IsValid).IsFalse();
        }
    }

    [TestCase]
    public void HostChangeRequiresTrustAndNewProjectFilesAreRefused()
    {
        var installed = Path.Combine(_root, "installed"); var stage = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0").Replace("example.org", "new.example"));
        File.WriteAllText(Path.Combine(stage, "plugin.gd"), "extends EditorPlugin");
        File.WriteAllText(Path.Combine(stage, "new.csproj"), "<Project />");
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", installed);
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", target.UpdateUrl, null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var findings = new AddonPackageValidator().Validate(new(target, candidate, stage)).Findings;
        Assertions.AssertBool(findings.Any(f => f.Code == "R9" && f.RequiresTrust)).IsTrue();
        Assertions.AssertBool(findings.Any(f => f.Code == "R11" && f.Severity == FindingSeverity.Error)).IsTrue();
        // with an update site set by the project, checks keep using it, so the new host needs no trust
        var overridden = new AddonPackageValidator().Validate(new(target with { OverrideUrl = "https://github.com/fork/plugin/releases" }, candidate, stage)).Findings
            .Single(f => f.Code == "R9");
        Assertions.AssertBool(overridden.RequiresTrust).IsFalse();
        Assertions.AssertString(overridden.Message).Contains("https://github.com/fork/plugin/releases");
    }

    [TestCase]
    public void UidsFollowVisiblePathsAndAuthorUidsWin()
    {
        var backup = Path.Combine(_root, "backup/src"); var staged = Path.Combine(_root, "plugin/.src");
        Directory.CreateDirectory(backup); Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(backup, "Code.cs.uid"), "uid://old");
        File.WriteAllText(Path.Combine(staged, "Code.cs"), "code");
        var map = UidMap.Collect(Path.Combine(_root, "backup"));
        UidMap.Restore(Path.Combine(_root, "plugin"), map);
        Assertions.AssertString(File.ReadAllText(Path.Combine(staged, "Code.cs.uid"))).IsEqual("uid://old");
        File.WriteAllText(Path.Combine(staged, "Code.cs.uid"), "uid://author");
        UidMap.Restore(Path.Combine(_root, "plugin"), map);
        Assertions.AssertString(File.ReadAllText(Path.Combine(staged, "Code.cs.uid"))).IsEqual("uid://author");
    }

    [TestCase]
    public void ValidatorRejectsEmptyMetadataAndChangedSlug()
    {
        var installed = Path.Combine(_root, "installed"); var stage = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", installed);
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", target.UpdateUrl, null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var validator = new AddonPackageValidator();
        var empty = validator.Validate(new(target, candidate, stage));
        foreach (var rule in new[] { "R1", "R2", "R3" }) Assertions.AssertBool(empty.Findings.Any(f => f.Code == rule && f.Severity == FindingSeverity.Error)).IsTrue();
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0")); File.WriteAllText(Path.Combine(stage, "plugin.gd"), "script");
        Assertions.AssertBool(validator.Validate(new(target, candidate with { Slug = "changed" }, stage)).Findings.Any(f => f.Code == "R5")).IsTrue();
    }
    [TestCase]
    public void ValidatorWarnsAboutActualVersionNameAndLanguageAndRejectsReadOnlyState()
    {
        var installed = Path.Combine(_root, "installed"); var stage = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("3.0.0").Replace("Plugin", "Renamed").Replace("plugin.gd", "plugin.cs"));
        File.WriteAllText(Path.Combine(stage, "plugin.cs"), "C# script");
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", installed, StoreReadOnly: true);
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", target.UpdateUrl, null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var result = new AddonPackageValidator().Validate(new(target, candidate, stage));
        foreach (var rule in new[] { "R7", "R8", "R13", "R14" }) Assertions.AssertBool(result.Findings.Any(f => f.Code == rule && f.Severity == FindingSeverity.Warning)).IsTrue();
        Assertions.AssertBool(result.Findings.Any(f => f.Code == "R17" && f.Severity == FindingSeverity.Error)).IsTrue();
        Assertions.AssertBool(result.ContainsCSharp).IsTrue();
    }
    [TestCase]
    public void ValidatorRefusesOversizedFilesAndNewProjectConfigsButAllowsExistingProjects()
    {
        var installed = Path.Combine(_root, "installed"); var stage = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(installed, "plugin.cfg"), Config("1.0.0"));
        File.WriteAllText(Path.Combine(installed, "Existing.csproj"), "old project");
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), Config("2.0.0")); File.WriteAllText(Path.Combine(stage, "plugin.gd"), "script");
        File.WriteAllText(Path.Combine(stage, "Existing.csproj"), "updated project");
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", installed);
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", target.UpdateUrl, null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var validator = new AddonPackageValidator();
        Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).Findings.Any(f => f.Code == "R11")).IsFalse();
        foreach (var name in new[] { "project.godot", "nuget.config", "New.csproj", "New.sln", "New.slnx", ".gitmodules" })
        {
            var path = Path.Combine(stage, name); File.WriteAllText(path, "forbidden");
            Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).Findings.Any(f => f.Code == "R11")).IsTrue();
            File.Delete(path);
        }
        using (var sparse = File.Create(Path.Combine(stage, "oversized.bin"))) sparse.SetLength(PackageFiles.MaximumFile + 1);
        Assertions.AssertBool(validator.Validate(new(target, candidate, stage)).Findings.Any(f => f.Code == "R12")).IsTrue();
    }

    private string Zip(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
        return path;
    }
    private static bool Refused(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
    private static string Config(string version) => $"[plugin]\nname=\"Plugin\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\nupdate_url=\"https://example.org/releases\"\n";
}
