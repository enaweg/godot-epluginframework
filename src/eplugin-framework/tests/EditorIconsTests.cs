using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EditorIconsTests
{
    [TestCase]
    public void ThemeChangesRecolorBothEditorIcons()
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
    public void IconPaletteFollowsFontsEvenWhenBackgroundContrastDiffers()
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
    public void StatusMaskStaysWhiteAndEditorScaleIsPartOfTheCache()
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

    private static void AssertHasColor(Texture2D texture, string html)
    {
        using var image = texture.GetImage();
        var expected = Color.FromHtml(html);
        var found = false;
        for (var y = 0; y < image.GetHeight() && !found; y++)
        for (var x = 0; x < image.GetWidth() && !found; x++)
            found = image.GetPixel(x, y).IsEqualApprox(expected);
        Assertions.AssertBool(found).IsTrue();
    }
}
