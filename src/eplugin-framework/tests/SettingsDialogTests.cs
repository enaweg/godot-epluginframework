#if TOOLS
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Welcomes;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class SettingsDialogTests
{
    private const string ScenePath = "res://addons/ePlugin/Internal/Manager/SettingsDialog.tscn";
    private static readonly string[] TabTitles = ["General", "Update sources", "About"];

    [TestCase]
    public static void TheSettingsAreTabsWithTheNodesTheirScriptsBind()
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<SettingsDialog>();
        try
        {
            // TabContainer shows each tab's node name as its title.
            var tabs = dialog.GetNode<TabContainer>("%SettingsTabs");
            Assertions.AssertArray(tabs.GetChildren().Select(t => t.Name.ToString()).ToArray())
                .IsEqual(TabTitles);
            Assertions.AssertObject(tabs.GetChild(0)).IsInstanceOf<GeneralSettingsTab>();
            Assertions.AssertObject(tabs.GetChild(1)).IsInstanceOf<UpdateSourcesTab>();
            Assertions.AssertObject(tabs.GetChild(2)).IsInstanceOf<AboutTab>();
            foreach (var name in new[]
                     {
                         "%AutoAcceptCheck", "%AutoAcceptWarning", "%AutoAcceptConfirm", "%GeneralStatus",
                         "%SourceTree", "%SourceStatus", "%RescanLocalSourcesButton", "%AddSiteButton", "%AddLocalSourceButton", "%EditSiteButton", "%RemoveSourceButton",
                         "%SiteEditor", "%SiteSlug", "%SiteUrl", "%SiteHint", "%LocalSourceFolderDialog",
                         "%AboutLogo", "%AboutVersion", "%AboutDescription", "%StoreLink", "%SourceLink", "%DocumentationLink", "%StarNote", "%StarLink"
                     })
                Assertions.AssertObject(dialog.GetNodeOrNull(name)).IsNotNull();
            // The tabs find these nodes from their own scripts, which resolve % in the dialog's scene.
            Assertions.AssertObject(tabs.GetChild(1).GetNodeOrNull("%SiteEditor")).IsNotNull();

            var manager = GD.Load<PackedScene>(EPluginManagerDialog.ScenePath).Instantiate<EPluginManagerDialog>();
            try
            {
                Assertions.AssertObject(manager.GetNodeOrNull("%SettingsButton")).IsNotNull();
                Assertions.AssertObject(manager.GetNodeOrNull("%SettingsDialog")).IsInstanceOf<SettingsDialog>();
                foreach (var name in new[] { "%LocalSourcesButton", "%UpdateSitesButton", "%AboutButton" })
                    Assertions.AssertObject(manager.GetNodeOrNull(name)).IsNull();
            }
            finally { manager.Free(); }
        }
        finally { dialog.Free(); }
    }

    [TestCase]
    public static void TheGeneralTabBindsEveryProjectSettingOfThePluginWithItsTypeAndValues()
    {
        UpdateSettings.Register(); LicenseSettings.Register(); WelcomeSettings.Register(); LoggingSettings.Register();
        var registered = ProjectSettings.Singleton.GetPropertyList()
            .Where(p => p["name"].AsString().StartsWith("eplugin/", System.StringComparison.Ordinal))
            .ToDictionary(p => p["name"].AsString());
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<SettingsDialog>();
        try
        {
            var controls = GeneralSettingsTab.SettingControls(dialog.GetNode("%SettingsTabs/General")).ToArray();
            Assertions.AssertArray(controls.Select(GeneralSettingsTab.SettingOf).OrderBy(k => k).ToArray())
                .IsEqual(registered.Keys.OrderBy(k => k).ToArray());
            foreach (var control in controls)
            {
                var property = registered[GeneralSettingsTab.SettingOf(control)];
                var type = (Variant.Type)property["type"].AsInt32();
                switch (control)
                {
                    case CheckBox:
                        Assertions.AssertInt((int)type).IsEqual((int)Variant.Type.Bool);
                        break;
                    case SpinBox spin:
                        Assertions.AssertInt((int)type).IsEqual((int)Variant.Type.Int);
                        Assertions.AssertString(property["hint_string"].AsString()).IsEqual($"{spin.MinValue},{spin.MaxValue},{spin.Step}");
                        break;
                    case OptionButton option:
                        Assertions.AssertInt((int)type).IsEqual((int)Variant.Type.String);
                        Assertions.AssertInt(option.ItemCount).IsEqual(GeneralSettingsTab.Values(option).Length);
                        Assertions.AssertString(property["hint_string"].AsString()).IsEqual(string.Join(",", GeneralSettingsTab.Values(option)));
                        break;
                    default:
                        Assertions.AssertObject(control).IsNull();
                        break;
                }
            }
        }
        finally { dialog.Free(); }
    }
}
#endif
