#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Best-effort feedback in a separate process while Godot's synchronous plugin work blocks its UI.
/// Nested editor callbacks share the same process.
/// </summary>
/// <remarks>
/// Startup never waits for the helper: commands are queued in a file until it has started. An operation that
/// ends before the helper shows its window never shows one. A project has at most one helper: the next operation
/// reopens a closing or hidden window, one lost to an assembly reload is taken over, and scheduled restarts hand the
/// window to the next editor (see <see cref="ProgressSession"/>).
/// </remarks>
internal static class ActivationProgress
{
    private const string HelperPath = "res://addons/ePlugin/progress/ActivationProgress.dll";
    internal const string SessionPath = "res://.godot/eplugin/progress";

    // Counts open scopes, not helper lifetimes: a helper that dies mid-operation is intentionally not restarted by
    // the nested scopes of that operation, only by the next top-level one.
    private static int _depth;
    private static IProgressHelper? _helper;
    private static IDisposable? _settingsDialog;
    private static bool _restartPending;

    /// <summary>Reuses or starts a helper, or returns null when none can run. Tests replace it with a fake.</summary>
    internal static Func<IProgressHelper?> Launch { get; set; } = LaunchDefault;
    internal static Func<IProgressHelper?> Resume { get; set; } = ResumeDefault;

    internal static Func<IDisposable?> HideSettingsDialog { get; set; } = ProjectSettingsDialog.TryHide;

    internal static IProgressHelper? LaunchDefault() => ProcessProgressHelper.TryStart(HelperPath);
    internal static IProgressHelper? ResumeDefault() => ProgressSession.TryResume(ProjectSettings.GlobalizePath(SessionPath));

    /// <summary>
    /// Called once at editor startup, before recovery and recipe reconciliation. Returns whether a restart handed over
    /// its window.
    /// </summary>
    internal static bool ResumeAfterRestart()
    {
        _restartPending = false;
        if (_helper is not null) return false;
        try { _helper = Resume(); }
        catch (Exception) { _helper = null; }
        return _helper is not null;
    }

    /// <summary>Hands the running window to the next editor process instead of closing it with this scope.</summary>
    internal static void PrepareForRestart()
    {
        _restartPending = true;
        SetText("Restarting the editor...");
        Heartbeat();
        try
        {
            _helper?.DetachForRestart();
            _helper = null;
        }
        catch (Exception) { Stop(); }
    }

    public static IDisposable Begin(string text)
    {
        if (_depth++ == 0)
        {
            try
            {
                _settingsDialog = HideSettingsDialog();
            }
            catch (Exception)
            {
                // Editor UI feedback must not prevent plugin operations.
                _settingsDialog = null;
            }

            if (_helper is null && !_restartPending) Start();
        }

        SetText(text);
        return new Scope();
    }

    public static void SetText(string text)
    {
        Send($"TEXT {Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}");
    }

    /// <summary>
    /// Restarts the helper's lifetime (15 minutes), which otherwise closes the window on its own. Sent whenever a
    /// plugin starts installing or uninstalling, so a long chain of dependencies keeps its window until done.
    /// </summary>
    public static void Heartbeat()
    {
        Send("HEARTBEAT");
    }

    private static void Send(string command)
    {
        if (_helper is null)
        {
            return;
        }

        try
        {
            _helper.Send(command);
        }
        catch (Exception)
        {
            Stop();
        }
    }

    private static void Start()
    {
        try
        {
            _helper = Launch();
        }
        catch (Exception)
        {
            _helper = null;
        }
    }

    private static void Stop()
    {
        var helper = _helper;
        _helper = null;
        try
        {
            helper?.Dispose();
        }
        catch (Exception)
        {
            // Feedback cleanup must not change the plugin operation's result.
        }
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--_depth == 0)
            {
                Stop();
                var settingsDialog = _settingsDialog;
                _settingsDialog = null;
                try
                {
                    settingsDialog?.Dispose();
                }
                catch (Exception)
                {
                    // Restoring editor UI must not change the plugin operation's result.
                }
            }
        }
    }
}

/// <summary>A running progress helper. Disposing it closes the window and lets the next operation reuse it.</summary>
internal interface IProgressHelper : IDisposable
{
    /// <summary>Sends one protocol line. Throws when the helper is gone.</summary>
    void Send(string command);
    /// <summary>Persists the session and releases this host's handles without closing the window.</summary>
    void DetachForRestart();
}

/// <summary>The published helper, run as <c>dotnet ActivationProgress.dll</c>.</summary>
internal static class ProcessProgressHelper
{
    public static IProgressHelper? TryStart(string resourcePath)
    {
        if (!HasDesktop())
        {
            return null;
        }

        var path = ProjectSettings.GlobalizePath(resourcePath);
        if (!File.Exists(path))
        {
            return null;
        }

        var directory = ProjectSettings.GlobalizePath(ActivationProgress.SessionPath);
        return ProgressSession.Acquire(directory, () => ProgressSession.Start(path, directory, PlacementArguments()));
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
