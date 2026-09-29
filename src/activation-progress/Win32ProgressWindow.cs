using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ActivationProgress;

internal sealed class Win32ProgressWindow : IProgressWindow
{
    private const string ClassName = "ePluginActivationProgress";
    private const uint WmDestroy = 0x0002;
    private const uint WmSetFont = 0x0030;
    private const uint WmUpdateText = 0x8000; // WM_APP
    private const uint PbmSetMarquee = 0x0400 + 10;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SsNoPrefix = 0x0080;
    private const uint SsEndEllipsis = 0x4000;
    private const uint PbsMarquee = 0x08;
    private const uint IccProgressClass = 0x20;
    private const uint ActCtxFlagResourceNameValid = 0x08;

    private readonly WndProc _wndProc;
    private IntPtr _window;
    private IntPtr _label;
    private volatile string _text = string.Empty;

    public Win32ProgressWindow()
    {
        _wndProc = WindowProc;
    }

    public void Run(string title, string text, Action shown)
    {
        EnableVisualStyles();
        var dpi = EnableDpiAwareness();
        int Scale(int value) => value * dpi / 96;

        var controls = new InitCommonControlsExInfo
        {
            Size = Marshal.SizeOf<InitCommonControlsExInfo>(),
            Classes = IccProgressClass
        };
        InitCommonControlsEx(ref controls);

        var instance = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
            Background = new IntPtr(15 + 1), // COLOR_BTNFACE
            ClassName = ClassName
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception();
        }

        const uint style = WsCaption | WsSysMenu;
        const uint exStyle = WsExTopmost | WsExToolWindow;
        var bounds = new Rect { Right = Scale(340), Bottom = Scale(100) };
        AdjustWindowRectEx(ref bounds, style, false, exStyle);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var x = (GetSystemMetrics(0) - width) / 2; // SM_CXSCREEN
        var y = (GetSystemMetrics(1) - height) / 2; // SM_CYSCREEN

        _window = CreateWindowExW(exStyle, ClassName, title, style, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        _label = CreateWindowExW(0, "STATIC", text, WsChild | WsVisible | SsNoPrefix | SsEndEllipsis,
            Scale(20), Scale(20), Scale(300), Scale(20), _window, IntPtr.Zero, instance, IntPtr.Zero);
        var font = CreateFontW(-Scale(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // 9pt, CLEARTYPE_QUALITY
        SendMessageW(_label, WmSetFont, font, IntPtr.Zero);

        var progress = CreateWindowExW(0, "msctls_progress32", null, WsChild | WsVisible | PbsMarquee,
            Scale(20), Scale(54), Scale(300), Scale(18), _window, IntPtr.Zero, instance, IntPtr.Zero);
        SendMessageW(progress, PbmSetMarquee, new IntPtr(1), new IntPtr(30));

        ShowWindow(_window, 5); // SW_SHOW
        UpdateWindow(_window);
        shown();

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    public void SetText(string text)
    {
        _text = text;
        PostMessageW(_window, WmUpdateText, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmUpdateText:
                SetWindowTextW(_label, _text);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    /// <summary>
    /// The process runs as dotnet.exe, whose manifest does not request Common Controls 6 (needed for the
    /// marquee progress bar), so activate the manifest embedded in this assembly instead.
    /// </summary>
    private static void EnableVisualStyles()
    {
        var context = new ActCtx
        {
            Size = Marshal.SizeOf<ActCtx>(),
            Flags = ActCtxFlagResourceNameValid,
            Source = typeof(Win32ProgressWindow).Assembly.Location,
            ResourceName = new IntPtr(1)
        };
        var handle = CreateActCtxW(ref context);
        if (handle != new IntPtr(-1))
        {
            ActivateActCtx(handle, out _);
        }
    }

    private static int EnableDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            return (int)GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
            // Older than Windows 10 1703: stay DPI-unaware and let Windows scale the window.
            return 96;
        }
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActCtx
    {
        public int Size;
        public uint Flags;
        public string Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public string? AssemblyDirectory;
        public IntPtr ResourceName;
        public string? ApplicationName;
        public IntPtr Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitCommonControlsExInfo
    {
        public int Size;
        public uint Classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateActCtxW(ref ActCtx context);

    [DllImport("kernel32.dll")]
    private static extern bool ActivateActCtx(IntPtr context, out IntPtr cookie);

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref InitCommonControlsExInfo info);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint exStyle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr window, string text);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}
