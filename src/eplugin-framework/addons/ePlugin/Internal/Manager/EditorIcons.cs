#if TOOLS
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>The update icon, the mask of the update indicator in the plugin list.</summary>
    public const string UpdatePath = "res://addons/ePlugin/icons/update.svg";
    // Action icons, drawn after docs/internals/editor-icons.md.
    public const string RescanPath = "res://addons/ePlugin/icons/rescan.svg";
    public const string SettingsPath = "res://addons/ePlugin/icons/settings.svg";
    public const string RetryPath = "res://addons/ePlugin/icons/retry.svg";
    public const string UpgradePath = "res://addons/ePlugin/icons/upgrade.svg";
    public const string ReinstallPath = "res://addons/ePlugin/icons/reinstall.svg";
    public const string DowngradePath = "res://addons/ePlugin/icons/downgrade.svg";

    // The common icon color of the editor's icons and what a light editor theme turns it into.
    private const string IconColor = "#e0e0e0", LightThemeIconColor = "#5a5a5a";
    // The puzzle piece of the ePlugin icon, which the toolbar button colors by the state of the plugins.
    private const string PieceId = "id=\"piece\"";
    // The accent colors of Godot's own icons, which the SVGs use and which are rendered in the theme's status colors, so
    // they stay readable on light themes.
    private static readonly (string Svg, string Name, Color Fallback)[] Accents =
    [
        ("#5fff97", "success_color", new Color(0.45f, 0.95f, 0.5f)),
        ("#ffdd65", "warning_color", new Color(1f, 0.87f, 0.4f)),
        ("#ff5f5f", "error_color", new Color(1f, 0.47f, 0.42f))
    ];
    private static readonly Dictionary<(string Path, bool LightIcons, float Scale, bool Tintable, string? Piece, string Accents), Texture2D?> Cache = [];

    public static Texture2D? Logo => Get(LogoPath);
    /// <summary>The colored ePlugin logo at eight times its icon size, for the About tab of the settings.</summary>
    public static Texture2D? LargeLogo => Get(LogoPath, EditorWindows.Theme, EditorWindows.Scale * 8);
    public static Texture2D? EPlugin => Get(EPluginPath);
    /// <summary>A white update mask, so status colors can tint it without multiplying by the icon palette.</summary>
    public static Texture2D? UpdateIndicator => Get(UpdatePath, tintable: true);
    public static Texture2D? Rescan => Get(RescanPath);
    public static Texture2D? Settings => Get(SettingsPath);
    public static Texture2D? Retry => Get(RetryPath);
    public static Texture2D? Upgrade => Get(UpgradePath);
    public static Texture2D? Reinstall => Get(ReinstallPath);
    public static Texture2D? Downgrade => Get(DowngradePath);

    /// <summary>The editor's color for something that went well, e.g. an available update.</summary>
    public static Color SuccessColor => ThemeColor(EditorWindows.Theme, Accents[0].Name, Accents[0].Fallback);
    /// <summary>The editor's color for warnings.</summary>
    public static Color WarningColor => ThemeColor(EditorWindows.Theme, Accents[1].Name, Accents[1].Fallback);
    /// <summary>The editor's color for errors.</summary>
    public static Color ErrorColor => ThemeColor(EditorWindows.Theme, Accents[2].Name, Accents[2].Fallback);

    /// <summary>
    /// The ePlugin icon of the toolbar button: its puzzle piece is green while updates can be installed and red while an
    /// installed plugin needs attention.
    /// </summary>
    public static Texture2D? Manager(ManagerStatus status)
    {
        var theme = EditorWindows.Theme;
        Color? piece = status switch
        {
            ManagerStatus.UpdatesAvailable => SuccessColor,
            ManagerStatus.NeedsAttention => ErrorColor,
            _ => null
        };
        return Get(EPluginPath, theme, EditorWindows.Scale, piece: piece);
    }

    private static Color ThemeColor(Theme? theme, string name, Color fallback) =>
        theme is not null && theme.HasColor(name, "Editor") ? theme.GetColor(name, "Editor") : fallback;

    private static Texture2D? Get(string path, bool tintable = false) =>
        Get(path, EditorWindows.Theme, EditorWindows.Scale, tintable);

    internal static Texture2D? Get(string path, Theme? theme, float scale, bool tintable = false, Color? piece = null)
    {
        // In newer Godot versions the icon/font palette can be selected independently of the background.
        // mono_color describes the background contrast; font_color follows the actual icon/font preference.
        var lightIcons = theme is null || (!theme.HasColor("font_color", "Editor") && !theme.HasColor("mono_color", "Editor")) ||
            theme.GetColor(theme.HasColor("font_color", "Editor") ? "font_color" : "mono_color", "Editor").Luminance > 0.5f;
        var pieceColor = piece is { } color ? "#" + color.ToHtml(false) : null;
        var accents = Accents.Select(a => "#" + ThemeColor(theme, a.Name, a.Fallback).ToHtml(false)).ToArray();
        var key = (path, lightIcons, scale, tintable, pieceColor, string.Join(' ', accents));
        if (Cache.TryGetValue(key, out var texture) && (texture is null || GodotObject.IsInstanceValid(texture))) return texture;
        return Cache[key] = Render(path, lightIcons, scale, tintable, pieceColor, accents);
    }

    private static ImageTexture? Render(string path, bool lightIcons, float scale, bool tintable, string? piece, string[] accents)
    {
        if (!FileAccess.FileExists(path)) return null;
        var svg = FileAccess.GetFileAsString(path);
        if (!tintable)
        {
            // Keep the logo's brand colors and the SVG's black/white mask values intact.
            if (path == UpdatePath) svg = svg.Replace("#fff", IconColor);
            if (!lightIcons && path != LogoPath) svg = svg.Replace(IconColor, LightThemeIconColor);
            for (var i = 0; i < Accents.Length; i++) svg = svg.Replace(Accents[i].Svg, accents[i]);
        }
        if (piece is not null) svg = svg.Replace(PieceId, $"{PieceId} fill=\"{piece}\"");
        using var image = new Image();
        if (image.LoadSvgFromString(svg, scale) != Error.Ok) return null;
        return ImageTexture.CreateFromImage(image);
    }
}
#endif
