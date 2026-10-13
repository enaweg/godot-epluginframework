#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// A file-backed command channel survives editor exits. Only scheduled restarts leave a reconnect marker;
/// ordinary disposal sends CLOSE. The helper's heartbeat timeout bounds abandoned sessions.
/// </summary>
internal sealed class ProgressSession : IProgressHelper
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);
    private readonly Process _process;
    private readonly string _directory;
    private readonly string _token;
    private readonly StreamWriter _commands;
    private readonly long _startedUtcTicks;
    private bool _disposed;
    private string Marker => Path.Combine(_directory, "restart");
    private string CommandFile => Path.Combine(_directory, _token + ".commands");

    private ProgressSession(Process process, string directory, string token, StreamWriter commands, long? startedUtcTicks = null)
    {
        _process = process;
        _directory = directory;
        _token = token;
        _commands = commands;
        _startedUtcTicks = startedUtcTicks ?? process.StartTime.ToUniversalTime().Ticks;
    }

    private static StreamWriter OpenCommands(string path) => new(new FileStream(path, FileMode.Append,
        FileAccess.Write, FileShare.Read | FileShare.Delete), Encoding.ASCII) { AutoFlush = true };

    public static ProgressSession? Start(string helperPath, string directory, IEnumerable<string> placement)
    {
        Directory.CreateDirectory(directory);
        // Windows locks loaded assemblies. Run an immutable copy so self-updates can replace the entire addon.
        var configPath = Path.ChangeExtension(helperPath, ".runtimeconfig.json");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(helperPath));
        hash.AppendData(File.ReadAllBytes(configPath));
        var runtime = Path.Combine(directory, "runtime", Convert.ToHexString(hash.GetHashAndReset()));
        Directory.CreateDirectory(runtime);
        foreach (var source in new[] { helperPath, configPath })
        {
            var target = Path.Combine(runtime, Path.GetFileName(source));
            if (!File.Exists(target)) File.Copy(source, target);
        }

        var token = Guid.NewGuid().ToString("N");
        var commandFile = Path.Combine(directory, token + ".commands");
        var commands = OpenCommands(commandFile);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                // The helper emits READY only for the stdin smoke-test protocol.
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add(Path.Combine(runtime, Path.GetFileName(helperPath)));
            startInfo.ArgumentList.Add("--command-file");
            startInfo.ArgumentList.Add(commandFile);
            foreach (var argument in placement) startInfo.ArgumentList.Add(argument);
            var process = Process.Start(startInfo);
            if (process is not null) return new(process, directory, token, commands);
        }
        catch
        {
            commands.Dispose();
            File.Delete(commandFile);
            throw;
        }
        commands.Dispose();
        File.Delete(commandFile);
        return null;
    }

    public static ProgressSession? TryResume(string directory)
    {
        var marker = Path.Combine(directory, "restart");
        Process? process = null;
        ProgressSession? session = null;
        try
        {
            if (!File.Exists(marker)) return null;
            var fields = File.ReadAllLines(marker);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) >= MaxAge || fields.Length != 3 ||
                !Guid.TryParseExact(fields[0], "N", out _) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start)) return null;
            process = Process.GetProcessById(pid);
            // Linux derives the boot time separately in each host, so StartTime can differ by a fraction of a
            // second across processes. Keep checking the PID's age without requiring identical clock precision.
            if (process.HasExited || Math.Abs(process.StartTime.ToUniversalTime().Ticks - start) > TimeSpan.TicksPerSecond) return null;
            var commandFile = Path.Combine(directory, fields[0] + ".commands");
            if (!File.Exists(commandFile)) return null;
            session = new ProgressSession(process, directory, fields[0], OpenCommands(commandFile), start);
            process = null;
            session.Send("HEARTBEAT");
            return session;
        }
        catch (Exception)
        {
            session?.Dispose();
            return null;
        }
        finally { process?.Dispose(); }
    }

    public void Send(string command)
    {
        if (_process.HasExited) throw new IOException("The progress helper exited.");
        _commands.WriteLine(command);
    }

    public void DetachForRestart()
    {
        File.WriteAllLines(Marker,
        [
            _token, _process.Id.ToString(CultureInfo.InvariantCulture),
            _startedUtcTicks.ToString(CultureInfo.InvariantCulture)
        ]);
        Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            // Not waited for: the helper plays its closing animation (about 2 s) and then exits on its own, also
            // when it reads CLOSE only after the command file is deleted below.
            if (!_process.HasExited) Send("CLOSE");
        }
        catch (Exception)
        {
            try { if (!_process.HasExited) _process.Kill(); }
            catch (Exception) { /* feedback must not affect the operation */ }
        }
        finally
        {
            Release();
            try { File.Delete(CommandFile); File.Delete(Marker); }
            catch (Exception) { /* stale state is ignored on the next startup */ }
        }
    }

    private void Release()
    {
        _disposed = true;
        try { _commands.Dispose(); }
        finally { _process.Dispose(); }
    }
}
#endif
