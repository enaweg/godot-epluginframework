#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This editor's connection to the progress helper that all Godot editors of the OS user share, whatever project they
/// have open. Editors serialize appends to the helper's command file with a named mutex; see the helper's
/// <c>Commands</c> for the protocol.
/// </summary>
/// <remarks>
/// <para>
/// A running helper is published in the shared directory as <c>&lt;token&gt;.helper</c> (protocol, process id, start
/// time) next to its <c>&lt;token&gt;.commands</c>. <see cref="Acquire"/> connects to the oldest running helper that
/// speaks this <see cref="Protocol"/>, and <see cref="Start"/> falls back to it when two editors started one at the
/// same time, so compatible editors converge on one helper. Missing or inaccessible command files are skipped.
/// Records of helpers that no longer run are discarded.
/// </para>
/// <para>
/// Protocol 3 writers acquire <c>eplugin-progress-&lt;token&gt;</c> with a 100 ms wait limit, seek to the file's end,
/// and write each complete command without buffering before releasing the mutex. Earlier writers use a separate
/// helper because their persistent append streams can overwrite other editors' commands.
/// </para>
/// <para>
/// A scheduled restart writes a handoff file into the project (<see cref="HandOver"/>), which the restarted editor
/// reads with <see cref="TryResume"/> to continue as the same user.
/// </para>
/// </remarks>
internal sealed class ProgressSession : IProgressHelper
{
    /// <summary>Raised whenever the transport or command protocol changes; editors only share a compatible helper.</summary>
    // Version 3 serializes append positions with a named mutex. Older writers would overwrite these commands.
    internal const int Protocol = 3;

    private const string Record = ".helper";
    private const string Commands = ".commands";

    /// <summary>Whether a process still runs. Tests replace it to simulate helpers.</summary>
    internal static Func<ProcessIdentity, bool> IsRunning { get; set; } = ProcessIdentity.IsRunning;

    private readonly string _token;
    private readonly ProcessIdentity _helper;
    private readonly string _handoff;
    private readonly FileStream _commands;
    private readonly Mutex _writeMutex;

    private ProgressSession(string token, ProcessIdentity helper, string handoff, FileStream commands)
    {
        _token = token;
        _helper = helper;
        _handoff = handoff;
        _commands = commands;
        try { _writeMutex = new Mutex(false, "eplugin-progress-" + token); }
        catch
        {
            commands.Dispose();
            throw;
        }
    }

    public TimeSpan Age => DateTime.UtcNow - new DateTime(_helper.StartUtcTicks, DateTimeKind.Utc);

    private static string PathOf(string directory, string token, string extension) => Path.Combine(directory, token + extension);

    private static FileStream OpenCommands(string path, bool create = false)
    {
        var stream = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    /// <summary>Connects to the shared helper, or calls <paramref name="start"/> when none runs.</summary>
    public static IProgressHelper? Acquire(string directory, string handoff, Func<IProgressHelper?> start)
    {
        Directory.CreateDirectory(directory);
        foreach (var (token, helper) in Running(directory))
        {
            if (Connect(directory, token, helper, handoff) is { } session) return session;
        }

        return start();
    }

    public static ProgressSession? Start(string helperPath, string directory, string handoff, IEnumerable<string> arguments)
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
        var commandFile = PathOf(directory, token, Commands);
        var commands = OpenCommands(commandFile, create: true);
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                // The helper outlives the editor by up to its lifetime. Inheriting the editor's standard streams would
                // keep whatever reads the editor's output (a terminal pipeline, a CI step) waiting for it that long.
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(Path.Combine(runtime, Path.GetFileName(helperPath)));
            startInfo.ArgumentList.Add("--command-file");
            startInfo.ArgumentList.Add(commandFile);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            process = Process.Start(startInfo);
            if (process is null)
            {
                commands.Dispose();
                Discard(directory, token);
                return null;
            }

            var helper = ProcessIdentity.Of(process);
            Write(PathOf(directory, token, Record), [Protocol.ToString(CultureInfo.InvariantCulture), .. helper.Fields]);
            // Another editor may have started one at the same time: everybody uses the oldest, and the other one exits
            // before anybody wrote to it.
            if (Running(directory).FirstOrDefault() is var (oldest, older) && oldest != token &&
                Connect(directory, oldest, older, handoff) is { } shared)
            {
                commands.Dispose();
                process.Kill();
                Discard(directory, token);
                return shared;
            }

            return new(token, helper, handoff, commands);
        }
        catch
        {
            commands.Dispose();
            try { if (process is { HasExited: false }) process.Kill(); }
            catch (Exception) { /* the helper exits on its own after its lifetime */ }
            Discard(directory, token);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Called once when the editor starts: continues the user whose window the editor before its restart handed over.
    /// The handoff is read only once.
    /// </summary>
    public static (ProgressSession Session, string User)? TryResume(string handoff, string directory)
    {
        try
        {
            CloseEarlierVersion(Path.GetDirectoryName(handoff)!);
            if (!File.Exists(handoff)) return null;
            string[] fields;
            try { fields = File.ReadAllLines(handoff); }
            finally { File.Delete(handoff); }
            if (fields.Length != 5 || fields[0] != Protocol.ToString(CultureInfo.InvariantCulture) ||
                !Guid.TryParseExact(fields[1], "N", out _) || !ProcessIdentity.TryParse(fields[2], fields[3], out var helper) ||
                !Guid.TryParseExact(fields[4], "N", out _) || !IsRunning(helper)) return null;
            return Connect(directory, fields[1], helper, handoff) is { } session ? (session, fields[4]) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Earlier framework versions kept a per-project helper and wrote a <c>restart</c> marker (token, process id, start
    /// time) next to its command file to hand it over. That helper does not understand users, so it is closed.
    /// </summary>
    private static void CloseEarlierVersion(string projectDirectory)
    {
        var marker = Path.Combine(projectDirectory, "restart");
        if (!File.Exists(marker)) return;
        try
        {
            var fields = File.ReadAllLines(marker);
            var commandFile = fields.Length == 3 && Guid.TryParseExact(fields[0], "N", out _)
                ? PathOf(projectDirectory, fields[0], Commands)
                : null;
            if (commandFile is not null && File.Exists(commandFile))
            {
                using var commands = OpenCommands(commandFile);
                commands.Write(Encoding.ASCII.GetBytes("CLOSE\n"));
            }
        }
        finally { File.Delete(marker); }
    }

    /// <summary>The running helpers of this protocol, oldest first; discards the records of helpers that exited.</summary>
    private static List<(string Token, ProcessIdentity Helper)> Running(string directory)
    {
        var running = new List<(string Token, ProcessIdentity Helper)>();
        foreach (var path in Directory.GetFiles(directory, "*" + Record))
        {
            var token = Path.GetFileNameWithoutExtension(path);
            string[] fields;
            try { fields = File.ReadAllLines(path); }
            catch (Exception) { continue; }
            if (fields.Length != 3 || !ProcessIdentity.TryParse(fields[1], fields[2], out var helper)) continue;
            if (!IsRunning(helper)) Discard(directory, token);
            else if (fields[0] == Protocol.ToString(CultureInfo.InvariantCulture)) running.Add((token, helper));
        }

        return running.OrderBy(h => h.Helper.StartUtcTicks).ThenBy(h => h.Token, StringComparer.Ordinal).ToList();
    }

    private static ProgressSession? Connect(string directory, string token, ProcessIdentity helper, string handoff)
    {
        var commandFile = PathOf(directory, token, Commands);
        try { return new ProgressSession(token, helper, handoff, OpenCommands(commandFile)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Discard(string directory, string token)
    {
        foreach (var extension in new[] { Record, Commands, ".exiting" })
        {
            try { File.Delete(PathOf(directory, token, extension)); }
            catch (Exception) { /* discarded again later */ }
        }
    }

    /// <summary>Replaces the file in one step, so nobody reads it half-written.</summary>
    private static void Write(string path, string[] lines)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }

    public void Send(string user, string command)
    {
        // FileMode.Append only seeks to the end when opened. Each editor's persistent stream otherwise overwrites
        // commands written by the others. Bound the wait so feedback cannot stall a plugin operation.
        try
        {
            if (!_writeMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                throw new IOException("The progress command file is busy.");
        }
        catch (AbandonedMutexException)
        {
            // The writer exited while holding it; this thread now owns it.
        }

        try
        {
            if (!IsRunning(_helper)) throw new IOException("The progress helper exited.");
            _commands.Seek(0, SeekOrigin.End);
            _commands.Write(Encoding.ASCII.GetBytes(user + " " + command + "\n"));
        }
        finally { _writeMutex.ReleaseMutex(); }
    }

    public void HandOver(string user)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_handoff)!);
        Write(_handoff, [Protocol.ToString(CultureInfo.InvariantCulture), _token, .. _helper.Fields, user]);
    }

    /// <summary>Releases the command file; the helper keeps running for other users.</summary>
    public void Dispose()
    {
        try { _commands.Dispose(); }
        finally { _writeMutex.Dispose(); }
    }
}

/// <summary>A process, identified by its id and start time so that a reused id is not mistaken for it.</summary>
internal readonly record struct ProcessIdentity(int Id, long StartUtcTicks)
{
    public string[] Fields =>
        [Id.ToString(CultureInfo.InvariantCulture), StartUtcTicks.ToString(CultureInfo.InvariantCulture)];

    public static ProcessIdentity Of(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks);

    public static bool TryParse(string id, string ticks, out ProcessIdentity identity)
    {
        identity = default;
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) ||
            !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTicks)) return false;
        identity = new ProcessIdentity(parsedId, parsedTicks);
        return true;
    }

    /// <summary>
    /// Linux derives the boot time separately in each host, so a start time can differ by a fraction of a second
    /// across processes.
    /// </summary>
    public bool Matches(ProcessIdentity other) =>
        Id == other.Id && Math.Abs(StartUtcTicks - other.StartUtcTicks) <= TimeSpan.TicksPerSecond;

    public static bool IsRunning(ProcessIdentity identity)
    {
        if (identity.Id <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            return !process.HasExited && identity.Matches(Of(process));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
#endif
