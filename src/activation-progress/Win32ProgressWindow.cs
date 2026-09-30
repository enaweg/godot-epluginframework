using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ActivationProgress;

internal sealed class Win32ProgressWindow : IProgressWindow
{
    private const string ClassName = "ePluginActivationProgress";
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmClose = 0x0010;
    private const uint WmSetFont = 0x0030;
    private const uint WmTimer = 0x0113;
    private const uint WmUpdateText = 0x8000; // WM_APP
    private const uint PbmSetMarquee = 0x0400 + 10;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SsNoPrefix = 0x0080;
    private const uint SsEndEllipsis = 0x4000;
    private const uint PbsMarquee = 0x08;
    private const uint IccProgressClass = 0x20;
    private const uint ActCtxFlagResourceNameValid = 0x08;
    private const int SwShowNoActivate = 4;
    private static readonly UIntPtr ShowTimerId = new(1);

    private readonly WndProc _wndProc;
    private readonly IntPtr _editorWindow;
    private IntPtr _window;
    private IntPtr _label;
    private IntPtr _logo;
    private IntPtr _logoStream;
    private nuint _gdiplusToken;
    private int _logoX;
    private int _logoY;
    private int _logoSize;
    private volatile string _text = string.Empty;

    /// <param name="editorWindow">The editor's window to center on, or zero for the primary screen.</param>
    public Win32ProgressWindow(IntPtr editorWindow)
    {
        _wndProc = WindowProc;
        _editorWindow = editorWindow;
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
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

        // A caption without a system menu has no close button; the window closes with the host or its lifetime.
        const uint style = WsCaption;
        // No activation: the editor keeps focus while the helper is up and after it exits.
        const uint exStyle = WsExTopmost | WsExToolWindow | WsExNoActivate;
        var bounds = new Rect { Right = Scale(340), Bottom = Scale(280) };
        AdjustWindowRectEx(ref bounds, style, false, exStyle);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        // Read in this process's DPI awareness, so the rectangle matches the scale the window is created at.
        var target = _editorWindow != IntPtr.Zero && GetWindowRect(_editorWindow, out var editor)
            ? editor
            : new Rect { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) }; // SM_CXSCREEN, SM_CYSCREEN
        var x = (target.Left + target.Right - width) / 2;
        var y = (target.Top + target.Bottom - height) / 2;
        _logoX = Scale((340 - Logo.Size) / 2);
        _logoY = Scale(20);
        _logoSize = Scale(Logo.Size);
        LoadLogo();

        _window = CreateWindowExW(exStyle, ClassName, title, style, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        _label = CreateWindowExW(0, "STATIC", text, WsChild | WsVisible | SsNoPrefix | SsEndEllipsis,
            Scale(20), Scale(195), Scale(300), Scale(20), _window, IntPtr.Zero, instance, IntPtr.Zero);
        var font = CreateFontW(-Scale(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // 9pt, CLEARTYPE_QUALITY
        SendMessageW(_label, WmSetFont, font, IntPtr.Zero);

        var progress = CreateWindowExW(0, "msctls_progress32", null, WsChild | WsVisible | PbsMarquee,
            Scale(20), Scale(229), Scale(300), Scale(18), _window, IntPtr.Zero, instance, IntPtr.Zero);
        SendMessageW(progress, PbmSetMarquee, new IntPtr(1), new IntPtr(30));

        SetTimer(_window, ShowTimerId, (uint)showDelayMilliseconds, IntPtr.Zero);
        ready();

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        GdipDisposeImage(_logo);
        Marshal.Release(_logoStream);
        GdiplusShutdown(_gdiplusToken);
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
            case WmPaint:
                var paintDc = BeginPaint(window, out var paint);
                if (GdipCreateFromHDC(paintDc, out var graphics) == 0)
                {
                    GdipSetInterpolationMode(graphics, 7); // InterpolationModeHighQualityBicubic
                    GdipDrawImageRectI(graphics, _logo, _logoX, _logoY, _logoSize, _logoSize);
                    GdipDeleteGraphics(graphics);
                }

                EndPaint(window, ref paint);
                return IntPtr.Zero;
            case WmUpdateText:
                SetWindowTextW(_label, _text);
                return IntPtr.Zero;
            case WmTimer when wParam == (IntPtr)ShowTimerId:
                KillTimer(window, ShowTimerId);
                ShowWindow(window, SwShowNoActivate);
                UpdateWindow(window);
                return IntPtr.Zero;
            case WmClose:
                // Alt+F4 and similar; only the host or the lifetime timer end the helper.
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private void LoadLogo()
    {
        var startup = new GdiplusStartupInput { Version = 1 };
        if (GdiplusStartup(out _gdiplusToken, ref startup, IntPtr.Zero) != 0)
        {
            throw new Win32Exception("GDI+ could not start.");
        }

        var bytes = Logo.Load();
        _logoStream = SHCreateMemStream(bytes, (uint)bytes.Length);
        if (_logoStream == IntPtr.Zero || GdipCreateBitmapFromStream(_logoStream, out _logo) != 0)
        {
            throw new Win32Exception("GDI+ could not decode the ePlugin logo.");
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
            // System-aware matches the fixed layout: it is scaled once by the system DPI, and Windows scales the
            // window on monitors with another DPI instead of the helper handling WM_DPICHANGED.
            SetProcessDpiAwarenessContext(new IntPtr(-2)); // SYSTEM_AWARE
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
    private struct PaintStruct
    {
        public IntPtr Dc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
        public uint Reserved4;
        public uint Reserved5;
        public uint Reserved6;
        public uint Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint Version;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
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

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(byte[] bytes, uint length);

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(nuint token);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromStream(IntPtr stream, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateFromHDC(IntPtr dc, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

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
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

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
    private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint elapse, IntPtr timerFunction);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr window, UIntPtr id);

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
