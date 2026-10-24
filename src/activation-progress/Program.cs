using System.Globalization;
using System.Text;

namespace ActivationProgress;

/// <summary>
/// Usage: <c>dotnet ActivationProgress.dll [--command-file PATH] [--editor-window HWND | --editor-center X Y]
/// [--max-lifetime-seconds N] [--window-timeout-seconds N]</c>.
/// <c>--editor-window</c> (Windows) and <c>--editor-center</c> (native desktop coordinates, macOS and Linux) place the
/// window over the editor instead of on the primary screen.
/// Commands arrive one per line, on stdin or, with <c>--command-file</c>, appended to a file that several editor
/// processes and threads share. See <see cref="Commands"/> for the protocol. With stdin, <c>CLOSE</c> (or end of input)
/// also closes the window and exits.
/// </summary>
internal static class Program
{
    private const string Title = "ePlugin Framework";
    private const string InitialText = "Working...";

    // Operations that finish sooner never show the window.
    private const int ShowDelayMilliseconds = 300;

    // The helper exits this long after the last message of any user.
    private const int DefaultMaxLifetimeSeconds = 15 * 60;

    // The window has no close button: it closes once no user has sent anything for this long.
    private const int DefaultWindowTimeoutSeconds = 15;

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

        var commandFile = Option(args, "--command-file", 1)?[0];
        var commands = new Commands(window, commandFile, InitialText,
            TimeSpan.FromSeconds(Seconds(args, "--window-timeout-seconds", DefaultWindowTimeoutSeconds)),
            TimeSpan.FromSeconds(Seconds(args, "--max-lifetime-seconds", DefaultMaxLifetimeSeconds)));

        try
        {
            window.Run(Title, InitialText, ShowDelayMilliseconds, () =>
            {
                if (commandFile is null)
                {
                    var output = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII) { AutoFlush = true };
                    output.WriteLine("READY");
                }

                commands.Start();
            }, commands.Hidden);
        }
        catch (Exception)
        {
            // Missing native UI libraries (e.g. no GTK 3); the host continues without a window.
            return 1;
        }

        return 0;
    }

    private static int Seconds(string[] args, string name, int fallback)
    {
        return Option(args, name, 1) is [var seconds] &&
               int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
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
/// Applies the commands of every user to the window and decides when the helper exits.
/// </summary>
/// <remarks>
/// <para>
/// A command line is <c>USER COMMAND [ARGUMENT]</c>, where <c>USER</c> is an id without spaces that the sender chooses for
/// each of its operations; a line without one belongs to an anonymous user. <c>TEXT base64-utf8</c> sets the user's
/// text, <c>HEARTBEAT</c> only says it is still working, <c>END</c> (or <c>CLOSE</c>) ends it, and <c>HOLD SECONDS</c>
/// keeps it active that long without messages, e.g. while its editor restarts; its next message restores the window
/// timeout.
/// </para>
/// <para>
/// The window is shown while any user is active, i.e. has not ended and sent something within the window timeout (or
/// its hold), and shows the text of the active user that set one most recently. A user is forgotten once it has been
/// silent for the maximum lifetime, and the helper exits when that applies to all of them.
/// </para>
/// <para>
/// With a command file the editor publishes the helper in a record next to it (<c>.helper</c>, same token), which
/// is how other editors find it. Before exiting, the helper renames the record to <c>.exiting</c>, so no editor
/// connects anymore, and keeps reading for a moment: a message that arrives meanwhile, from an editor that connected
/// just before, restores the record and keeps the helper running.
/// </para>
/// </remarks>
/// <param name="clock">Milliseconds since some start; <see cref="Environment.TickCount64"/> unless a test drives time.</param>
/// <param name="exit">
/// Ends the helper, on the UI thread once the window is hidden; <see cref="Environment.Exit"/> unless a test watches for it.
/// </param>
internal sealed class Commands(IProgressWindow window, string? commandFile, string initialText, TimeSpan windowTimeout,
    TimeSpan lifetime, Func<long>? clock = null, Action? exit = null)
{
    private const int TickMilliseconds = 100;
    private const int ExitDrainMilliseconds = 1000;
    private static readonly string[] Keywords = ["TEXT", "HEARTBEAT", "END", "CLOSE", "HOLD"];

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Action _exit = exit ?? (() => Environment.Exit(0));
    private readonly object _gate = new();
    private readonly Dictionary<string, User> _users = new(StringComparer.Ordinal);
    private long _lastMessage = (clock ?? (() => Environment.TickCount64))();
    private long _sequence;
    private bool _shown;
    private string? _text;
    private long _exitAt = -1;
    private volatile bool _quitting;

    public void Start()
    {
        // Reading a redirected console stream can block before its first await.
        new Thread(Read) { IsBackground = true }.Start();
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(TickMilliseconds);
                lock (_gate) Update();
            }
        }) { IsBackground = true }.Start();
    }

    /// <summary>Called on the UI thread whenever <see cref="IProgressWindow.Close"/> has hidden the window.</summary>
    public void Hidden()
    {
        if (_quitting)
        {
            _exit();
        }
    }

    private void Read()
    {
        try
        {
            using var input = new StreamReader(commandFile is null ? Console.OpenStandardInput() :
                new FileStream(commandFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.ASCII);
            var pending = new StringBuilder();
            string? line;
            while ((line = commandFile is null ? input.ReadLine() : ReadAppendedLine(input, pending)) is not null)
            {
                if (commandFile is null && line == "CLOSE")
                {
                    break;
                }

                lock (_gate)
                {
                    Apply(line);
                    Update();
                }
            }
        }
        catch (Exception)
        {
            // The parent process closed the pipe, or deleted the command file before the helper could open it.
        }

        lock (_gate) Quit();
    }

    /// <summary>
    /// Closes the window, and exits once its closing animation has played: <see cref="Hidden"/> runs on the UI thread,
    /// so the native UI loop is not calling into managed code while the runtime shuts down. Exiting from another thread
    /// while it ran crashed the helper (SIGSEGV with GTK on .NET 8). The caller holds the lock.
    /// </summary>
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        window.Close();
        if (exit is not null) return;
        // The native UI loops own the main thread; exiting from here too avoids per-platform shutdown plumbing should
        // a window never get there.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(Animation.TotalSeconds + 1));
            Environment.Exit(0);
        }) { IsBackground = true }.Start();
    }

    internal static string ReadAppendedLine(StreamReader input, StringBuilder pending)
    {
        // Keep incomplete lines until their newline is written. EOF is temporary while editors restart.
        while (true)
        {
            var character = input.Read();
            if (character < 0)
            {
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

    /// <summary>Applies one command line; the caller holds the lock.</summary>
    internal void Apply(string line)
    {
        var now = _clock();
        _lastMessage = now;
        if (_exitAt >= 0)
        {
            // An editor connected just before the helper withdrew its record.
            _exitAt = -1;
            try { File.Move(Path.ChangeExtension(commandFile!, ".exiting"), Path.ChangeExtension(commandFile!, ".helper")); }
            catch (Exception) { /* the editor that wrote this finds the helper by its command file */ }
        }

        var parts = line.Split(' ');
        var offset = Keywords.Contains(parts[0]) ? 0 : 1;
        if (parts.Length <= offset) return;
        var key = offset == 0 ? string.Empty : parts[0];
        var argument = parts.Length > offset + 1 ? parts[offset + 1] : null;
        if (!_users.TryGetValue(key, out var user))
        {
            _users[key] = user = new User();
        }

        user.LastMessage = now;
        user.Timeout = (long)windowTimeout.TotalMilliseconds;
        switch (parts[offset])
        {
            case "TEXT" when argument is not null:
                try
                {
                    user.Text = Encoding.UTF8.GetString(Convert.FromBase64String(argument));
                    user.Sequence = ++_sequence;
                    user.Ended = false;
                }
                catch (FormatException)
                {
                    // Ignore malformed optional text updates.
                }

                break;
            case "END" or "CLOSE":
                user.Ended = true;
                break;
            case "HOLD" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds):
                user.Timeout = seconds * 1000L;
                break;
        }
    }

    /// <summary>Shows, updates or closes the window for the active users, and exits once all were silent too long.</summary>
    internal void Update()
    {
        if (_quitting) return;
        var now = _clock();
        var forget = (long)lifetime.TotalMilliseconds;
        User? latest = null;
        var active = false;
        foreach (var (key, user) in _users.ToArray())
        {
            if (now - user.LastMessage >= forget)
            {
                _users.Remove(key);
                continue;
            }

            if (user.Ended || now - user.LastMessage >= user.Timeout) continue;
            active = true;
            if (user.Text is not null && (latest is null || user.Sequence > latest.Sequence)) latest = user;
        }

        if (active)
        {
            var text = latest?.Text ?? initialText;
            if (text != _text)
            {
                _text = text;
                window.SetText(text);
            }

            if (!_shown)
            {
                _shown = true;
                window.Open();
            }
        }
        else if (_shown)
        {
            _shown = false;
            window.Close();
        }

        if (now - _lastMessage < forget) return;
        if (commandFile is null)
        {
            Quit();
            return;
        }

        var exiting = Path.ChangeExtension(commandFile, ".exiting");
        if (_exitAt < 0)
        {
            _exitAt = now + ExitDrainMilliseconds;
            try { File.Move(Path.ChangeExtension(commandFile, ".helper"), exiting); }
            catch (Exception) { /* not published, e.g. in a test */ }
            return;
        }

        if (now < _exitAt) return;
        try
        {
            File.Delete(exiting);
            File.Delete(commandFile);
        }
        catch (Exception)
        {
            // Editors discard the files of a helper that is no longer running.
        }

        Quit();
    }

    private sealed class User
    {
        public long LastMessage;
        public long Timeout;
        public string? Text;
        public long Sequence;
        public bool Ended;
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
