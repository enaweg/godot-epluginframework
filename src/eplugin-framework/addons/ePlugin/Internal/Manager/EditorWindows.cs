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
    public static void Prepare(Window window, EditorSignalConnections signals, GodotObject owner, StringName visibilityChanged)
    {
        var scale = EditorInterface.Singleton.GetEditorScale();
        window.Size = (Vector2I)((Vector2)window.Size * scale);
        window.MinSize = (Vector2I)((Vector2)window.MinSize * scale);
        ScaleMinimumSizes(window, scale);
        if (DisplayServer.Singleton.HasMethod(WindowSetIcon))
            // A closure capturing Window cannot be serialized by Godot on reload. Use a native method callable.
            signals.Connect(window, Window.SignalName.VisibilityChanged, new Callable(owner, visibilityChanged));
    }

    /// <summary>
    /// The minimum sizes of the window's controls, e.g. a list's width or an autowrapping label's. A window inside the
    /// scene is prepared on its own, so it is left out here.
    /// </summary>
    private static void ScaleMinimumSizes(Node parent, float scale)
    {
        if (Math.Abs(scale - 1f) < 0.01f) return;
        foreach (var child in parent.GetChildren())
        {
            if (child is Window) continue;
            if (child is Control control) control.CustomMinimumSize *= scale;
            ScaleMinimumSizes(child, scale);
        }
    }

    /// <summary>
    /// Shows the ePlugin icon in the title bar of a separate OS window. Godot recreates that window on every popup, so
    /// this runs each time it is shown. An embedded dialog has no title bar icon of its own and must not change the
    /// editor's.
    /// </summary>
    public static void ApplyIcon(Window window)
    {
        var display = DisplayServer.Singleton;
        if (!GodotObject.IsInstanceValid(window) || !window.Visible || window.IsEmbedded() || !display.HasMethod(WindowSetIcon)) return;
        var id = window.GetWindowId();
        if (id == DisplayServer.InvalidWindowId || id == DisplayServer.MainWindowId) return;
        _icon ??= IconImage();
        if (_icon is not null) display.Call(WindowSetIcon, _icon, id);
    }

    // Long enough for the editor to process the frames and window messages that still follow the hide.
    private const double ReleaseDelaySeconds = 0.25;

    /// <summary>
    /// Hides a window now and frees it a moment later. Freeing a window in the frame that hides it, from its own button
    /// or right after an assembly reload while it had focus and the mouse, left Godot handling the next window message
    /// with references to the freed window, and the editor crashed.
    /// </summary>
    public static void Release(Window window)
    {
        if (!GodotObject.IsInstanceValid(window) || window.IsQueuedForDeletion()) return;
        window.Hide();
        if (!window.IsInsideTree())
        {
            // Never shown, so nothing refers to it yet.
            window.QueueFree();
            return;
        }

        // Godot reparents its progress UI into the active modal window and leaves it there after hiding it.
        // EditorNode still owns that UI through a raw pointer: freeing it with this window crashes the next scan.
        // Hide first so this window cannot become the host of another progress task while it waits to be freed.
        PreserveProgressDialog(window, window.GetTree().Root);

        // A native method callable rather than a C# delegate, so it still runs after an assembly reload.
        window.GetTree().CreateTimer(ReleaseDelaySeconds)
            .Connect(SceneTreeTimer.SignalName.Timeout, new Callable(window, Node.MethodName.QueueFree));
    }

    private static void PreserveProgressDialog(Node parent, Node destination)
    {
        foreach (var child in parent.GetChildren(true))
        {
            // ProgressDialog is an internal engine class, so its C# wrapper has only its public base type.
            if (child.GetClass() == "ProgressDialog") child.Reparent(destination);
            else PreserveProgressDialog(child, destination);
        }
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
