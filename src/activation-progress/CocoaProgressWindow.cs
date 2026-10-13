using System.Runtime.InteropServices;

namespace ActivationProgress;

/// <summary>
/// Borderless, transparent AppKit window driven through the Objective-C runtime: the logo's parts are image views
/// around a custom box in the window background color. Must run on the process main thread.
/// </summary>
internal sealed class CocoaProgressWindow : IProgressWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const ulong DispatchTimeNow = 0;

    // Kept in fields so the delegates outlive the native callbacks that reference them.
    private readonly DispatchFunction _ready;
    private readonly DispatchFunction _show;
    private readonly DispatchFunction _frame;
    private readonly DispatchFunction _close;
    private readonly EditorCenter? _editorCenter;
    private readonly Animation _animation = new();
    private IntPtr _mainQueue;
    private Action? _onReady;
    private IntPtr _window;
    private IntPtr _panel;
    private IntPtr _head;
    private IntPtr _puzzle;
    private IntPtr _label;
    private IntPtr _progress;
    private volatile string _text = string.Empty;
    private string? _shownText;

    /// <param name="editorCenter">
    /// The editor's center in AppKit screen points (bottom-left origin), or null for the screen center.
    /// </param>
    public CocoaProgressWindow(EditorCenter? editorCenter)
    {
        _editorCenter = editorCenter;
        _ready = _ => _onReady?.Invoke();
        _show = _ =>
        {
            _animation.Open();
            Update();
            // Ordered front without activating the helper, so the editor keeps focus.
            Send(_window, Sel("orderFrontRegardless"));
            ScheduleFrame();
        };
        _frame = _ =>
        {
            if (_animation.Closed)
            {
                Environment.Exit(0);
            }

            Update();
            ScheduleFrame();
        };
        _close = _ =>
        {
            if (_animation.Started)
            {
                _animation.Close();
            }
            else
            {
                Environment.Exit(0);
            }
        };
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
    {
        NativeLibrary.Load(AppKit);
        // dispatch_get_main_queue() is a macro for the address of this symbol.
        _mainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");
        Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));

        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        Send(app, Sel("setActivationPolicy:"), 1L); // NSApplicationActivationPolicyAccessory: no Dock icon

        _text = text;
        var width = Layout.Width;
        var height = Layout.Height;
        var contentRect = _editorCenter is { } center
            ? new CGRect(center.X - width / 2, center.Y - height / 2, width, height)
            : new CGRect(0, 0, width, height);
        // Borderless: no close button; the window closes with the host or its lifetime.
        var window = _window = Send(Send(Class("NSWindow"), Sel("alloc")),
            Sel("initWithContentRect:styleMask:backing:defer:"),
            contentRect, 0UL /* borderless */, 2UL /* buffered */, false);
        Send(window, Sel("setTitle:"), NSString(title));
        Send(window, Sel("setLevel:"), 3L); // NSFloatingWindowLevel
        Send(window, Sel("setOpaque:"), false);
        Send(window, Sel("setBackgroundColor:"), Send(Class("NSColor"), Sel("clearColor")));
        // The shadow of a transparent window follows its content only when it is recomputed, not while it animates.
        Send(window, Sel("setHasShadow:"), false);
        Send(window, Sel("setAlphaValue:"), 0.0);
        if (_editorCenter is null)
        {
            Send(window, Sel("center"));
        }

        var content = Send(window, Sel("contentView"));
        Send(content, Sel("setWantsLayer:"), true);

        _panel = Send(Send(Class("NSBox"), Sel("alloc")), Sel("initWithFrame:"), Frame(Layout.FullPanel));
        Send(_panel, Sel("setBoxType:"), 4L); // NSBoxCustom
        Send(_panel, Sel("setTitlePosition:"), 0L); // NSNoTitle
        Send(_panel, Sel("setFillColor:"), Send(Class("NSColor"), Sel("windowBackgroundColor")));
        Send(_panel, Sel("setBorderColor:"), Send(Class("NSColor"), Sel("separatorColor")));
        Send(_panel, Sel("setBorderWidth:"), 1.0);
        Send(_panel, Sel("setCornerRadius:"), Layout.PanelRadius);
        Send(_panel, Sel("setHidden:"), true);
        Send(content, Sel("addSubview:"), _panel);

        _label = Send(Class("NSTextField"), Sel("labelWithString:"), NSString(text));
        Send(_label, Sel("setFrame:"), Frame(Layout.Label));
        Send(_label, Sel("setLineBreakMode:"), 4L); // NSLineBreakByTruncatingTail
        Send(_label, Sel("setAlphaValue:"), 0.0);
        Send(content, Sel("addSubview:"), _label);

        _progress = Send(Send(Class("NSProgressIndicator"), Sel("alloc")), Sel("initWithFrame:"), Frame(Layout.Bar));
        Send(_progress, Sel("setStyle:"), 0L); // NSProgressIndicatorStyleBar
        Send(_progress, Sel("setIndeterminate:"), true);
        Send(_progress, Sel("setAlphaValue:"), 0.0);
        Send(_progress, Sel("startAnimation:"), IntPtr.Zero);
        Send(content, Sel("addSubview:"), _progress);

        // Above the panel, whose edges they cover.
        _head = ImageView(content, Logo.LoadHead());
        _puzzle = ImageView(content, Logo.LoadPuzzle());

        dispatch_after_f(dispatch_time(DispatchTimeNow, showDelayMilliseconds * 1_000_000L), _mainQueue, IntPtr.Zero,
            Marshal.GetFunctionPointerForDelegate(_show));

        _onReady = ready;
        dispatch_async_f(_mainQueue, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_ready));
        Send(app, Sel("run"));
    }

    public void SetText(string text)
    {
        // Shown with the next frame.
        _text = text;
    }

    public void Close()
    {
        dispatch_async_f(_mainQueue, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_close));
    }

    private void ScheduleFrame()
    {
        dispatch_after_f(dispatch_time(DispatchTimeNow, Animation.FrameMilliseconds * 1_000_000L), _mainQueue,
            IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_frame));
    }

    private void Update()
    {
        var frame = _animation.Frame;
        var text = _text;
        if (!ReferenceEquals(text, _shownText))
        {
            Send(_label, Sel("setStringValue:"), NSString(text));
            _shownText = text;
        }

        Send(_window, Sel("setAlphaValue:"), frame.Opacity);
        if (Layout.PanelAt(frame.Split) is { } panel)
        {
            Send(_panel, Sel("setFrame:"), Frame(panel));
            Send(_panel, Sel("setHidden:"), false);
        }
        else
        {
            Send(_panel, Sel("setHidden:"), true);
        }

        Send(_head, Sel("setFrame:"), Frame(Layout.HeadAt(frame.Split)));
        Send(_puzzle, Sel("setFrame:"), Frame(Layout.PuzzleAt(frame.Split)));
        Send(_label, Sel("setAlphaValue:"), frame.Controls);
        Send(_progress, Sel("setAlphaValue:"), frame.Controls);
    }

    private static IntPtr ImageView(IntPtr content, byte[] bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        IntPtr data;
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            data = Send(Class("NSData"), Sel("dataWithBytes:length:"), buffer, (ulong)bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
        if (image == IntPtr.Zero)
        {
            throw new InvalidOperationException("AppKit could not decode the ePlugin logo.");
        }

        var view = Send(Send(Class("NSImageView"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, 1, 1));
        Send(view, Sel("setImage:"), image);
        Send(view, Sel("setImageScaling:"), 1L); // NSImageScaleAxesIndependently: the layout keeps the aspect ratio
        Send(content, Sel("addSubview:"), view);
        return view;
    }

    /// <summary>AppKit's origin is bottom-left.</summary>
    private static CGRect Frame(Box box) => new(box.X, Layout.Height - box.Bottom, box.Width, box.Height);

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr NSString(string value) => Send(Class("NSString"), Sel("stringWithUTF8String:"), value);

    private delegate void DispatchFunction(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument, ulong length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, long argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, double argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, CGRect argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, CGRect rect, ulong styleMask, ulong backing,
        [MarshalAs(UnmanagedType.I1)] bool defer);

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);

    [DllImport(LibSystem)]
    private static extern ulong dispatch_time(ulong when, long deltaNanoseconds);

    [DllImport(LibSystem)]
    private static extern void dispatch_after_f(ulong when, IntPtr queue, IntPtr context, IntPtr work);
}
