using System.Runtime.InteropServices;

namespace ActivationProgress;

/// <summary>AppKit window driven through the Objective-C runtime. Must run on the process main thread.</summary>
internal sealed class CocoaProgressWindow : IProgressWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const ulong DispatchTimeNow = 0;

    // Kept in fields so the delegates outlive the native callbacks that reference them.
    private readonly DispatchFunction _updateText;
    private readonly DispatchFunction _ready;
    private readonly DispatchFunction _show;
    private IntPtr _mainQueue;
    private Action? _onReady;
    private IntPtr _window;
    private IntPtr _label;
    private volatile string _text = string.Empty;

    public CocoaProgressWindow()
    {
        _updateText = _ => Send(_label, Sel("setStringValue:"), NSString(_text));
        _ready = _ => _onReady?.Invoke();
        // Ordered front without activating the helper, so the editor keeps focus.
        _show = _ => Send(_window, Sel("orderFrontRegardless"));
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
    {
        NativeLibrary.Load(AppKit);
        // dispatch_get_main_queue() is a macro for the address of this symbol.
        _mainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");
        Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));

        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        Send(app, Sel("setActivationPolicy:"), 1L); // NSApplicationActivationPolicyAccessory: no Dock icon

        var window = _window = Send(Send(Class("NSWindow"), Sel("alloc")),
            Sel("initWithContentRect:styleMask:backing:defer:"),
            new CGRect(0, 0, 340, 100), 1UL /* titled */, 2UL /* buffered */, false);
        Send(window, Sel("setTitle:"), NSString(title));
        Send(window, Sel("setLevel:"), 3L); // NSFloatingWindowLevel
        Send(window, Sel("center"));
        var content = Send(window, Sel("contentView"));

        // AppKit's origin is bottom-left.
        _label = Send(Class("NSTextField"), Sel("labelWithString:"), NSString(text));
        Send(_label, Sel("setFrame:"), new CGRect(20, 56, 300, 20));
        Send(_label, Sel("setLineBreakMode:"), 4L); // NSLineBreakByTruncatingTail
        Send(content, Sel("addSubview:"), _label);

        var progress = Send(Send(Class("NSProgressIndicator"), Sel("alloc")), Sel("initWithFrame:"),
            new CGRect(20, 22, 300, 20));
        Send(progress, Sel("setStyle:"), 0L); // NSProgressIndicatorStyleBar
        Send(progress, Sel("setIndeterminate:"), true);
        Send(progress, Sel("startAnimation:"), IntPtr.Zero);
        Send(content, Sel("addSubview:"), progress);

        dispatch_after_f(dispatch_time(DispatchTimeNow, showDelayMilliseconds * 1_000_000L), _mainQueue, IntPtr.Zero,
            Marshal.GetFunctionPointerForDelegate(_show));

        _onReady = ready;
        dispatch_async_f(_mainQueue, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_ready));
        Send(app, Sel("run"));
    }

    public void SetText(string text)
    {
        _text = text;
        dispatch_async_f(_mainQueue, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_updateText));
    }

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
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, long argument);

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
