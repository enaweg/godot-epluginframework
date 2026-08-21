using System;
using System.IO;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class NugetConfigManagerTests
{
    private static string ConfigPath => Path.Combine(ProjectSettings.GlobalizePath("res://"), "nuget.config");

    [AfterTest]
    public void Cleanup()
    {
        if (File.Exists(ConfigPath))
        {
            File.Delete(ConfigPath);
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RegisterSourceCreatesFileWithManagedEntry()
    {
        NugetConfigManager.RegisterSource("sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsTrue();

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("ePlugin-managed used-by=\"sample_plugin\"");
        Assertions.AssertThat(content).Contains("./addons/sample_plugin/.nuget");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RegisterSourceSameSourceFromTwoPluginsSharesOneEntry()
    {
        NugetConfigManager.RegisterSource("plugin_a", "res://addons/shared/.nuget", new NullLogger());
        NugetConfigManager.RegisterSource("plugin_b", "res://addons/shared/.nuget", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertInt(CountOccurrences(content, "<add key=\"ePlugin-")).IsEqual(1);
        Assertions.AssertThat(content).Contains("used-by=\"plugin_a,plugin_b\"");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void UnregisterSourceOneOfTwoUsersKeepsEntry()
    {
        NugetConfigManager.RegisterSource("plugin_a", "res://addons/shared/.nuget", new NullLogger());
        NugetConfigManager.RegisterSource("plugin_b", "res://addons/shared/.nuget", new NullLogger());

        NugetConfigManager.UnregisterSource("plugin_a", "res://addons/shared/.nuget", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("used-by=\"plugin_b\"");
        Assertions.AssertThat(content).NotContains("plugin_a");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void UnregisterSourceLastUserRemovesFileWhenOtherwiseEmpty()
    {
        NugetConfigManager.RegisterSource("sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        NugetConfigManager.UnregisterSource("sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void UnregisterSourceLastUserPreservesFileWithOtherContent()
    {
        File.WriteAllText(ConfigPath,
            "<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");

        NugetConfigManager.RegisterSource("sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());
        NugetConfigManager.UnregisterSource("sample_plugin", "res://addons/sample_plugin/.nuget", new NullLogger());

        Assertions.AssertBool(File.Exists(ConfigPath)).IsTrue();
        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains("nuget.org");
        Assertions.AssertThat(content).NotContains("ePlugin-managed");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RegisterSourceExistingIdenticalUserSourceIsNotDuplicatedOrOwned()
    {
        File.WriteAllText(ConfigPath,
            "<configuration><packageSources><add key=\"my-feed\" value=\"https://example.com/feed/index.json\" /></packageSources></configuration>");

        NugetConfigManager.RegisterSource("sample_plugin", "https://example.com/feed/index.json", new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertInt(CountOccurrences(content, "<add ")).IsEqual(1);
        Assertions.AssertThat(content).NotContains("ePlugin-managed");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RegisterSourceUrlPersistsVerbatim()
    {
        const string url = "https://example.com/feed/index.json";

        NugetConfigManager.RegisterSource("sample_plugin", url, new NullLogger());

        var content = File.ReadAllText(ConfigPath);
        Assertions.AssertThat(content).Contains(url);
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
