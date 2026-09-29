#if TOOLS
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Best-effort feedback in a separate process while Godot's synchronous plugin work blocks its UI.
/// Nested editor callbacks share the same process.
/// </summary>
internal static class ActivationProgress
{
    // A cold .NET start of the helper can take a few seconds on slow machines.
    private const int StartupTimeoutMilliseconds = 10000;
    private const int ExitTimeoutMilliseconds = 500;
    private static int _depth;
    private static Process? _process;

    public static IDisposable Begin(string text)
    {
        if (_depth++ == 0)
        {
            Start();
        }

        SetText(text);
        return new Scope();
    }

    public static void SetText(string text)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            _process.StandardInput.WriteLine($"TEXT {encoded}");
        }
        catch (Exception)
        {
            Stop();
        }
    }

    private static void Start()
    {
        if (!HasDesktop())
        {
            return;
        }

        try
        {
            var path = ProjectSettings.GlobalizePath("res://addons/ePlugin/progress/ActivationProgress.dll");
            if (!File.Exists(path))
            {
                return;
            }

            // The helper is framework-dependent, so it runs on the same dotnet host the recipes use.
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                // dotnet is a console application; don't flash a console window next to the progress window.
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = Encoding.ASCII,
                StandardOutputEncoding = Encoding.ASCII
            };
            startInfo.ArgumentList.Add(path);
            var process = Process.Start(startInfo);

            if (process is null)
            {
                return;
            }

            _process = process;
            process.StandardInput.AutoFlush = true;
            var ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(StartupTimeoutMilliseconds) || ready.Result != "READY")
            {
                Stop();
            }
        }
        catch (Exception)
        {
            Stop();
        }
    }

    private static bool HasDesktop()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return true;
        }

        return OperatingSystem.IsLinux() &&
               (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")) ||
                !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }

    private static void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine("CLOSE");
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // The helper may already have closed its input pipe.
                }

                if (!process.WaitForExit(ExitTimeoutMilliseconds))
                {
                    process.Kill();
                }
            }
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // A failed helper must not affect activation or deactivation.
            }
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch (Exception)
            {
                // Feedback cleanup must not change the plugin operation's result.
            }
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
            }
        }
    }
}
#endif
