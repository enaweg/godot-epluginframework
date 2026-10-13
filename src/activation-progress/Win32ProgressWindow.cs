using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ActivationProgress;

/// <summary>
/// A layered window without a frame: everything is drawn into one per-pixel-alpha buffer each frame, so the
/// logo's parts can float on the desktop around a panel in the system's dialog colors.
/// </summary>
internal sealed class Win32ProgressWindow : IProgressWindow
{
    private const string ClassName = "ePluginActivationProgress";
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmTimer = 0x0113;
    private const uint WmCloseAnimated = 0x8000; // WM_APP
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint ActCtxFlagResourceNameValid = 0x08;
    private const int SwShowNoActivate = 4;
    private const int ColorBtnFace = 15;
    private const int ColorBtnText = 18;
    private const int ColorBtnShadow = 16;
    private const int ColorHighlight = 13;
    private const int ColorWindow = 5;
    private const uint DtSingleLine = 0x20;
    private const uint DtVCenter = 0x04;
    private const uint DtNoPrefix = 0x800;
    private const uint DtEndEllipsis = 0x8000;
    private const int PixelFormat32bppPArgb = 0x000E200B;
    private const int ProgressBar = 1; // PP_BAR
    private const int ProgressFill = 5; // PP_FILL
    private const double MarqueeSeconds = 2.0;
    private static readonly UIntPtr ShowTimerId = new(1);
    private static readonly UIntPtr FrameTimerId = new(2);

    private readonly WndProc _wndProc;
    private readonly IntPtr _editorWindow;
    private readonly Animation _animation = new();
    private readonly Stopwatch _marquee = Stopwatch.StartNew();
    private IntPtr _window;
    private double _scale;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private nuint _gdiplusToken;
    private IntPtr _head;
    private IntPtr _puzzle;
    private Canvas _canvas = null!;
    private Canvas _controls = null!;
    private IntPtr _graphics;
    private IntPtr _font;
    private IntPtr _theme;
    private volatile string _text = string.Empty;

    /// <param name="editorWindow">The editor's window to center on, or zero for the primary screen.</param>
    public Win32ProgressWindow(IntPtr editorWindow)
    {
        _wndProc = WindowProc;
        _editorWindow = editorWindow;
    }

    public void Run(string title, string text, int showDelayMilliseconds, Action ready)
    {
        _text = text;
        EnableVisualStyles();
        _scale = EnableDpiAwareness() / 96.0;

        var instance = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
            ClassName = ClassName
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception();
        }

        _width = Layout.Scale(Layout.Width, _scale);
        _height = Layout.Scale(Layout.Height, _scale);
        // Read in this process's DPI awareness, so the rectangle matches the scale the window is created at.
        var target = _editorWindow != IntPtr.Zero && GetWindowRect(_editorWindow, out var editor)
            ? editor
            : new Rect { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) }; // SM_CXSCREEN, SM_CYSCREEN
        _x = (target.Left + target.Right - _width) / 2;
        _y = (target.Top + target.Bottom - _height) / 2;

        // No frame and no activation: the editor keeps focus while the helper is up and after it exits. Layered
        // windows let clicks through wherever they are transparent.
        _window = CreateWindowExW(WsExLayered | WsExTopmost | WsExToolWindow | WsExNoActivate, ClassName, title,
            WsPopup, _x, _y, _width, _height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        LoadImages();
        _canvas = new Canvas(_width, _height);
        if (GdipCreateBitmapFromScan0(_width, _height, _width * 4, PixelFormat32bppPArgb, _canvas.Bits, out var bitmap) != 0 ||
            GdipGetImageGraphicsContext(bitmap, out _graphics) != 0)
        {
            throw new Win32Exception("GDI+ could not draw into the window buffer.");
        }

        GdipSetSmoothingMode(_graphics, 4); // SmoothingModeAntiAlias
        GdipSetInterpolationMode(_graphics, 7); // InterpolationModeHighQualityBicubic
        GdipSetPixelOffsetMode(_graphics, 4); // PixelOffsetModeHighQuality
        _controls = new Canvas(Layout.Scale(Layout.FullPanel.Width, _scale), Layout.Scale(Layout.FullPanel.Height, _scale));
        _font = CreateFontW(-Layout.Scale(12, _scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // 9pt, CLEARTYPE_QUALITY
        SelectObject(_controls.Dc, _font);
        _theme = OpenThemeData(_window, "PROGRESS");

        SetTimer(_window, ShowTimerId, (uint)showDelayMilliseconds, IntPtr.Zero);
        ready();

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        if (_theme != IntPtr.Zero) CloseThemeData(_theme);
        GdipDeleteGraphics(_graphics);
        GdipDisposeImage(bitmap);
        GdipDisposeImage(_head);
        GdipDisposeImage(_puzzle);
        GdiplusShutdown(_gdiplusToken);
    }

    public void SetText(string text)
    {
        // Drawn with the next frame.
        _text = text;
    }

    public void Close()
    {
        PostMessageW(_window, WmCloseAnimated, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmTimer when wParam == (IntPtr)ShowTimerId:
                KillTimer(window, ShowTimerId);
                _animation.Open();
                Draw();
                ShowWindow(window, SwShowNoActivate);
                SetTimer(window, FrameTimerId, Animation.FrameMilliseconds, IntPtr.Zero);
                return IntPtr.Zero;
            case WmTimer when wParam == (IntPtr)FrameTimerId:
                if (_animation.Closed)
                {
                    DestroyWindow(window);
                    return IntPtr.Zero;
                }

                Draw();
                return IntPtr.Zero;
            case WmCloseAnimated:
                if (_animation.Started)
                {
                    _animation.Close();
                }
                else
                {
                    DestroyWindow(window);
                }

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

    private void Draw()
    {
        var frame = _animation.Frame;
        GdipGraphicsClear(_graphics, 0);
        if (Layout.PanelAt(frame.Split) is { } panel)
        {
            DrawPanel(panel);
            if (frame.Controls > 0)
            {
                GdipFlush(_graphics, 1); // FlushIntentionSync: the panel must be in the buffer before blending
                DrawControls(frame.Controls);
            }
        }

        DrawImage(_head, Layout.HeadAt(frame.Split));
        DrawImage(_puzzle, Layout.PuzzleAt(frame.Split));
        GdipFlush(_graphics, 1);

        var position = new Point { X = _x, Y = _y };
        var size = new Size { Width = _width, Height = _height };
        var source = new Point();
        var blend = new BlendFunction
        {
            SourceConstantAlpha = (byte)Math.Round(frame.Opacity * 255),
            AlphaFormat = 1 // AC_SRC_ALPHA
        };
        UpdateLayeredWindow(_window, IntPtr.Zero, ref position, ref size, _canvas.Dc, ref source, 0, ref blend, 2); // ULW_ALPHA
    }

    private void DrawPanel(Box panel)
    {
        float x = (float)(panel.X * _scale), y = (float)(panel.Y * _scale);
        float width = (float)(panel.Width * _scale), height = (float)(panel.Height * _scale);
        var diameter = (float)Math.Min(2 * Layout.PanelRadius * _scale, Math.Min(width, height));
        GdipCreatePath(0, out var path);
        GdipAddPathArc(path, x, y, diameter, diameter, 180, 90);
        GdipAddPathArc(path, x + width - diameter, y, diameter, diameter, 270, 90);
        GdipAddPathArc(path, x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        GdipAddPathArc(path, x, y + height - diameter, diameter, diameter, 90, 90);
        GdipClosePathFigure(path);
        GdipCreateSolidFill(Argb(GetSysColor(ColorBtnFace)), out var brush);
        GdipFillPath(_graphics, brush, path);
        GdipDeleteBrush(brush);
        GdipCreatePen1(Argb(GetSysColor(ColorBtnShadow)), 1, 0, out var pen);
        GdipDrawPath(_graphics, pen, path);
        GdipDeletePen(pen);
        GdipDeletePath(path);
    }

    /// <summary>
    /// Draws the label and the progress bar with GDI and the visual style into an opaque buffer of the panel's size,
    /// then blends their area over the panel: GDI cannot draw into a buffer with an alpha channel.
    /// </summary>
    private void DrawControls(double opacity)
    {
        var panelX = Layout.Scale(Layout.FullPanel.X, _scale);
        var panelY = Layout.Scale(Layout.FullPanel.Y, _scale);
        Rect Local(Box box) => new()
        {
            Left = Layout.Scale(box.X, _scale) - panelX, Top = Layout.Scale(box.Y, _scale) - panelY,
            Right = Layout.Scale(box.Right, _scale) - panelX, Bottom = Layout.Scale(box.Bottom, _scale) - panelY
        };

        var all = new Rect { Right = _controls.Width, Bottom = _controls.Height };
        FillRect(_controls.Dc, ref all, GetSysColorBrush(ColorBtnFace));
        var label = Local(Layout.Label);
        SetBkMode(_controls.Dc, 1); // TRANSPARENT
        SetTextColor(_controls.Dc, GetSysColor(ColorBtnText));
        DrawTextW(_controls.Dc, _text, -1, ref label, DtSingleLine | DtVCenter | DtNoPrefix | DtEndEllipsis);
        var bar = Local(Layout.Bar);
        DrawBar(bar);

        var top = Math.Min(label.Top, bar.Top);
        var bottom = Math.Max(label.Bottom, bar.Bottom);
        var left = Math.Min(label.Left, bar.Left);
        var right = Math.Max(label.Right, bar.Right);
        var background = (int)Argb(GetSysColor(ColorBtnFace));
        var row = new int[right - left];
        var target = new int[right - left];
        for (var y = top; y < bottom; y++)
        {
            Marshal.Copy(_controls.Bits + (y * _controls.Width + left) * 4, row, 0, row.Length);
            Marshal.Copy(_canvas.Bits + ((y + panelY) * _width + left + panelX) * 4, target, 0, target.Length);
            for (var x = 0; x < row.Length; x++)
            {
                target[x] = Mix(background, row[x], opacity);
            }

            Marshal.Copy(target, 0, _canvas.Bits + ((y + panelY) * _width + left + panelX) * 4, target.Length);
        }
    }

    private void DrawBar(Rect bar)
    {
        var phase = _marquee.Elapsed.TotalSeconds % MarqueeSeconds / MarqueeSeconds;
        if (_theme != IntPtr.Zero &&
            DrawThemeBackground(_theme, _controls.Dc, ProgressBar, 1, ref bar, IntPtr.Zero) == 0 &&
            GetThemeBackgroundContentRect(_theme, _controls.Dc, ProgressBar, 1, ref bar, out var content) == 0)
        {
            var chunk = Chunk(content, phase);
            DrawThemeBackground(_theme, _controls.Dc, ProgressFill, 1, ref chunk, ref content);
            return;
        }

        // Classic theme.
        FillRect(_controls.Dc, ref bar, GetSysColorBrush(ColorWindow));
        FrameRect(_controls.Dc, ref bar, GetSysColorBrush(ColorBtnShadow));
        var inner = new Rect { Left = bar.Left + 1, Top = bar.Top + 1, Right = bar.Right - 1, Bottom = bar.Bottom - 1 };
        var fill = Chunk(inner, phase);
        fill.Left = Math.Max(fill.Left, inner.Left);
        fill.Right = Math.Min(fill.Right, inner.Right);
        if (fill.Right > fill.Left) FillRect(_controls.Dc, ref fill, GetSysColorBrush(ColorHighlight));
    }

    /// <summary>The indeterminate bar's moving chunk, entering on the left and leaving on the right.</summary>
    private static Rect Chunk(Rect content, double phase)
    {
        var width = (content.Right - content.Left) / 4;
        var left = content.Left - width + (int)Math.Round((content.Right - content.Left + width) * phase);
        return content with { Left = left, Right = left + width };
    }

    private void DrawImage(IntPtr image, Box box)
    {
        GdipDrawImageRect(_graphics, image, (float)(box.X * _scale), (float)(box.Y * _scale),
            (float)(box.Width * _scale), (float)(box.Height * _scale));
    }

    /// <summary>A COLORREF as an opaque ARGB value.</summary>
    private static uint Argb(uint color) =>
        0xFF000000 | (color & 0xFF) << 16 | color & 0xFF00 | (color >> 16 & 0xFF);

    /// <summary>Blends two opaque BGRA pixels; the result is opaque, whatever GDI left in the alpha byte.</summary>
    private static int Mix(int from, int to, double amount)
    {
        int Channel(int shift) => (int)Math.Round((from >> shift & 0xFF) * (1 - amount) + (to >> shift & 0xFF) * amount) << shift;
        return unchecked((int)0xFF000000) | Channel(16) | Channel(8) | Channel(0);
    }

    private void LoadImages()
    {
        var startup = new GdiplusStartupInput { Version = 1 };
        if (GdiplusStartup(out _gdiplusToken, ref startup, IntPtr.Zero) != 0)
        {
            throw new Win32Exception("GDI+ could not start.");
        }

        _head = LoadImage(Logo.LoadHead());
        _puzzle = LoadImage(Logo.LoadPuzzle());
    }

    private static IntPtr LoadImage(byte[] bytes)
    {
        var stream = SHCreateMemStream(bytes, (uint)bytes.Length);
        try
        {
            if (stream == IntPtr.Zero || GdipCreateBitmapFromStream(stream, out var image) != 0)
            {
                throw new Win32Exception("GDI+ could not decode the ePlugin logo.");
            }

            // GDI+ decodes lazily from the stream; a clone owns its pixels.
            var clone = IntPtr.Zero;
            GdipGetImageWidth(image, out var width);
            GdipGetImageHeight(image, out var height);
            var status = GdipCloneBitmapAreaI(0, 0, (int)width, (int)height, PixelFormat32bppPArgb, image, out clone);
            GdipDisposeImage(image);
            if (status != 0) throw new Win32Exception("GDI+ could not decode the ePlugin logo.");
            return clone;
        }
        finally
        {
            if (stream != IntPtr.Zero) Marshal.Release(stream);
        }
    }

    /// <summary>
    /// The process runs as dotnet.exe, whose manifest does not request Common Controls 6 (needed for the
    /// progress bar's visual style), so activate the manifest embedded in this assembly instead.
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

    /// <summary>A memory DC with a top-down 32-bit DIB selected into it. Lives as long as the process.</summary>
    private sealed class Canvas
    {
        public Canvas(int width, int height)
        {
            Width = width;
            Height = height;
            var info = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32
            };
            Dc = CreateCompatibleDC(IntPtr.Zero);
            var bitmap = CreateDIBSection(Dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (Dc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception("The window buffer could not be created.");
            }

            Bits = bits;
            SelectObject(Dc, bitmap);
        }

        public int Width { get; }
        public int Height { get; }
        public IntPtr Dc { get; }
        public IntPtr Bits { get; }
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
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
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
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0,
        out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCloneBitmapAreaI(int x, int y, int width, int height, int format, IntPtr source,
        out IntPtr clone);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGraphicsClear(IntPtr graphics, uint color);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFlush(IntPtr graphics, int intention);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawImageRect(IntPtr graphics, IntPtr image, float x, float y, float width,
        float height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePath(int fillMode, out IntPtr path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipAddPathArc(IntPtr path, float x, float y, float width, float height,
        float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    private static extern int GdipClosePathFigure(IntPtr path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePath(IntPtr path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePen1(uint color, float width, int unit, out IntPtr pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePen(IntPtr pen);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenThemeData(IntPtr window, string classList);

    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(IntPtr theme);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr theme, IntPtr dc, int part, int state, ref Rect rect,
        IntPtr clip);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr theme, IntPtr dc, int part, int state, ref Rect rect,
        ref Rect clip);

    [DllImport("uxtheme.dll")]
    private static extern int GetThemeBackgroundContentRect(IntPtr theme, IntPtr dc, int part, int state,
        ref Rect bounds, out Rect content);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int count, ref Rect rect, uint format);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSysColorBrush(int index);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc, ref Point position, ref Size size,
        IntPtr sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

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
