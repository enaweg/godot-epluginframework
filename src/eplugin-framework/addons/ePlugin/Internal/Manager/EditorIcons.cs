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
    /// <summary>A white update icon, tinted by whoever draws it.</summary>
    public const string UpdatePath = "res://addons/ePlugin/icons/update.svg";

    // The common icon color of the editor's icons and what a light editor theme turns it into.
    private const string IconColor = "#e0e0e0", LightThemeIconColor = "#5a5a5a";
    private static readonly Dictionary<(string Path, bool Dark), Texture2D?> Cache = [];

    public static Texture2D? Logo => Get(LogoPath);
    public static Texture2D? EPlugin => Get(EPluginPath);
    public static Texture2D? Update => Get(UpdatePath);

    private static Texture2D? Get(string path)
    {
        var dark = IsDarkTheme();
        if (Cache.TryGetValue((path, dark), out var texture) && (texture is null || GodotObject.IsInstanceValid(texture))) return texture;
        return Cache[(path, dark)] = Render(path, dark);
    }

    private static Texture2D? Render(string path, bool dark)
    {
        if (!FileAccess.FileExists(path)) return null;
        var svg = FileAccess.GetFileAsString(path);
        if (!dark) svg = svg.Replace(IconColor, LightThemeIconColor);
        var image = new Image();
        if (image.LoadSvgFromString(svg, EditorInterface.Singleton.GetEditorScale()) != Error.Ok) return null;
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The editor's mono color is white in a dark theme and black in a light one.</summary>
    private static bool IsDarkTheme() =>
        EditorInterface.Singleton.GetEditorTheme() is not { } theme || !theme.HasColor("mono_color", "Editor") ||
        theme.GetColor("mono_color", "Editor").Luminance > 0.5f;
}
#endif
