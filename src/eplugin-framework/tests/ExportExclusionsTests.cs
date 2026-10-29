#if TOOLS
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ExportExclusionsTests
{
    [TestCase]
    public static void TheFrameworkAndTheProjectsPluginStateAreLeftOut()
    {
        foreach (var path in new[]
                 {
                     "res://addons/ePlugin/EPluginPlugin.cs", "res://addons/ePlugin/Internal/Manager/EPluginManagerDialog.tscn",
                     "res://addons/ePlugin/icons/eplugin.svg", "res://addons/eplugin-state.json", "res://addons/eplugin-update-sites.json",
                     // export filters ignore case
                     "res://addons/EPLUGIN/plugin.cfg", "addons/ePlugin/LICENSE"
                 })
            Assertions.AssertBool(ExportExclusions.Excludes(path)).OverrideFailureMessage(path).IsTrue();
    }

    [TestCase]
    public static void TheGamesAndOtherPluginsFilesAreExported()
    {
        foreach (var path in new[]
                 {
                     "res://main.tscn", "res://addons/ePluginExtras/plugin.cfg", "res://addons/my_plugin/eplugin-state.json",
                     "res://addons/eplugin-state.json.user", "res://data/addons/ePlugin/level.tscn", "res://addons/sample_plugin/SamplePlugin.cs"
                 })
            Assertions.AssertBool(ExportExclusions.Excludes(path)).OverrideFailureMessage(path).IsFalse();
    }

    [TestCase]
    public static void TheFilterToEnterByHandNamesThePatterns()
    {
        Assertions.AssertString(ExportExclusions.Filter).IsEqual("addons/ePlugin/*, addons/eplugin-state.json, addons/eplugin-update-sites.json");
    }
}
#endif
