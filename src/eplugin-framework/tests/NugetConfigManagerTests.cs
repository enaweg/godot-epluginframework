#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
            // a comment of the team's own is kept, the marker goes next to it
            if (replacement.Length > 0) Assertions.AssertThat(content).Contains(replacement);
        }

        NugetConfigManager.UnregisterSource(Root, "plugin_b", url, new NullLogger());
        var left = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(left).Contains("<!-- reviewed by the team -->");
        Assertions.AssertThat(left).NotContains("ePlugin-");
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

    [TestCase]
    public static void TheProjectsFormattingIsKeptAndOnlyTheManagedEntryChanges()
    {
        // committed and shared by the team: four spaces, CRLF, a byte order mark and a declaration without encoding
        const string config = "<?xml version=\"1.0\"?>\r\n<configuration>\r\n    <packageSources>\r\n" +
                              "        <add key=\"team\" value=\"https://feed.example.com/v3/index.json\" />\r\n" +
                              "    </packageSources>\r\n</configuration>\r\n";
        byte[] original = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(config)];
        File.WriteAllBytes(ConfigPath, original);

        NugetConfigManager.RegisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertString(content).StartsWith("<?xml version=\"1.0\"?>\r\n<configuration>\r\n    <packageSources>\r\n" +
                                                    "        <add key=\"team\" value=\"https://feed.example.com/v3/index.json\" />\r\n" +
                                                    "        <!-- ePlugin-managed used-by=\"plugin_a\" -->\r\n        <add key=\"ePlugin-");
        Assertions.AssertString(content).EndsWith(" value=\"./addons/a/.nuget\" />\r\n    </packageSources>\r\n</configuration>\r\n");

        NugetConfigManager.UnregisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());

        Assertions.AssertBool(File.ReadAllBytes(ConfigPath).SequenceEqual(original)).IsTrue();
        Assertions.AssertThat(Directory.GetFiles(Root).Select(Path.GetFileName).ToArray()).ContainsExactly("nuget.config");
    }

    [TestCase]
    public static void ANewFileIsIndentedAndAFileOnOneLineStaysOnOne()
    {
        NugetConfigManager.RegisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());
        var content = File.ReadAllText(ConfigPath).ReplaceLineEndings("\n");
        Assertions.AssertString(content).StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n" +
                                                    "    <!-- ePlugin-managed used-by=\"plugin_a\" -->\n    <add key=\"ePlugin-");
        Assertions.AssertString(content).EndsWith("/>\n  </packageSources>\n</configuration>\n");

        const string inline = "<configuration><packageSources><add key=\"team\" value=\"https://feed.example.com/v3/index.json\" /></packageSources></configuration>";
        File.WriteAllText(ConfigPath, inline);
        NugetConfigManager.RegisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());
        Assertions.AssertString(File.ReadAllText(ConfigPath)).NotContains("\n");
        NugetConfigManager.UnregisterSource(Root, "plugin_a", "addons/a/.nuget", new NullLogger());
        Assertions.AssertString(File.ReadAllText(ConfigPath)).IsEqual(inline);
    }

    [TestCase]
    public static void SourcesSpelledDifferentlyAreTheSameEntry()
    {
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "res://addons/a/feed", "addons/a/feed/")).IsTrue();
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "addons/a/feed", Path.Combine(Root, "addons", "a", "feed"))).IsTrue();
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "https://example.com/feed/", "https://example.com/feed")).IsTrue();
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "addons/a/feed", "addons/b/feed")).IsFalse();
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "addons/a/feed", null)).IsFalse();
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, null, null)).IsTrue();
        // outside the project nothing is tracked, so only the same spelling is the same source
        Assertions.AssertBool(NugetConfigManager.SameSource(Root, "user://feed", "user://feed/")).IsFalse();
    }

    [TestCase]
    public static void SlugsWithDoubleHyphensOrCommasAreRemovedAgain()
    {
        foreach (var slug in new[] { "my--tool", "a,b", "100%", "q\"uote", "-lead-" })
            Assertions.AssertString(NugetConfigManager.DecodeSlug(NugetConfigManager.EncodeSlug(slug))).IsEqual(slug);
        // single hyphens stay readable, and comments written before slugs were encoded are read the same
        Assertions.AssertString(NugetConfigManager.EncodeSlug("sample-plugin")).IsEqual("sample-plugin");

        NugetConfigManager.RegisterSource(Root, "my--tool", "addons/shared/feed", new NullLogger());
        NugetConfigManager.RegisterSource(Root, "a,b", "addons/shared/feed", new NullLogger());
        Assertions.AssertThat(File.ReadAllText(ConfigPath)).NotContains("- -");

        NugetConfigManager.UnregisterSource(Root, "my--tool", "addons/shared/feed", new NullLogger());
        Assertions.AssertThat(File.ReadAllText(ConfigPath)).Contains("used-by=\"a%2Cb\"");
        NugetConfigManager.UnregisterSource(Root, "a,b", "addons/shared/feed", new NullLogger());
        Assertions.AssertBool(File.Exists(ConfigPath)).IsFalse();
    }

    [TestCase]
    public static void CommentsTheProjectKeepsSurviveTheLastManagedEntry()
    {
        // a feed the team commented out, and a header outside the root element
        const string config = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Copyright -->\n<configuration>\n  <packageSources>\n    <!-- <add key=\"corp\" value=\"https://corp/feed\" /> -->\n  </packageSources>\n</configuration>\n";
        File.WriteAllText(ConfigPath, config);
        NugetConfigManager.RegisterSource(Root, "plugin", "addons/plugin/feed", new NullLogger());
        NugetConfigManager.UnregisterSource(Root, "plugin", "addons/plugin/feed", new NullLogger());
        Assertions.AssertString(File.ReadAllText(ConfigPath)).IsEqual(config);

        const string header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Copyright -->\n<configuration>\n</configuration>\n";
        File.WriteAllText(ConfigPath, header);
        NugetConfigManager.RegisterSource(Root, "plugin", "addons/plugin/feed", new NullLogger());
        NugetConfigManager.UnregisterSource(Root, "plugin", "addons/plugin/feed", new NullLogger());
        Assertions.AssertBool(File.Exists(ConfigPath)).IsTrue();
        Assertions.AssertString(File.ReadAllText(ConfigPath)).Contains("<!-- Copyright -->");
    }

    [TestCase]
    public static void TheConfigFileKeepsTheSpellingItHasOnDisk()
    {
        Assertions.AssertString(NugetConfigManager.ConfigFileName([], _ => false)).IsEqual("nuget.config");
        Assertions.AssertString(NugetConfigManager.ConfigFileName(["NuGet.Config", "nuget.config"], _ => true)).IsEqual("nuget.config");
        Assertions.AssertString(NugetConfigManager.ConfigFileName(["Game.csproj", "NuGet.Config"], _ => true)).IsEqual("NuGet.Config");
        // a case-insensitive file system finds any spelling; a case-sensitive one ignores it like NuGet does
        Assertions.AssertString(NugetConfigManager.ConfigFileName(["NUGET.CONFIG"], _ => true)).IsEqual("NUGET.CONFIG");
        Assertions.AssertString(NugetConfigManager.ConfigFileName(["NUGET.CONFIG"], _ => false)).IsEqual("nuget.config");
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
