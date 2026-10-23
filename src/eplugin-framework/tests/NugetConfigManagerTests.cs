#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>The nuget.config of a project in a temporary folder.</summary>
[TestSuite]
public class NugetConfigManagerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "eplugin-nuget-" + Guid.NewGuid().ToString("N"));

    private static string ConfigPath => Path.Combine(Root, "nuget.config");

    [BeforeTest]
    public static void Setup() => Directory.CreateDirectory(Root);

    [AfterTest]
    public static void Cleanup()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    [TestCase]
    public static void LocalSourcesAreRelativeToTheProjectAndOtherGodotPathsAreOutsideIt()
    {
        var log = new List<string>();
        NugetConfigManager.RegisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());
        NugetConfigManager.RegisterSource(Root, "plugin_b", Path.Combine(Root, "addons", "b", ".nuget"), new NullLogger());
        Assertions.AssertBool(NugetConfigManager.RegisterSource(Root, "plugin_c", "user://feed", new RecordingLogger(log, "nuget"))).IsTrue();

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("value=\"./addons/a/.nuget\"");
        Assertions.AssertThat(content).Contains("value=\"./addons/b/.nuget\"");
        Assertions.AssertThat(content).NotContains("plugin_c");
        Assertions.AssertInt(log.Count(l => l.Contains("is outside the project"))).IsEqual(1);
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void RegisterSourceCreatesFileWithManagedEntry()
    {
        NugetConfigManager.RegisterSource(Root, "sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsTrue();

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("ePlugin-managed used-by=\"sample_plugin\"");
        Assertions.AssertThat(content).Contains("./addons/sample_plugin/.nuget");
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void RegisterSourceSameSourceFromTwoPluginsSharesOneEntry()
    {
        NugetConfigManager.RegisterSource(Root, "plugin_a", "res://addons/shared/.nuget", new NullLogger());
        NugetConfigManager.RegisterSource(Root, "plugin_b", "res://addons/shared/.nuget", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertInt(CountOccurrences(content, "<add key=\"ePlugin-")).IsEqual(1);
        Assertions.AssertThat(content).Contains("used-by=\"plugin_a,plugin_b\"");
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void UnregisterSourceOneOfTwoUsersKeepsEntry()
    {
        NugetConfigManager.RegisterSource(Root, "plugin_a", "res://addons/shared/.nuget", new NullLogger());
        NugetConfigManager.RegisterSource(Root, "plugin_b", "res://addons/shared/.nuget", new NullLogger());

        NugetConfigManager.UnregisterSource(Root, "plugin_a", "res://addons/shared/.nuget", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("used-by=\"plugin_b\"");
        Assertions.AssertThat(content).NotContains("plugin_a");
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void UnregisterSourceLastUserRemovesFileWhenOtherwiseEmpty()
    {
        NugetConfigManager.RegisterSource(Root, "sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        NugetConfigManager.UnregisterSource(Root, "sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void UnregisterSourceLastUserPreservesFileWithOtherContent()
    {
        File.WriteAllText(ConfigPath,
            "<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");

        NugetConfigManager.RegisterSource(Root, "sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());
        NugetConfigManager.UnregisterSource(Root, "sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsTrue();
        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("nuget.org");
        Assertions.AssertThat(content).NotContains("ePlugin-managed");
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void RegisterSourceExistingIdenticalUserSourceIsNotDuplicatedOrOwned()
    {
        File.WriteAllText(ConfigPath,
            "<configuration><packageSources><add key=\"my-feed\" value=\"https://example.com/feed/index.json\" /></packageSources></configuration>");

        NugetConfigManager.RegisterSource(Root, "sample_plugin", "https://example.com/feed/index.json", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertInt(CountOccurrences(content, "<add ")).IsEqual(1);
        Assertions.AssertThat(content).NotContains("ePlugin-managed");
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void RegisterSourceUrlPersistsVerbatim()
    {
        const string url = "https://example.com/feed/index.json";

        NugetConfigManager.RegisterSource(Root, "sample_plugin", url, new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains(url);
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void SourcesOutsideTheProjectAreNotTracked()
    {
        var log = new List<string>();
        var outside = Path.Combine(Path.GetTempPath(), "eplugin-feed");

        Assertions.AssertBool(NugetConfigManager.RegisterSource(Root, "sample_plugin", outside, new RecordingLogger(log, "nuget"))).IsTrue();
        Assertions.AssertBool(NugetConfigManager.RegisterSource(Root, "sample_plugin", " ", new RecordingLogger(log, "nuget"))).IsTrue();
        Assertions.AssertBool(NugetConfigManager.UnregisterSource(Root, "sample_plugin", outside, new RecordingLogger(log, "nuget"))).IsTrue();

        Assertions.AssertBool(File.Exists(ConfigPath)).IsFalse();
        Assertions.AssertInt(log.Count(l => l.Contains("is outside the project"))).IsEqual(2);
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void UnregisteringASourceThatIsNotListedLeavesTheFileAlone()
    {
        Assertions.AssertBool(NugetConfigManager.UnregisterSource(Root, "sample_plugin", "https://example.com/feed/index.json", new NullLogger())).IsTrue();
        Assertions.AssertBool(File.Exists(ConfigPath)).IsFalse();

        const string config = "<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>";
        File.WriteAllText(ConfigPath, config);
        Assertions.AssertBool(NugetConfigManager.UnregisterSource(Root, "sample_plugin", "https://example.com/feed/index.json", new NullLogger())).IsTrue();
        Assertions.AssertString(File.ReadAllText(ConfigPath)).IsEqual(config);
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void AMalformedFileIsReportedAndKept()
    {
        var log = new List<string>();
        File.WriteAllText(ConfigPath, "<configuration><packageSources>");

        Assertions.AssertBool(NugetConfigManager.RegisterSource(Root, "sample_plugin", "https://example.com/feed/index.json", new RecordingLogger(log, "nuget"))).IsFalse();
        Assertions.AssertBool(NugetConfigManager.UnregisterSource(Root, "sample_plugin", "https://example.com/feed/index.json", new RecordingLogger(log, "nuget"))).IsFalse();

        Assertions.AssertString(File.ReadAllText(ConfigPath)).IsEqual("<configuration><packageSources>");
        Assertions.AssertBool(log.Any(l => l.StartsWith("error: nuget: Failed to update nuget.config for source", StringComparison.Ordinal))).IsTrue();
        Assertions.AssertBool(log.Any(l => l.StartsWith("error: nuget: Failed to update nuget.config while removing source", StringComparison.Ordinal))).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void AManagedEntryWhoseCommentWasEditedGetsItsMarkerBack()
    {
        const string url = "https://example.com/feed/index.json";
        NugetConfigManager.RegisterSource(Root, "plugin_a", url, new NullLogger());
        // someone removed the marker comment, or replaced it with their own
        foreach (var replacement in new[] { "", "<!-- reviewed by the team -->" })
        {
            var content = File.ReadAllText(ConfigPath);
            var start = content.IndexOf("<!--", StringComparison.Ordinal);
            var end = content.IndexOf("-->", start, StringComparison.Ordinal) + 3;
            File.WriteAllText(ConfigPath, content[..start] + replacement + content[end..]);

            NugetConfigManager.RegisterSource(Root, "plugin_b", url, new NullLogger());

            content = File.ReadAllText(ConfigPath);
            Assertions.AssertInt(CountOccurrences(content, "<add key=\"ePlugin-")).IsEqual(1);
            Assertions.AssertThat(content).Contains("used-by=\"plugin_b\"");
            Assertions.AssertThat(content).NotContains("plugin_a");
        }
    }

    [TestCase]
    public static void AProjectsNuGetConfigUnderAnotherCasingIsEditedInsteadOfHidden()
    {
        var own = Path.Combine(Root, "NuGet.Config");
        File.WriteAllText(own, "<configuration><packageSources><clear /><add key=\"team\" value=\"https://feed.example.com/v3/index.json\" /></packageSources></configuration>");

        NugetConfigManager.RegisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());

        Assertions.AssertThat(Directory.GetFiles(Root).Select(Path.GetFileName).ToArray()).ContainsExactly("NuGet.Config");
        var content = File.ReadAllText(own);
        Assertions.AssertThat(content).Contains("key=\"team\"");
        Assertions.AssertThat(content).Contains("value=\"./addons/a/.nuget\"");

        NugetConfigManager.UnregisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());

        Assertions.AssertThat(Directory.GetFiles(Root).Select(Path.GetFileName).ToArray()).ContainsExactly("NuGet.Config");
        Assertions.AssertThat(File.ReadAllText(own)).NotContains("addons/a");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
#endif
