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
                         "%AboutLogo", "%AboutVersion", "%AboutDescription", "%AuthorLink", "%StoreLink", "%SourceLink", "%DocumentationLink", "%StarNote", "%StarLink"
                     })
                Assertions.AssertObject(dialog.GetNodeOrNull(name)).IsNotNull();
            // The tabs find these nodes from their own scripts, which resolve % in the dialog's scene.
            Assertions.AssertObject(tabs.GetChild(1).GetNodeOrNull("%SiteEditor")).IsNotNull();

            var manager = GD.Load<PackedScene>(EPluginManagerDialog.ScenePath).Instantiate<EPluginManagerDialog>();
            try
            {
                Assertions.AssertObject(manager.GetNodeOrNull("%SettingsButton")).IsNotNull();
                Assertions.AssertObject(manager.GetNodeOrNull("%SettingsDialog")).IsInstanceOf<SettingsDialog>();
                Assertions.AssertObject(manager.GetNodeOrNull("%AttentionDialog")).IsInstanceOf<AttentionDialog>();
                Assertions.AssertObject(manager.GetNodeOrNull("%AttentionButton")).IsNotNull();
                foreach (var name in new[] { "%AttentionList", "%AttentionName", "%AttentionText", "%RetryPluginButton", "%RollbackButton",
                             "%DisablePluginButton", "%OpenLogButton", "%OpenPluginFolderButton", "%RollbackConfirm" })
                    Assertions.AssertObject(manager.GetNode("%AttentionDialog").GetNodeOrNull(name)).IsNotNull();
                // decisions moved to their own dialogs
                foreach (var name in new[] { "%RetryButton", "%TrustCheck", "%RollbackKeptConfirm" })
                    Assertions.AssertObject(manager.GetNodeOrNull(name)).IsNull();
                foreach (var name in new[] { "%LocalSourcesButton", "%UpdateSitesButton", "%AboutButton" })
                    Assertions.AssertObject(manager.GetNodeOrNull(name)).IsNull();
            }
            finally { manager.Free(); }

            var review = GD.Load<PackedScene>(UpdateReviewDialog.ScenePath).Instantiate<UpdateReviewDialog>();
            try
            {
                foreach (var name in new[] { "%Summary", "%ReviewTree", "%ReviewName", "%ReviewText" })
                    Assertions.AssertObject(review.GetNodeOrNull(name)).IsNotNull();
            }
            finally { review.Free(); }
        }
        finally { dialog.Free(); }
    }

    [TestCase]
    public static void TheGeneralTabShowsTheCurrentProjectSettingsWhenOpened()
    {
        UpdateSettings.Register(); LicenseSettings.Register(); WelcomeSettings.Register(); LoggingSettings.Register();
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<SettingsDialog>();
        try
        {
            var general = dialog.GetNode<GeneralSettingsTab>("%SettingsTabs/General");
            general.Initialize();
            foreach (var control in GeneralSettingsTab.SettingControls(general))
            {
                switch (control)
                {
                    case CheckBox check: check.SetPressedNoSignal(!check.ButtonPressed); break;
                    case SpinBox spin: spin.SetValueNoSignal(spin.MinValue == spin.Value ? spin.MaxValue : spin.MinValue); break;
                    case OptionButton option: option.Select(-1); break;
                }
            }

            general.Open();

            foreach (var control in GeneralSettingsTab.SettingControls(general))
            {
                var value = ProjectSettings.GetSetting(GeneralSettingsTab.SettingOf(control));
                switch (control)
                {
                    case CheckBox check: Assertions.AssertBool(check.ButtonPressed).IsEqual(value.AsBool()); break;
                    case SpinBox spin: Assertions.AssertFloat(spin.Value).IsEqual(value.AsDouble()); break;
                    case OptionButton option: Assertions.AssertString(GeneralSettingsTab.Values(option)[option.Selected]).IsEqual(value.AsString()); break;
                }
            }
            Assertions.AssertBool(dialog.GetNode<Label>("%GeneralStatus").Visible).IsFalse();
        }
        finally { dialog.Free(); }
    }

    [TestCase]
    public static void TheAboutTabLinksOnlyWebAddresses()
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<SettingsDialog>();
        try
        {
            var about = dialog.GetNode<AboutTab>("%SettingsTabs/About");
            about.Initialize();
            about.Open(new PluginInfo("ePlugin", "ePlugin Framework", Enaweg.Plugin.Internal.Kinds.PluginKind.Framework, true)
            {
                Version = "1.2.3", Author = AboutInfo.Publisher, Description = " Plugins for plugins. ", SourceUrl = "https://example.org/fork",
                DocumentationUrl = "file:///docs"
            });

            Assertions.AssertString(dialog.GetNode<Label>("%AboutVersion").Text).IsEqual("Version 1.2.3 · by");
            Assertions.AssertString(dialog.GetNode<LinkButton>("%AuthorLink").Uri).IsEqual(AboutInfo.PublisherPage);
            Assertions.AssertString(dialog.GetNode<Label>("%AboutDescription").Text).IsEqual("Plugins for plugins.");
            Assertions.AssertString(dialog.GetNode<LinkButton>("%SourceLink").Uri).IsEqual("https://example.org/fork");
            Assertions.AssertBool(dialog.GetNode<LinkButton>("%DocumentationLink").Visible).IsFalse();
            Assertions.AssertBool(dialog.GetNode<PanelContainer>("%StarNote").Visible).IsTrue();
            Assertions.AssertObject(dialog.GetNode<TextureRect>("%AboutLogo").Texture).IsNotNull();

            // without the manager's entry, the defaults are shown
            about.Open(null);
            Assertions.AssertString(dialog.GetNode<Label>("%AboutVersion").Text).IsEqual("Version unknown");
            Assertions.AssertBool(dialog.GetNode<LinkButton>("%AuthorLink").Visible).IsFalse();
            Assertions.AssertBool(dialog.GetNode<Label>("%AboutDescription").Visible).IsFalse();
            Assertions.AssertString(dialog.GetNode<LinkButton>("%DocumentationLink").Uri).IsEqual(AboutInfo.DefaultDocumentation);
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
