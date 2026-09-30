using System.Runtime.InteropServices;

namespace ActivationProgress;

/// <summary>GTK 3 window; works on X11 and Wayland. Fails (and the host skips the window) without libgtk-3.</summary>
internal sealed class GtkProgressWindow : IProgressWindow
{
    private const string Gtk = "libgtk-3.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    // Kept in fields so the delegates outlive the native callbacks that reference them.
    private readonly SourceFunc _pulse = Pulse;
    private readonly SourceFunc _updateText;
    private readonly SourceFunc _ready;
    private readonly SourceFunc _show;
    private readonly SignalCallback _destroy = (_, _) => gtk_main_quit();
    // Only the host or the lifetime timer end the helper.
    private readonly EventCallback _refuseDelete = (_, _, _) => true;
    private readonly EditorCenter? _editorCenter;
    private Action? _onReady;
    private IntPtr _window;
    private IntPtr _label;
    private volatile string _text = string.Empty;

    /// <param name="editorCenter">The editor's center in X11 root pixels, or null for the screen center.</param>
    public GtkProgressWindow(EditorCenter? editorCenter)
    {
        _editorCenter = editorCenter;
        _updateText = _ =>
        {
            gtk_label_set_text(_label, _text);
            return false;
        };
        _ready = _ =>
        {
            _onReady?.Invoke();
            return false;
        };
        _show = _ =>
        {
            gtk_widget_show_all(_window);
            return false;
        };
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
    {
        if (!gtk_init_check(IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("GTK could not open a display.");
        }

        var window = _window = gtk_window_new(0); // GTK_WINDOW_TOPLEVEL
        gtk_window_set_title(window, title);
        gtk_window_set_default_size(window, 340, 100);
        gtk_window_set_resizable(window, false);
        gtk_window_set_keep_above(window, true);
        gtk_window_set_skip_taskbar_hint(window, true);
        // The editor keeps focus while the helper is up and after it exits.
        gtk_window_set_focus_on_map(window, false);
        gtk_window_set_accept_focus(window, false);
        gtk_window_set_deletable(window, false);
        gtk_container_set_border_width(window, 20);

        var box = gtk_box_new(1, 14); // GTK_ORIENTATION_VERTICAL
        _label = gtk_label_new(text);
        gtk_label_set_xalign(_label, 0f);
        gtk_label_set_ellipsize(_label, 3); // PANGO_ELLIPSIZE_END
        var progress = gtk_progress_bar_new();
        gtk_box_pack_start(box, _label, false, false, 0);
        gtk_box_pack_start(box, progress, false, false, 0);
        gtk_container_add(window, box);

        Place(window);

        g_signal_connect_data(window, "destroy", Marshal.GetFunctionPointerForDelegate(_destroy),
            IntPtr.Zero, IntPtr.Zero, 0);
        g_signal_connect_data(window, "delete-event", Marshal.GetFunctionPointerForDelegate(_refuseDelete),
            IntPtr.Zero, IntPtr.Zero, 0);
        g_timeout_add(100, Marshal.GetFunctionPointerForDelegate(_pulse), progress);
        g_timeout_add((uint)showDelayMilliseconds, Marshal.GetFunctionPointerForDelegate(_show), IntPtr.Zero);

        _onReady = ready;
        g_idle_add(Marshal.GetFunctionPointerForDelegate(_ready), IntPtr.Zero);
        gtk_main();
    }

    public void SetText(string text)
    {
        _text = text;
        // g_idle_add is thread-safe and runs the callback on the GTK main loop.
        g_idle_add(Marshal.GetFunctionPointerForDelegate(_updateText), IntPtr.Zero);
    }

    /// <summary>
    /// Centers on the editor. Wayland compositors ignore client positions and place the window themselves.
    /// </summary>
    private void Place(IntPtr window)
    {
        if (_editorCenter is not { } center)
        {
            gtk_window_set_position(window, 1); // GTK_WIN_POS_CENTER
            return;
        }

        // Godot reports device pixels; GTK positions windows in pixels divided by the GDK scale.
        var scale = Math.Max(1, gtk_widget_get_scale_factor(window));
        gtk_window_get_size(window, out var width, out var height);
        gtk_window_move(window, (int)(center.X / scale) - width / 2, (int)(center.Y / scale) - height / 2);
    }

    private static bool Pulse(IntPtr progress)
    {
        gtk_progress_bar_pulse(progress);
        return true;
    }

    private delegate bool SourceFunc(IntPtr data);

    private delegate void SignalCallback(IntPtr instance, IntPtr data);

    private delegate bool EventCallback(IntPtr instance, IntPtr gdkEvent, IntPtr data);

    [DllImport(Gtk)]
    private static extern bool gtk_init_check(IntPtr argc, IntPtr argv);

    [DllImport(Gtk)]
    private static extern void gtk_main();

    [DllImport(Gtk)]
    private static extern void gtk_main_quit();

    [DllImport(Gtk)]
    private static extern IntPtr gtk_window_new(int type);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_title(IntPtr window,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_default_size(IntPtr window, int width, int height);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_resizable(IntPtr window, bool resizable);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_keep_above(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_skip_taskbar_hint(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_focus_on_map(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_accept_focus(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_position(IntPtr window, int position);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_deletable(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_get_size(IntPtr window, out int width, out int height);

    [DllImport(Gtk)]
    private static extern void gtk_window_move(IntPtr window, int x, int y);

    [DllImport(Gtk)]
    private static extern int gtk_widget_get_scale_factor(IntPtr widget);

    [DllImport(Gtk)]
    private static extern void gtk_container_set_border_width(IntPtr container, uint width);

    [DllImport(Gtk)]
    private static extern void gtk_container_add(IntPtr container, IntPtr widget);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_box_new(int orientation, int spacing);

    [DllImport(Gtk)]
    private static extern void gtk_box_pack_start(IntPtr box, IntPtr child, bool expand, bool fill, uint padding);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_label_new([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(Gtk)]
    private static extern void gtk_label_set_text(IntPtr label, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(Gtk)]
    private static extern void gtk_label_set_xalign(IntPtr label, float xalign);

    [DllImport(Gtk)]
    private static extern void gtk_label_set_ellipsize(IntPtr label, int mode);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_progress_bar_new();

    [DllImport(Gtk)]
    private static extern void gtk_progress_bar_pulse(IntPtr progress);

    [DllImport(Gtk)]
    private static extern void gtk_widget_show_all(IntPtr widget);

    [DllImport(GLib)]
    private static extern uint g_idle_add(IntPtr function, IntPtr data);

    [DllImport(GLib)]
    private static extern uint g_timeout_add(uint interval, IntPtr function, IntPtr data);

    [DllImport(GObject)]
    private static extern ulong g_signal_connect_data(IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, IntPtr handler, IntPtr data, IntPtr destroyData,
        int flags);
}
