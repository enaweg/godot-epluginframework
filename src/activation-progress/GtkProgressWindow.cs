using System.Runtime.InteropServices;

namespace ActivationProgress;

/// <summary>
/// GTK 3 window; works on X11 and Wayland. Fails (and the host skips the window) without libgtk-3.
/// Without a compositor the window cannot be transparent and fills its background with the theme's instead.
/// </summary>
internal sealed class GtkProgressWindow : IProgressWindow
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GdkPixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string Cairo = "libcairo.so.2";

    // Kept in fields so the delegates outlive the native callbacks that reference them.
    private readonly SourceFunc _pulse = Pulse;
    private readonly SourceFunc _ready;
    private readonly SourceFunc _show;
    private readonly SourceFunc _frame;
    private readonly SourceFunc _close;
    private readonly DrawCallback _draw;
    private readonly SignalCallback _destroy = (_, _) => gtk_main_quit();
    // Only the host or the lifetime timer end the helper.
    private readonly EventCallback _refuseDelete = (_, _, _) => true;
    private readonly EditorCenter? _editorCenter;
    private readonly Animation _animation = new();
    private Action? _onReady;
    private IntPtr _window;
    private IntPtr _label;
    private IntPtr _progress;
    private IntPtr _head;
    private IntPtr _puzzle;
    private bool _transparent;
    private volatile string _text = string.Empty;
    private string? _shownText;

    /// <param name="editorCenter">The editor's center in X11 root pixels, or null for the screen center.</param>
    public GtkProgressWindow(EditorCenter? editorCenter)
    {
        _editorCenter = editorCenter;
        _ready = _ =>
        {
            _onReady?.Invoke();
            return false;
        };
        _frame = _ =>
        {
            if (_animation.Closed)
            {
                gtk_main_quit();
                return false;
            }

            Update();
            return true;
        };
        _show = _ =>
        {
            _animation.Open();
            Update();
            gtk_widget_show_all(_window);
            g_timeout_add(Animation.FrameMilliseconds, Marshal.GetFunctionPointerForDelegate(_frame), IntPtr.Zero);
            return false;
        };
        _close = _ =>
        {
            if (_animation.Started)
            {
                _animation.Close();
            }
            else
            {
                gtk_main_quit();
            }

            return false;
        };
        _draw = Draw;
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
    {
        if (!gtk_init_check(IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("GTK could not open a display.");
        }

        _text = text;
        var window = _window = gtk_window_new(0); // GTK_WINDOW_TOPLEVEL
        gtk_window_set_title(window, title);
        var width = (int)Layout.Width;
        var height = (int)Layout.Height;
        gtk_window_set_default_size(window, width, height);
        gtk_widget_set_size_request(window, width, height);
        gtk_window_set_resizable(window, false);
        gtk_window_set_decorated(window, false);
        gtk_window_set_type_hint(window, 4); // GDK_WINDOW_TYPE_HINT_SPLASHSCREEN
        gtk_window_set_keep_above(window, true);
        gtk_window_set_skip_taskbar_hint(window, true);
        // The editor keeps focus while the helper is up and after it exits.
        gtk_window_set_focus_on_map(window, false);
        gtk_window_set_accept_focus(window, false);
        gtk_window_set_deletable(window, false);
        gtk_widget_set_app_paintable(window, true);
        var screen = gtk_widget_get_screen(window);
        var visual = gdk_screen_get_rgba_visual(screen);
        if (visual != IntPtr.Zero && gdk_screen_is_composited(screen))
        {
            gtk_widget_set_visual(window, visual);
            _transparent = true;
        }

        _head = LoadPixbuf(Logo.LoadHead());
        _puzzle = LoadPixbuf(Logo.LoadPuzzle());

        var fixedLayout = gtk_fixed_new();
        _label = gtk_label_new(text);
        gtk_label_set_xalign(_label, 0f);
        gtk_label_set_ellipsize(_label, 3); // PANGO_ELLIPSIZE_END
        Put(fixedLayout, _label, Layout.Label);
        _progress = gtk_progress_bar_new();
        Put(fixedLayout, _progress, Layout.Bar);
        gtk_container_add(window, fixedLayout);

        Place(window);

        g_signal_connect_data(window, "draw", Marshal.GetFunctionPointerForDelegate(_draw), IntPtr.Zero, IntPtr.Zero, 0);
        g_signal_connect_data(window, "destroy", Marshal.GetFunctionPointerForDelegate(_destroy),
            IntPtr.Zero, IntPtr.Zero, 0);
        g_signal_connect_data(window, "delete-event", Marshal.GetFunctionPointerForDelegate(_refuseDelete),
            IntPtr.Zero, IntPtr.Zero, 0);
        g_timeout_add(100, Marshal.GetFunctionPointerForDelegate(_pulse), _progress);
        g_timeout_add((uint)showDelayMilliseconds, Marshal.GetFunctionPointerForDelegate(_show), IntPtr.Zero);

        _onReady = ready;
        g_idle_add(Marshal.GetFunctionPointerForDelegate(_ready), IntPtr.Zero);
        gtk_main();
    }

    public void SetText(string text)
    {
        // Shown with the next frame.
        _text = text;
    }

    public void Close()
    {
        // g_idle_add is thread-safe and runs the callback on the GTK main loop.
        g_idle_add(Marshal.GetFunctionPointerForDelegate(_close), IntPtr.Zero);
    }

    /// <summary>Puts a control at its place, vertically centered at its natural height.</summary>
    private static void Put(IntPtr fixedLayout, IntPtr widget, Box box)
    {
        gtk_widget_set_size_request(widget, (int)box.Width, -1);
        gtk_widget_set_opacity(widget, 0);
        gtk_widget_get_preferred_height(widget, out var height, out _);
        gtk_fixed_put(fixedLayout, widget, (int)box.X, (int)Math.Round(box.Y + (box.Height - height) / 2));
    }

    private void Update()
    {
        var frame = _animation.Frame;
        var text = _text;
        if (!ReferenceEquals(text, _shownText))
        {
            gtk_label_set_text(_label, text);
            _shownText = text;
        }

        var controls = frame.Controls * frame.Opacity;
        gtk_widget_set_opacity(_label, controls);
        gtk_widget_set_opacity(_progress, controls);
        gtk_widget_queue_draw(_window);
    }

    /// <summary>Draws the panel and the logo; GTK draws the controls on top afterwards.</summary>
    private bool Draw(IntPtr widget, IntPtr cr, IntPtr data)
    {
        var frame = _animation.Frame;
        var style = gtk_widget_get_style_context(widget);
        cairo_save(cr);
        cairo_set_operator(cr, 0); // CAIRO_OPERATOR_CLEAR
        cairo_paint(cr);
        cairo_restore(cr);
        if (!_transparent)
        {
            gtk_render_background(style, cr, 0, 0, Layout.Width, Layout.Height);
        }

        cairo_push_group(cr);
        if (Layout.PanelAt(frame.Split) is { } panel)
        {
            cairo_save(cr);
            RoundedRectangle(cr, panel);
            cairo_clip(cr);
            gtk_render_background(style, cr, panel.X, panel.Y, panel.Width, panel.Height);
            cairo_restore(cr);
            // A border in the theme's text color stays visible on light and dark themes alike.
            gtk_style_context_get_color(style, gtk_widget_get_state_flags(widget), out var color);
            RoundedRectangle(cr, panel with { X = panel.X + 0.5, Y = panel.Y + 0.5, Width = panel.Width - 1, Height = panel.Height - 1 });
            cairo_set_source_rgba(cr, color.Red, color.Green, color.Blue, 0.25);
            cairo_set_line_width(cr, 1);
            cairo_stroke(cr);
        }

        DrawPixbuf(cr, _head, Layout.HeadAt(frame.Split));
        DrawPixbuf(cr, _puzzle, Layout.PuzzleAt(frame.Split));
        cairo_pop_group_to_source(cr);
        cairo_paint_with_alpha(cr, frame.Opacity);
        return false;
    }

    private static void RoundedRectangle(IntPtr cr, Box box)
    {
        var radius = Math.Min(Layout.PanelRadius, Math.Min(box.Width, box.Height) / 2);
        cairo_new_sub_path(cr);
        cairo_arc(cr, box.Right - radius, box.Y + radius, radius, -Math.PI / 2, 0);
        cairo_arc(cr, box.Right - radius, box.Bottom - radius, radius, 0, Math.PI / 2);
        cairo_arc(cr, box.X + radius, box.Bottom - radius, radius, Math.PI / 2, Math.PI);
        cairo_arc(cr, box.X + radius, box.Y + radius, radius, Math.PI, 3 * Math.PI / 2);
        cairo_close_path(cr);
    }

    private static void DrawPixbuf(IntPtr cr, IntPtr pixbuf, Box box)
    {
        cairo_save(cr);
        cairo_translate(cr, box.X, box.Y);
        cairo_scale(cr, box.Width / gdk_pixbuf_get_width(pixbuf), box.Height / gdk_pixbuf_get_height(pixbuf));
        gdk_cairo_set_source_pixbuf(cr, pixbuf, 0, 0);
        cairo_pattern_set_filter(cairo_get_source(cr), 2); // CAIRO_FILTER_BEST
        cairo_paint(cr);
        cairo_restore(cr);
    }

    private static IntPtr LoadPixbuf(byte[] bytes)
    {
        var loader = gdk_pixbuf_loader_new();
        try
        {
            if (!gdk_pixbuf_loader_write(loader, bytes, (nuint)bytes.Length, IntPtr.Zero) ||
                !gdk_pixbuf_loader_close(loader, IntPtr.Zero))
            {
                throw new InvalidOperationException("GTK could not decode the ePlugin logo.");
            }

            // The loader owns its pixbuf; keep it beyond the loader.
            return g_object_ref(gdk_pixbuf_loader_get_pixbuf(loader));
        }
        finally
        {
            g_object_unref(loader);
        }
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

    private delegate bool DrawCallback(IntPtr widget, IntPtr cr, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct GdkRgba
    {
        public double Red;
        public double Green;
        public double Blue;
        public double Alpha;
    }

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
    private static extern void gtk_window_set_decorated(IntPtr window, bool setting);

    [DllImport(Gtk)]
    private static extern void gtk_window_set_type_hint(IntPtr window, int hint);

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
    private static extern void gtk_widget_set_app_paintable(IntPtr widget, bool setting);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_widget_get_screen(IntPtr widget);

    [DllImport(Gtk)]
    private static extern void gtk_widget_set_visual(IntPtr widget, IntPtr visual);

    [DllImport(Gtk)]
    private static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);

    [DllImport(Gtk)]
    private static extern void gtk_widget_get_preferred_height(IntPtr widget, out int minimum, out int natural);

    [DllImport(Gtk)]
    private static extern void gtk_widget_set_opacity(IntPtr widget, double opacity);

    [DllImport(Gtk)]
    private static extern void gtk_widget_queue_draw(IntPtr widget);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_widget_get_style_context(IntPtr widget);

    [DllImport(Gtk)]
    private static extern int gtk_widget_get_state_flags(IntPtr widget);

    [DllImport(Gtk)]
    private static extern void gtk_style_context_get_color(IntPtr context, int state, out GdkRgba color);

    [DllImport(Gtk)]
    private static extern void gtk_render_background(IntPtr context, IntPtr cr, double x, double y, double width,
        double height);

    [DllImport(Gtk)]
    private static extern void gtk_container_add(IntPtr container, IntPtr widget);

    [DllImport(Gtk)]
    private static extern IntPtr gtk_fixed_new();

    [DllImport(Gtk)]
    private static extern void gtk_fixed_put(IntPtr fixedLayout, IntPtr widget, int x, int y);

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

    [DllImport(Gdk)]
    private static extern IntPtr gdk_screen_get_rgba_visual(IntPtr screen);

    [DllImport(Gdk)]
    private static extern bool gdk_screen_is_composited(IntPtr screen);

    [DllImport(Gdk)]
    private static extern void gdk_cairo_set_source_pixbuf(IntPtr cr, IntPtr pixbuf, double x, double y);

    [DllImport(Cairo)]
    private static extern void cairo_save(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_restore(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_set_operator(IntPtr cr, int op);

    [DllImport(Cairo)]
    private static extern void cairo_paint(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_paint_with_alpha(IntPtr cr, double alpha);

    [DllImport(Cairo)]
    private static extern void cairo_push_group(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_pop_group_to_source(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_new_sub_path(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_arc(IntPtr cr, double x, double y, double radius, double angle1, double angle2);

    [DllImport(Cairo)]
    private static extern void cairo_close_path(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_clip(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_stroke(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_set_line_width(IntPtr cr, double width);

    [DllImport(Cairo)]
    private static extern void cairo_set_source_rgba(IntPtr cr, double red, double green, double blue, double alpha);

    [DllImport(Cairo)]
    private static extern void cairo_translate(IntPtr cr, double x, double y);

    [DllImport(Cairo)]
    private static extern void cairo_scale(IntPtr cr, double x, double y);

    [DllImport(Cairo)]
    private static extern IntPtr cairo_get_source(IntPtr cr);

    [DllImport(Cairo)]
    private static extern void cairo_pattern_set_filter(IntPtr pattern, int filter);

    [DllImport(GLib)]
    private static extern uint g_idle_add(IntPtr function, IntPtr data);

    [DllImport(GLib)]
    private static extern uint g_timeout_add(uint interval, IntPtr function, IntPtr data);

    [DllImport(GObject)]
    private static extern ulong g_signal_connect_data(IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, IntPtr handler, IntPtr data, IntPtr destroyData,
        int flags);

    [DllImport(GObject)]
    private static extern IntPtr g_object_ref(IntPtr instance);

    [DllImport(GObject)]
    private static extern void g_object_unref(IntPtr instance);

    [DllImport(GdkPixbuf)]
    private static extern IntPtr gdk_pixbuf_loader_new();

    [DllImport(GdkPixbuf)]
    private static extern bool gdk_pixbuf_loader_write(IntPtr loader, byte[] buffer, nuint count, IntPtr error);

    [DllImport(GdkPixbuf)]
    private static extern bool gdk_pixbuf_loader_close(IntPtr loader, IntPtr error);

    [DllImport(GdkPixbuf)]
    private static extern IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);

    [DllImport(GdkPixbuf)]
    private static extern int gdk_pixbuf_get_width(IntPtr pixbuf);

    [DllImport(GdkPixbuf)]
    private static extern int gdk_pixbuf_get_height(IntPtr pixbuf);
}
