using System.Globalization;
using System.Text;

namespace ActivationProgress;

/// <summary>
/// Usage: <c>dotnet ActivationProgress.dll [--editor-window HWND | --editor-center X Y] [--max-lifetime-seconds N]</c>.
/// <c>--editor-window</c> (Windows) and <c>--editor-center</c> (native desktop coordinates, macOS and Linux) place the
/// window over the editor instead of on the primary screen.
/// Commands on stdin, one per line: <c>TEXT base64-utf8</c> replaces the label, <c>HEARTBEAT</c> restarts the
/// lifetime, and <c>CLOSE</c> (or end of input) exits.
/// </summary>
internal static class Program
{
    private const string Title = "ePlugin Framework";
    private const string InitialText = "Working...";

    // Operations that finish sooner close the helper before its window ever appears.
    private const int ShowDelayMilliseconds = 300;

    // The window has no close button, so a host that hangs or never sends CLOSE cannot leave it up forever.
    // Each HEARTBEAT restarts it.
    private const int DefaultMaxLifetimeSeconds = 15 * 60;

    [STAThread]
    private static int Main(string[] args)
    {
        IProgressWindow window;
        if (OperatingSystem.IsWindows())
        {
            var handle = Option(args, "--editor-window", 1) is [var value] &&
                         long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? new IntPtr(parsed)
                : IntPtr.Zero;
            window = new Win32ProgressWindow(handle);
        }
        else if (OperatingSystem.IsMacOS())
        {
            window = new CocoaProgressWindow(ParseEditorCenter(args));
        }
        else if (OperatingSystem.IsLinux())
        {
            window = new GtkProgressWindow(ParseEditorCenter(args));
        }
        else
        {
            return 1;
        }

        var maxLifetimeSeconds = Option(args, "--max-lifetime-seconds", 1) is [var seconds] &&
                                 int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                     out var parsedSeconds) && parsedSeconds > 0
            ? parsedSeconds
            : DefaultMaxLifetimeSeconds;
        var maxLifetime = TimeSpan.FromSeconds(maxLifetimeSeconds);
        // Not disposed: the reader thread may still restart it while the process exits.
        var lifetime = new Timer(_ => Environment.Exit(0), null, maxLifetime, Timeout.InfiniteTimeSpan);

        try
        {
            window.Run(Title, InitialText, ShowDelayMilliseconds, () =>
            {
                var output = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII) { AutoFlush = true };
                output.WriteLine("READY");

                // Reading a redirected console stream can block before its first await.
                new Thread(() => ReadCommands(window, () => lifetime.Change(maxLifetime, Timeout.InfiniteTimeSpan)))
                {
                    IsBackground = true
                }.Start();
            });
        }
        catch (Exception)
        {
            // Missing native UI libraries (e.g. no GTK 3); the host continues without a window.
            return 1;
        }

        // A timer nothing references can be collected before it fires.
        GC.KeepAlive(lifetime);
        return 0;
    }

    /// <summary>Returns the <paramref name="count"/> values after <paramref name="name"/>, or null if absent.</summary>
    private static string[]? Option(string[] args, string name, int count)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + count < args.Length ? args[(index + 1)..(index + 1 + count)] : null;
    }

    private static EditorCenter? ParseEditorCenter(string[] args)
    {
        return Option(args, "--editor-center", 2) is [var x, var y] &&
               double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedX) &&
               double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedY)
            ? new EditorCenter(parsedX, parsedY)
            : null;
    }

    private static void ReadCommands(IProgressWindow window, Action heartbeat)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.ASCII);
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line == "CLOSE")
                {
                    break;
                }

                if (line == "HEARTBEAT")
                {
                    heartbeat();
                    continue;
                }

                if (line.StartsWith("TEXT ", StringComparison.Ordinal))
                {
                    try
                    {
                        window.SetText(Encoding.UTF8.GetString(Convert.FromBase64String(line[5..])));
                    }
                    catch (FormatException)
                    {
                        // Ignore malformed optional text updates.
                    }
                }
            }
        }
        catch (IOException)
        {
            // The parent process closed the pipe.
        }

        // The native UI loops own the main thread; exiting from here avoids per-platform shutdown plumbing.
        Environment.Exit(0);
    }
}

/// <summary>The editor window's center in the platform's native desktop coordinates.</summary>
internal readonly record struct EditorCenter(double X, double Y);

internal interface IProgressWindow
{
    /// <summary>
    /// Creates the window hidden and runs the native UI loop on the calling thread until the window is closed.
    /// <paramref name="ready"/> runs on the UI thread once the window exists. The window is shown without taking
    /// focus from the editor after <paramref name="showDelayMilliseconds"/>.
    /// </summary>
    void Run(string title, string text, int showDelayMilliseconds, Action ready);

    /// <summary>Replaces the label text. Callable from any thread once <c>ready</c> has run.</summary>
    void SetText(string text);
}
