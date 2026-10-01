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
