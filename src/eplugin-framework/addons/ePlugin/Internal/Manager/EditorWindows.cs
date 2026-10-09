#if TOOLS
using System;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>Makes the framework's dialogs fit the editor: its display scale and the ePlugin icon in their title bars.</summary>
internal static class EditorWindows
{
    // DisplayServer.window_set_icon exists from Godot 4.7 on; the addon is compiled against 4.4 to 4.7.
    private static readonly StringName WindowSetIcon = "window_set_icon";
    private static Image? _icon;

    /// <summary>Scales the sizes a scene authored for 100% editor scale, and shows the ePlugin icon whenever it opens.</summary>
    public static void Prepare(Window window)
    {
        var scale = EditorInterface.Singleton.GetEditorScale();
        window.Size = (Vector2I)((Vector2)window.Size * scale);
        window.MinSize = (Vector2I)((Vector2)window.MinSize * scale);
        window.VisibilityChanged += () => { if (window.Visible) Callable.From(() => ApplyIcon(window)).CallDeferred(); };
    }

    /// <summary>
    /// Shows the ePlugin icon in the title bar of a separate OS window. Godot recreates that window on every popup, so
    /// this runs each time it is shown. An embedded dialog has no title bar icon of its own and must not change the
    /// editor's.
    /// </summary>
    private static void ApplyIcon(Window window)
    {
        var display = DisplayServer.Singleton;
        if (!GodotObject.IsInstanceValid(window) || !window.Visible || window.IsEmbedded() || !display.HasMethod(WindowSetIcon)) return;
        var id = window.GetWindowId();
        if (id == DisplayServer.InvalidWindowId || id == DisplayServer.MainWindowId) return;
        _icon ??= IconImage();
        if (_icon is not null) display.Call(WindowSetIcon, _icon, id);
    }

    /// <summary>The logo rendered large and centered on a square, as title bar icons are square.</summary>
    private static Image? IconImage()
    {
        if (!Godot.FileAccess.FileExists(EditorIcons.LogoPath)) return null;
        var logo = new Image();
        if (logo.LoadSvgFromString(Godot.FileAccess.GetFileAsString(EditorIcons.LogoPath), 4f) != Error.Ok) return null;
        logo.Convert(Image.Format.Rgba8);
        var side = Math.Max(logo.GetWidth(), logo.GetHeight());
        var icon = Image.CreateEmpty(side, side, false, Image.Format.Rgba8);
        icon.BlitRect(logo, new Rect2I(Vector2I.Zero, logo.GetSize()), new Vector2I((side - logo.GetWidth()) / 2, (side - logo.GetHeight()) / 2));
        return icon;
    }
}
#endif
