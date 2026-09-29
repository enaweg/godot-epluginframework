#if TOOLS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Best-effort feedback in a separate process while Godot's synchronous plugin work blocks its UI.
/// Nested editor callbacks share the same process.
/// </summary>
internal static class ActivationProgress
{
    // A cold single-file launch may need to extract native libraries before the window opens.
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
        var runtime = GetRuntime();
        if (runtime is null)
        {
            return;
        }

        try
        {
            var fileName = OperatingSystem.IsWindows() ? "ActivationProgress.exe" : "ActivationProgress";
            var path = ProjectSettings.GlobalizePath($"res://addons/ePlugin/progress/{runtime}/{fileName}");
            if (!File.Exists(path))
            {
                return;
            }

            var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = Encoding.ASCII,
                StandardOutputEncoding = Encoding.ASCII
            });

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

    private static string? GetRuntime()
    {
        if (OperatingSystem.IsLinux() &&
            string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        if (architecture is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"linux-{architecture}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        return null;
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
