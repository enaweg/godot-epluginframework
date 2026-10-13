using System.Globalization;
using System.Text;

namespace ActivationProgress;

/// <summary>
/// Usage: <c>dotnet ActivationProgress.dll [--editor-window HWND | --editor-center X Y] [--max-lifetime-seconds N]</c>.
/// <c>--editor-window</c> (Windows) and <c>--editor-center</c> (native desktop coordinates, macOS and Linux) place the
/// window over the editor instead of on the primary screen.
/// Commands on stdin, one per line: <c>TEXT base64-utf8</c> replaces the label, <c>HEARTBEAT</c> restarts the
/// lifetime, and <c>CLOSE</c> (or end of input) closes the window and exits. With <c>--command-file PATH</c>, commands are
/// appended to a file instead; end of file waits for more commands across editor restarts, <c>CLOSE</c> only hides the
/// window and <c>TEXT</c> shows it again, so the next operation reuses it. See <see cref="Commands"/> for when that helper
/// exits.
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
        var commandFile = Option(args, "--command-file", 1)?[0];
        // Not disposed: the reader thread may still restart it while the process exits.
        var lifetime = new Timer(_ => Environment.Exit(0), null, maxLifetime, Timeout.InfiniteTimeSpan);
        var commands = new Commands(window, () => lifetime.Change(maxLifetime, Timeout.InfiniteTimeSpan), commandFile);

        try
        {
            window.Run(Title, InitialText, ShowDelayMilliseconds, () =>
            {
                if (commandFile is null)
                {
                    var output = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII) { AutoFlush = true };
                    output.WriteLine("READY");
                }

                // Reading a redirected console stream can block before its first await.
                new Thread(commands.Read) { IsBackground = true }.Start();
            }, commands.Hidden);
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
}

/// <summary>
/// Applies the host's commands to the window and decides when the helper exits.
/// </summary>
/// <remarks>
/// With a command file the editor keeps a state file next to it, named after the same token: <c>.owned</c> while an
/// editor uses the window, <c>.idle</c> while none does. Once <c>CLOSE</c> has hidden the window, the helper waits
/// <see cref="LingerMilliseconds"/> and then claims <c>.idle</c> by renaming it to <c>.exiting</c> before it exits. An
/// editor that wants to reuse the window renames <c>.idle</c> to <c>.owned</c> instead; whichever rename succeeds wins,
/// so an editor never writes to a helper that is about to exit, and a helper never exits under an editor that uses it.
/// Without either file (an older editor, or a test) the helper exits once the window is hidden.
/// </remarks>
internal sealed class Commands(IProgressWindow window, Action heartbeat, string? commandFile)
{
    private const int LingerMilliseconds = 1000;

    // When the window was hidden after the last CLOSE; -1 while it is wanted. Written on the UI thread.
    private long _hiddenAt = -1;
    private bool _closed;
    private long _nextAttempt;

    /// <summary>Called on the UI thread whenever <see cref="IProgressWindow.Close"/> has hidden the window.</summary>
    public void Hidden()
    {
        if (commandFile is null)
        {
            Environment.Exit(0);
        }

        Volatile.Write(ref _hiddenAt, Environment.TickCount64);
    }

    public void Read()
    {
        try
        {
            using var input = new StreamReader(commandFile is null ? Console.OpenStandardInput() :
                new FileStream(commandFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.ASCII);
            var pending = new StringBuilder();
            string? line;
            while ((line = commandFile is null ? input.ReadLine() : ReadAppendedLine(input, pending)) is not null)
            {
                if (line == "CLOSE")
                {
                    if (commandFile is null)
                    {
                        break;
                    }

                    _closed = true;
                    Volatile.Write(ref _hiddenAt, -1);
                    window.Close();
                    continue;
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

                    // Every operation starts with its text, also one that reuses a closed window.
                    _closed = false;
                    window.Open();
                }
            }
        }
        catch (Exception)
        {
            // The parent process closed the pipe, or deleted the command file before the helper could open it.
        }

        // The window exits the process once its closing animation has played. The native UI loops own the main
        // thread; exiting from here too avoids per-platform shutdown plumbing should a window never get there.
        window.Close();
        Thread.Sleep(TimeSpan.FromSeconds(Animation.TotalSeconds + 1));
        Environment.Exit(0);
    }

    private string ReadAppendedLine(StreamReader input, StringBuilder pending)
    {
        // Keep incomplete lines until their newline is written. EOF is temporary while the editor restarts.
        while (true)
        {
            var character = input.Read();
            if (character < 0)
            {
                if (pending.Length == 0) ExitIfUnused();
                Thread.Sleep(50);
                continue;
            }

            if (character == '\n')
            {
                var line = pending.ToString().TrimEnd('\r');
                pending.Clear();
                return line;
            }
            pending.Append((char)character);
        }
    }

    private void ExitIfUnused()
    {
        var hiddenAt = Volatile.Read(ref _hiddenAt);
        var now = Environment.TickCount64;
        if (!_closed || hiddenAt < 0 || now - hiddenAt < LingerMilliseconds || now < _nextAttempt) return;
        // An editor that owns the window will write to it; try again later in case it releases it without a command.
        _nextAttempt = now + LingerMilliseconds;
        var exiting = Path.ChangeExtension(commandFile!, ".exiting");
        try
        {
            File.Move(Path.ChangeExtension(commandFile!, ".idle"), exiting);
        }
        catch (Exception)
        {
            if (File.Exists(Path.ChangeExtension(commandFile!, ".owned"))) return;
        }

        try
        {
            File.Delete(exiting);
            File.Delete(commandFile!);
        }
        catch (Exception)
        {
            // The editor discards the files of a helper that is no longer running.
        }

        Environment.Exit(0);
    }
}

/// <summary>The editor window's center in the platform's native desktop coordinates.</summary>
internal readonly record struct EditorCenter(double X, double Y);

internal interface IProgressWindow
{
    /// <summary>
    /// Creates the window hidden and runs the native UI loop on the calling thread until the process exits.
    /// <paramref name="ready"/> runs on the UI thread once the window exists. The window is shown without taking
    /// focus from the editor after <paramref name="showDelayMilliseconds"/>. <paramref name="hidden"/> runs on the UI
    /// thread whenever <see cref="Close"/> has hidden the window, at once if it was not shown yet.
    /// </summary>
    void Run(string title, string text, int showDelayMilliseconds, Action ready, Action hidden);

    /// <summary>Replaces the label text. Callable from any thread once <c>ready</c> has run.</summary>
    void SetText(string text);

    /// <summary>
    /// Shows the window again: a closing window plays its opening forwards from wherever it got to, a hidden one
    /// shows after the show delay. Callable from any thread once <c>ready</c> has run.
    /// </summary>
    void Open();

    /// <summary>
    /// Plays the opening backwards from wherever it got to, then hides the window; cancels a show that is still
    /// waiting for its delay. Callable from any thread once <c>ready</c> has run.
    /// </summary>
    void Close();
}
