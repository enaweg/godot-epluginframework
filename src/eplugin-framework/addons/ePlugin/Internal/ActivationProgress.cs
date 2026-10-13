#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Best-effort feedback in a separate process while Godot's synchronous plugin work blocks its UI. Thread-safe: async
/// operations and background threads can report progress too.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="Begin"/> without an enclosing scope starts a user of the shared progress window; scopes opened
/// inside it, also by Godot callbacks and in the async flow that opened it, belong to the same user and change its
/// text. The window shows the most recent text of the users that are still active, so concurrent operations can share
/// it, also with other Godot editors. While a user is active a timer sends a heartbeat every
/// <see cref="HeartbeatInterval"/>, also while Godot's main thread is blocked; the helper closes the window when no
/// user has sent anything for 15 seconds, and exits 15 minutes after the last message.
/// </para>
/// <para>
/// Startup never waits for the helper: commands are queued in a file until it has started. An operation that ends
/// before the helper shows its window never shows one. A scheduled restart holds the window for
/// <see cref="RestartHoldSeconds"/> and hands its user to the next editor (see <see cref="ProgressSession"/>).
/// </para>
/// <para>
/// Helper failures do not fail plugin work. Recovery has at most two connection/send attempts per command, including
/// replaying active users' text. A helper at least 30 seconds old may be replaced immediately; early failures or a
/// failed replacement suppress retries until all current users end and another operation begins.
/// </para>
/// </remarks>
internal static class ActivationProgress
{
    private const string HelperPath = "res://addons/ePlugin/progress/ActivationProgress.dll";
    internal const string HandoffPath = "res://.godot/eplugin/progress/handoff";
    internal const int RestartHoldSeconds = 300;
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    // A helper that exits sooner after it started cannot run here (e.g. no GTK 3); it is started again only by the next
    // operation. One that ran longer, e.g. one that exited after its lifetime just as this editor connected, is replaced.
    private static readonly TimeSpan ReplaceAfter = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static readonly AsyncLocal<User?> Current = new();
    private static readonly List<User> Users = [];
    private static IProgressHelper? _helper;
    private static bool _mayStart;
    private static IDisposable? _heartbeat;
    private static User? _resumed;
    private static bool _restartPending;
    private static IDisposable? _settingsDialog;
    private static bool _settingsDialogRequested;

    /// <summary>Connects to or starts a helper, or returns null when none can run. Tests replace it with a fake.</summary>
    internal static Func<IProgressHelper?> Launch { get; set; } = LaunchDefault;
    internal static Func<(IProgressHelper Helper, string User)?> Resume { get; set; } = ResumeDefault;
    internal static Func<IDisposable?> HideSettingsDialog { get; set; } = ProjectSettingsDialog.TryHide;
    internal static Func<bool> IsEditorThread { get; set; } = () => OS.GetThreadCallerId() == OS.GetMainThreadId();

    /// <summary>Starts the timer that calls <see cref="SendHeartbeats"/>. Tests replace it and call that themselves.</summary>
    internal static Func<Action, IDisposable> StartHeartbeat { get; set; } =
        tick => new System.Threading.Timer(_ => tick(), null, HeartbeatInterval, HeartbeatInterval);

    internal static IProgressHelper? LaunchDefault() => ProcessProgressHelper.TryStart(HelperPath);

    internal static (IProgressHelper Helper, string User)? ResumeDefault() =>
        ProcessProgressHelper.SharedDirectory() is { } directory &&
        ProgressSession.TryResume(ProjectSettings.GlobalizePath(HandoffPath), directory) is (var session, var user)
            ? (session, user)
            : null;

    static ActivationProgress()
    {
        // A running timer keeps the collectible assembly loaded, so it stops when the assembly is reloaded. Users of
        // the unloaded assembly stop sending, and the helper closes their window after its timeout. A scope that spans
        // frames on the editor thread leaves its user in that thread's execution context, which would keep it loaded too.
        var context = AssemblyLoadContext.GetLoadContext(typeof(ActivationProgress).Assembly);
        if (context is not null)
        {
            context.Unloading += _ =>
            {
                StopHeartbeat();
                Current.Value = null;
            };
        }
    }

    /// <summary>
    /// Called once at editor startup, before recovery and recipe reconciliation. Returns whether a restart handed over
    /// its window; the next <see cref="Begin"/> then continues that user.
    /// </summary>
    internal static bool ResumeAfterRestart()
    {
        lock (Gate)
        {
            _restartPending = false;
            if (_helper is not null || Users.Count > 0) return false;
            (IProgressHelper Helper, string User)? resumed;
            try { resumed = Resume(); }
            catch (Exception) { resumed = null; }
            if (resumed is not (var helper, var user)) return false;
            _helper = helper;
            _resumed = new User(user);
            // Ends the hold: from now on the window closes when this user stops sending.
            Send(_resumed, "HEARTBEAT");
            if (_helper is not null) return true;
            _resumed = null;
            return false;
        }
    }

    /// <summary>
    /// Holds the window of the current user while the editor restarts and hands the user over to the next editor
    /// process. Until then no new operation shows a window, and the held user sends nothing more.
    /// </summary>
    internal static void PrepareForRestart()
    {
        lock (Gate)
        {
            _restartPending = true;
            var user = Current.Value is { Ended: false, Held: false } current ? current : Users.LastOrDefault(u => !u.Held);
            if (user is null) return;
            Text(user, "Restarting the editor...");
            Send(user, $"HOLD {RestartHoldSeconds}");
            if (_helper is null) return;
            try
            {
                _helper.HandOver(user.Id);
                user.Held = true;
            }
            catch (Exception)
            {
                Lost();
            }
        }
    }

    public static Scope Begin(string text, bool hideSettingsDialog = true)
    {
        var previous = Current.Value;
        User? user;
        lock (Gate)
        {
            user = previous is { Ended: false } ? previous : null;
            if (user is not null || !_restartPending)
            {
                if (hideSettingsDialog) HideSettingsOnce();
                if (user is null)
                {
                    user = _resumed ?? new User(Guid.NewGuid().ToString("N"));
                    _resumed = null;
                    if (Users.Count == 0) First();
                    Users.Add(user);
                }

                user.Depth++;
            }
        }

        var scope = new Scope(user, previous);
        if (user is not null) Current.Value = user;
        scope.SetText(text);
        return scope;
    }

    /// <summary>Sets the text of the current scope's user; does nothing outside a scope.</summary>
    public static void SetText(string text)
    {
        lock (Gate) Text(Current.Value, text);
    }

    /// <summary>Sends a heartbeat for every active user. Called by the timer, from a thread pool thread.</summary>
    internal static void SendHeartbeats()
    {
        lock (Gate)
        {
            foreach (var user in Users.ToArray()) Send(user, "HEARTBEAT");
        }
    }

    /// <summary>Forgets every user and the helper. For tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            StopHeartbeat();
            try { _helper?.Dispose(); }
            catch (Exception) { /* feedback cleanup must not fail */ }
            _helper = null;
            Users.Clear();
            _resumed = null;
            _restartPending = false;
            _settingsDialog = null;
            _settingsDialogRequested = false;
            Current.Value = null;
        }
    }

    private static void HideSettingsOnce()
    {
        if (_settingsDialogRequested) return;
        _settingsDialogRequested = true;
        try
        {
            // Project Settings is editor UI; an operation started on another thread leaves it alone.
            if (IsEditorThread()) _settingsDialog = HideSettingsDialog();
        }
        catch (Exception)
        {
            // Editor UI feedback must not prevent plugin operations.
            _settingsDialog = null;
        }
    }

    private static void First()
    {
        _mayStart = true;
        Connect();
        try { _heartbeat ??= StartHeartbeat(SendHeartbeats); }
        catch (Exception) { _heartbeat = null; }
    }

    private static void Last()
    {
        StopHeartbeat();
        try { _helper?.Dispose(); }
        catch (Exception) { /* the helper keeps running for other editors */ }
        _helper = null;
        var settingsDialog = _settingsDialog;
        _settingsDialog = null;
        _settingsDialogRequested = false;
        if (settingsDialog is null) return;
        try
        {
            if (IsEditorThread()) settingsDialog.Dispose();
            else EGlobal.Post(settingsDialog.Dispose);
        }
        catch (Exception)
        {
            // Restoring editor UI must not change the plugin operation's result.
        }
    }

    private static void StopHeartbeat()
    {
        lock (Gate)
        {
            try { _heartbeat?.Dispose(); }
            catch (Exception) { /* already stopped */ }
            _heartbeat = null;
        }
    }

    /// <summary>Connects when there is no helper and one may be started; then repeats every user's text.</summary>
    private static void Connect()
    {
        if (_helper is not null || !_mayStart) return;
        _mayStart = false;
        try { _helper = Launch(); }
        catch (Exception) { _helper = null; }
        try
        {
            foreach (var user in Users)
            {
                if (!user.Held && !user.Ended && user.Text is { } text) _helper?.Send(user.Id, Encode(text));
            }
        }
        catch (Exception)
        {
            // Replay must not call Send: another failed connection would recursively replay the same users.
            Lost();
        }
    }

    private static void Text(User? user, string text)
    {
        if (user is null || user.Ended) return;
        user.Text = text;
        Send(user, Encode(text));
    }

    private static string Encode(string text) => "TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static void Send(User user, string command)
    {
        if (user.Held) return;
        // A helper that may be replaced is replaced right away, and the command goes to the new one.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Connect();
            if (_helper is null)
            {
                if (!_mayStart) return;
                continue;
            }
            try
            {
                _helper.Send(user.Id, command);
                return;
            }
            catch (Exception)
            {
                Lost();
            }
        }

        // A replacement that also fails must not keep reconnecting on every heartbeat. The next operation retries.
        _mayStart = false;
    }

    private static void Lost()
    {
        var helper = _helper;
        _helper = null;
        _mayStart = false;
        try { _mayStart = helper?.Age >= ReplaceAfter; }
        catch (Exception) { /* an unavailable process cannot be checked */ }
        try
        {
            helper?.Dispose();
        }
        catch (Exception)
        {
            // Feedback cleanup must not change the plugin operation's result.
        }
    }

    private static void End(User user, User? previous)
    {
        if (Current.Value == user) Current.Value = previous;
        lock (Gate)
        {
            if (--user.Depth > 0) return;
            Send(user, "END");
            user.Ended = true;
            Users.Remove(user);
            if (Users.Count == 0) Last();
        }
    }

    /// <summary>One operation using the window: its id, text and nesting depth. Guarded by <see cref="Gate"/>.</summary>
    internal sealed class User(string id)
    {
        public string Id { get; } = id;
        public int Depth { get; set; }
        public string? Text { get; set; }
        public bool Ended { get; set; }

        /// <summary>Handed over to the next editor process: sends nothing, so the helper keeps its hold.</summary>
        public bool Held { get; set; }
    }

    /// <summary>A progress scope; disposing the outermost scope of a user ends it.</summary>
    public sealed class Scope : IEPluginProgressScope
    {
        private readonly User? _user;
        private readonly User? _previous;
        private int _disposed;

        internal Scope(User? user, User? previous)
        {
            _user = user;
            _previous = previous;
        }

        /// <summary>Sets this scope's text from any thread; ignored after disposal or once its user has ended.</summary>
        public void SetText(string text)
        {
            lock (Gate)
            {
                if (_disposed == 0) Text(_user, text);
            }
        }

        void IProgress<string>.Report(string value) => SetText(value);

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed != 0) return;
                _disposed = 1;
                if (_user is not null) End(_user, _previous);
            }
        }
    }
}

/// <summary>A running progress helper that this editor writes to.</summary>
internal interface IProgressHelper : IDisposable
{
    /// <summary>How long the helper process has been running.</summary>
    TimeSpan Age { get; }

    /// <summary>Sends one protocol command of a user. Throws when the helper is unavailable or the send fails.</summary>
    void Send(string user, string command);

    /// <summary>Records the user so that the editor a restart starts can continue it.</summary>
    void HandOver(string user);
}

/// <summary>The published helper, run as <c>dotnet ActivationProgress.dll</c>.</summary>
internal static class ProcessProgressHelper
{
    private static string? _directory;
    private static string? _handoff;

    /// <summary>
    /// Connects to the helper all editors share, or starts it. Godot's display server and editor paths are only used on
    /// the editor thread: another thread connects to a running helper, if the editor thread already found where.
    /// </summary>
    public static IProgressHelper? TryStart(string resourcePath)
    {
        var editorThread = ActivationProgress.IsEditorThread();
        if (editorThread && !HasDesktop())
        {
            return null;
        }

        var path = ProjectSettings.GlobalizePath(resourcePath);
        if (!File.Exists(path) || SharedDirectory() is not { } directory)
        {
            return null;
        }

        var handoff = _handoff ??= ProjectSettings.GlobalizePath(ActivationProgress.HandoffPath);
        return ProgressSession.Acquire(directory, handoff,
            () => editorThread ? ProgressSession.Start(path, directory, handoff, PlacementArguments()) : null);
    }

    /// <summary>
    /// Godot's per-user editor data directory, shared by all editors of the OS user (unless self-contained). Null when
    /// first asked outside the editor thread.
    /// </summary>
    internal static string? SharedDirectory()
    {
        if (_directory is null && ActivationProgress.IsEditorThread())
        {
            _directory = Path.Combine(EditorInterface.Singleton.GetEditorPaths().GetDataDir(), "eplugin", "progress");
        }

        return _directory;
    }

    private static bool HasDesktop()
    {
        // e.g. `godot --headless --editor` for imports or exports on a desktop machine.
        if (DisplayServer.GetName() == "headless")
        {
            return false;
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return true;
        }

        return OperatingSystem.IsLinux() &&
               (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")) ||
                !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }

    /// <summary>
    /// Tells the helper where the editor is, so its window appears over the editor instead of on the primary screen.
    /// Without these arguments the helper centers on the primary screen.
    /// </summary>
    private static IEnumerable<string> PlacementArguments()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The helper reads the rectangle itself, in its own DPI awareness, rather than Godot's physical pixels.
                var handle = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
                return handle == 0 ? [] : ["--editor-window", handle.ToString(CultureInfo.InvariantCulture)];
            }

            var center = DisplayServer.WindowGetPosition() + DisplayServer.WindowGetSize() / 2;
            double x = center.X;
            double y = center.Y;
            if (OperatingSystem.IsMacOS())
            {
                // Godot's macOS coordinates are AppKit points scaled by the maximum screen scale, with a top-left
                // origin. AppKit puts the origin at the bottom-left of the primary screen, which is Godot's screen 0.
                var scale = DisplayServer.ScreenGetMaxScale();
                var primary = DisplayServer.ScreenGetPosition(0);
                var primarySize = DisplayServer.ScreenGetSize(0);
                x = (center.X - primary.X) / scale;
                y = (primary.Y + primarySize.Y - center.Y) / scale;
            }

            return
            [
                "--editor-center", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
#endif
