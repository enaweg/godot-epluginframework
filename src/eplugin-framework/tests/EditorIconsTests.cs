#if TOOLS
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EditorIconsTests
{
    [TestCase]
    public static void ThemeChangesRecolorBothEditorIcons()
    {
        using var theme = new Theme();
        foreach (var path in new[] { EditorIcons.EPluginPath, EditorIcons.UpdatePath })
        {
            theme.SetColor("font_color", "Editor", Colors.White);
            var light = EditorIcons.Get(path, theme, 1f)!;
            AssertHasColor(light, "#e0e0e0");
            theme.SetColor("font_color", "Editor", Colors.Black);
            var dark = EditorIcons.Get(path, theme, 1f)!;
            AssertHasColor(dark, "#5a5a5a");
            Assertions.AssertBool(ReferenceEquals(light, dark)).IsFalse();
            theme.SetColor("font_color", "Editor", Colors.White);
            Assertions.AssertBool(ReferenceEquals(light, EditorIcons.Get(path, theme, 1f))).IsTrue();
        }
    }

    [TestCase]
    public static void IconPaletteFollowsFontsEvenWhenBackgroundContrastDiffers()
    {
        using var theme = new Theme();
        theme.SetColor("mono_color", "Editor", Colors.White);
        theme.SetColor("font_color", "Editor", Colors.Black);
        AssertHasColor(EditorIcons.Get(EditorIcons.EPluginPath, theme, 1f)!, "#5a5a5a");
        theme.SetColor("mono_color", "Editor", Colors.Black);
        theme.SetColor("font_color", "Editor", Colors.White);
        AssertHasColor(EditorIcons.Get(EditorIcons.EPluginPath, theme, 1f)!, "#e0e0e0");
    }

    [TestCase]
    public static void StatusMaskStaysWhiteAndEditorScaleIsPartOfTheCache()
    {
        using var theme = new Theme();
        theme.SetColor("font_color", "Editor", Colors.Black);
        var button = EditorIcons.Get(EditorIcons.UpdatePath, theme, 1f)!;
        var indicator = EditorIcons.Get(EditorIcons.UpdatePath, theme, 1f, tintable: true)!;
        AssertHasColor(button, "#5a5a5a");
        AssertHasColor(indicator, "#ffffff");
        Assertions.AssertBool(ReferenceEquals(button, indicator)).IsFalse();
        var scaled = EditorIcons.Get(EditorIcons.EPluginPath, theme, 2f)!;
        Assertions.AssertInt(scaled.GetWidth()).IsEqual(32);
        Assertions.AssertInt(EditorIcons.Get(EditorIcons.EPluginPath, theme, 1f)!.GetWidth()).IsEqual(16);
    }

    [TestCase]
    public static void ThePuzzlePieceTakesTheStatusColor()
    {
        using var theme = new Theme();
        theme.SetColor("font_color", "Editor", Colors.Black);
        var plain = EditorIcons.Get(EditorIcons.EPluginPath, theme, 4f)!;
        var green = EditorIcons.Get(EditorIcons.EPluginPath, theme, 4f, piece: Color.FromHtml("#00ff00"))!;
        var red = EditorIcons.Get(EditorIcons.EPluginPath, theme, 4f, piece: Color.FromHtml("#ff0000"))!;
        AssertHasColor(green, "#00ff00");
        AssertHasColor(red, "#ff0000");
        Assertions.AssertBool(HasColor(plain, "#00ff00") || HasColor(red, "#00ff00")).IsFalse();
        Assertions.AssertBool(ReferenceEquals(plain, green) || ReferenceEquals(green, red)).IsFalse();
        Assertions.AssertBool(ReferenceEquals(green, EditorIcons.Get(EditorIcons.EPluginPath, theme, 4f, piece: Color.FromHtml("#00ff00")))).IsTrue();
    }

    [TestCase]
    public static void ActionIconsFollowTheIconPaletteAndTheThemeStatusColors()
    {
        using var theme = new Theme();
        theme.SetColor("font_color", "Editor", Colors.Black);
        foreach (var path in new[] { EditorIcons.RescanPath, EditorIcons.SettingsPath, EditorIcons.RetryPath,
                     EditorIcons.UpgradePath, EditorIcons.ReinstallPath, EditorIcons.DowngradePath })
            AssertHasColor(EditorIcons.Get(path, theme, 4f)!, "#5a5a5a");

        theme.SetColor("success_color", "Editor", Color.FromHtml("#00ff00"));
        theme.SetColor("warning_color", "Editor", Color.FromHtml("#ffff00"));
        theme.SetColor("error_color", "Editor", Color.FromHtml("#ff0000"));
        var green = EditorIcons.Get(EditorIcons.UpgradePath, theme, 4f)!;
        AssertHasColor(green, "#00ff00");
        AssertHasColor(EditorIcons.Get(EditorIcons.DowngradePath, theme, 4f)!, "#ffff00");
        AssertHasColor(EditorIcons.Get(EditorIcons.RetryPath, theme, 4f)!, "#ff0000");
        theme.SetColor("success_color", "Editor", Color.FromHtml("#0000ff"));
        var blue = EditorIcons.Get(EditorIcons.UpgradePath, theme, 4f)!;
        AssertHasColor(blue, "#0000ff");
        Assertions.AssertBool(ReferenceEquals(green, blue)).IsFalse();
    }

    private static void AssertHasColor(Texture2D texture, string html) => Assertions.AssertBool(HasColor(texture, html)).IsTrue();

    private static bool HasColor(Texture2D texture, string html)
    {
        using var image = texture.GetImage();
        var expected = Color.FromHtml(html);
        var found = false;
        for (var y = 0; y < image.GetHeight() && !found; y++)
        for (var x = 0; x < image.GetWidth() && !found; x++)
            found = image.GetPixel(x, y).IsEqualApprox(expected);
        return found;
    }
}
#endif
