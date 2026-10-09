#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The framework's icons, rendered from their SVG at the editor's display scale. The imported textures cannot be
/// relied on for that: a headless editor, e.g. a test run building the solution, imports their editor variant at 100%.
/// </summary>
internal static class EditorIcons
{
    /// <summary>The colored ePlugin logo, for title bars and the details header.</summary>
    public const string LogoPath = "res://addons/ePlugin/icons/eplugin.svg";
    /// <summary>The ePlugin icon in the style of the editor's own icons, for the toolbar and lists.</summary>
    public const string EPluginPath = "res://addons/ePlugin/icons/eplugin_editor.svg";
    /// <summary>The update icon, for buttons using the editor's normal icon palette.</summary>
    public const string UpdatePath = "res://addons/ePlugin/icons/update.svg";

    // The common icon color of the editor's icons and what a light editor theme turns it into.
    private const string IconColor = "#e0e0e0", LightThemeIconColor = "#5a5a5a";
    private static readonly Dictionary<(string Path, bool LightIcons, float Scale, bool Tintable), Texture2D?> Cache = [];

    public static Texture2D? Logo => Get(LogoPath);
    public static Texture2D? EPlugin => Get(EPluginPath);
    public static Texture2D? Update => Get(UpdatePath);
    /// <summary>A white update mask, so status colors can tint it without multiplying by the icon palette.</summary>
    public static Texture2D? UpdateIndicator => Get(UpdatePath, tintable: true);

    private static Texture2D? Get(string path, bool tintable = false) =>
        Get(path, EditorInterface.Singleton.GetEditorTheme(), EditorInterface.Singleton.GetEditorScale(), tintable);

    internal static Texture2D? Get(string path, Theme? theme, float scale, bool tintable = false)
    {
        // In newer Godot versions the icon/font palette can be selected independently of the background.
        // mono_color describes the background contrast; font_color follows the actual icon/font preference.
        var lightIcons = theme is null || (!theme.HasColor("font_color", "Editor") && !theme.HasColor("mono_color", "Editor")) ||
            theme.GetColor(theme.HasColor("font_color", "Editor") ? "font_color" : "mono_color", "Editor").Luminance > 0.5f;
        var key = (path, lightIcons, scale, tintable);
        if (Cache.TryGetValue(key, out var texture) && (texture is null || GodotObject.IsInstanceValid(texture))) return texture;
        return Cache[key] = Render(path, lightIcons, scale, tintable);
    }

    private static Texture2D? Render(string path, bool lightIcons, float scale, bool tintable)
    {
        if (!FileAccess.FileExists(path)) return null;
        var svg = FileAccess.GetFileAsString(path);
        if (!tintable)
        {
            // Keep the logo's brand colors and the SVG's black/white mask values intact.
            if (path == UpdatePath) svg = svg.Replace("#fff", IconColor);
            if (!lightIcons && path != LogoPath) svg = svg.Replace(IconColor, LightThemeIconColor);
        }
        using var image = new Image();
        if (image.LoadSvgFromString(svg, scale) != Error.Ok) return null;
        return ImageTexture.CreateFromImage(image);
    }
}
#endif
