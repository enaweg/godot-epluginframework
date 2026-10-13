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
/// The editor's side of a progress helper: a command file the helper reads, and a state file that says whether an
/// editor uses its window. Both are named after the helper's token in the progress directory and survive editor exits.
/// </summary>
/// <remarks>
/// <para>
/// The state file is <c>&lt;token&gt;.owned</c> (helper process, owning editor process, restart flag) while an editor
/// uses the window and <c>&lt;token&gt;.idle</c> after it closed it. <see cref="Acquire"/> reuses an existing helper
/// before it starts one, so a project has at most one helper and window:
/// </para>
/// <list type="bullet">
/// <item>An idle helper is claimed by renaming its file back to <c>.owned</c>; its window reopens with the next text.
/// The helper claims <c>.idle</c> itself before it exits, and only one of the two renames can succeed.</item>
/// <item>An owned helper is taken over when its editor is this process (a C# assembly reload lost the reference) or
/// no longer runs. One owned by another running editor is left alone, and this editor shows no window.</item>
/// </list>
/// <para>
/// Only a scheduled restart sets the restart flag; <see cref="TryResume"/> hands that helper to the next editor and
/// closes the window of an editor that ended without one. The helper's heartbeat timeout bounds abandoned sessions.
/// </para>
/// </remarks>
internal sealed class ProgressSession : IProgressHelper
{
    private const string Owned = ".owned";
    private const string Idle = ".idle";
    private const string Commands = ".commands";

    /// <summary>Whether a process still runs. Tests replace it to simulate other editors and helpers.</summary>
    internal static Func<ProcessIdentity, bool> IsRunning { get; set; } = ProcessIdentity.IsRunning;

    /// <summary>This editor process. Tests replace it to simulate another editor.</summary>
    internal static ProcessIdentity Self { get; set; } = ProcessIdentity.Current();

    private readonly string _directory;
    private readonly string _token;
    private readonly ProcessIdentity _helper;
    private readonly StreamWriter _commands;
    private bool _disposed;

    private ProgressSession(string directory, string token, ProcessIdentity helper, StreamWriter commands)
    {
        _directory = directory;
        _token = token;
        _helper = helper;
        _commands = commands;
    }

    private static string PathOf(string directory, string token, string extension) => Path.Combine(directory, token + extension);

    private static StreamWriter OpenCommands(string path) => new(new FileStream(path, FileMode.Append,
        FileAccess.Write, FileShare.Read | FileShare.Delete), Encoding.ASCII) { AutoFlush = true };

    /// <summary>
    /// Reuses the project's helper, or calls <paramref name="start"/> when there is none. Returns null while another
    /// running editor uses the window.
    /// </summary>
    public static IProgressHelper? Acquire(string directory, Func<IProgressHelper?> start)
    {
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*" + Owned))
        {
            var token = Path.GetFileNameWithoutExtension(path);
            if (State.Read(path) is not { } state) continue;
            if (!IsRunning(state.Helper))
            {
                Discard(directory, token);
                continue;
            }

            // Another running editor uses the window, or hands it to the editor it restarts.
            if (!state.Owner.Matches(Self) && IsRunning(state.Owner)) return null;
            if (Adopt(directory, token, state) is { } orphan) return orphan;
        }

        foreach (var path in Directory.GetFiles(directory, "*" + Idle))
        {
            var token = Path.GetFileNameWithoutExtension(path);
            if (State.Read(path) is not { } state) continue;
            if (!IsRunning(state.Helper))
            {
                Discard(directory, token);
                continue;
            }

            try { File.Move(path, PathOf(directory, token, Owned)); }
            catch (Exception) { continue; } // the helper claimed it to exit
            if (Adopt(directory, token, state) is { } idle) return idle;
        }

        return start();
    }

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
        var commandFile = PathOf(directory, token, Commands);
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
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                var helper = ProcessIdentity.Of(process);
                new State(helper, Self, false).Write(PathOf(directory, token, Owned));
                return new(directory, token, helper, commands);
            }
        }
        catch
        {
            commands.Dispose();
            Discard(directory, token);
            throw;
        }
        commands.Dispose();
        Discard(directory, token);
        return null;
    }

    /// <summary>
    /// Called once when the editor starts: takes over the helper a restart handed over, and closes the window of an
    /// editor that ended without handing it over.
    /// </summary>
    public static ProgressSession? TryResume(string directory)
    {
        ProgressSession? resumed = null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            MigrateRestartMarker(directory);
            foreach (var path in Directory.GetFiles(directory, "*" + Owned))
            {
                var token = Path.GetFileNameWithoutExtension(path);
                if (State.Read(path) is not { } state) continue;
                if (!IsRunning(state.Helper)) Discard(directory, token);
                // The editor that restarted may still be shutting down.
                else if (state.Restart && resumed is null) resumed = Adopt(directory, token, state);
                else if (!state.Owner.Matches(Self) && !IsRunning(state.Owner)) Adopt(directory, token, state)?.Dispose();
            }
            return resumed;
        }
        catch (Exception)
        {
            resumed?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Earlier framework versions wrote one <c>restart</c> marker (token, helper id, start ticks) for a scheduled
    /// restart. Their helper exits on CLOSE instead of waiting to be reused, which leaves an idle file that the next
    /// <see cref="Acquire"/> discards.
    /// </summary>
    private static void MigrateRestartMarker(string directory)
    {
        var marker = Path.Combine(directory, "restart");
        if (!File.Exists(marker)) return;
        try
        {
            var fields = File.ReadAllLines(marker);
            if (fields.Length == 3 && Guid.TryParseExact(fields[0], "N", out _) &&
                ProcessIdentity.TryParse(fields[1], fields[2], out var helper) &&
                File.Exists(PathOf(directory, fields[0], Commands)))
                new State(helper, default, true).Write(PathOf(directory, fields[0], Owned));
        }
        finally { File.Delete(marker); }
    }

    private static ProgressSession? Adopt(string directory, string token, State state)
    {
        var commandFile = PathOf(directory, token, Commands);
        if (!File.Exists(commandFile))
        {
            // Unreachable helper: without its state file it exits once its window is hidden.
            Discard(directory, token);
            return null;
        }

        new State(state.Helper, Self, false).Write(PathOf(directory, token, Owned));
        var session = new ProgressSession(directory, token, state.Helper, OpenCommands(commandFile));
        session.Send("HEARTBEAT");
        return session;
    }

    private static void Discard(string directory, string token)
    {
        foreach (var extension in new[] { Owned, Idle, Commands, ".exiting" })
        {
            try { File.Delete(PathOf(directory, token, extension)); }
            catch (Exception) { /* stale files are discarded again later */ }
        }
    }

    public void Send(string command)
    {
        if (!IsRunning(_helper)) throw new IOException("The progress helper exited.");
        _commands.WriteLine(command);
    }

    public void DetachForRestart()
    {
        new State(_helper, Self, true).Write(PathOf(_directory, _token, Owned));
        Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            // Not waited for: the helper plays its closing animation (about 3 s), then waits briefly for the next
            // operation before it exits on its own.
            Send("CLOSE");
        }
        catch (Exception)
        {
            Release();
            try
            {
                if (IsRunning(_helper))
                {
                    using var process = Process.GetProcessById(_helper.Id);
                    process.Kill();
                }
            }
            catch (Exception) { /* feedback must not affect the operation */ }
            Discard(_directory, _token);
            return;
        }

        Release();
        // Without either state file the helper also exits once its window is hidden.
        try { File.Move(PathOf(_directory, _token, Owned), PathOf(_directory, _token, Idle), true); }
        catch (Exception) { /* see above */ }
    }

    private void Release()
    {
        _disposed = true;
        _commands.Dispose();
    }

    /// <param name="Helper">The helper process.</param>
    /// <param name="Owner">The editor process that uses or last used the window.</param>
    /// <param name="Restart">Whether the owner handed the window to the editor its restart starts.</param>
    private readonly record struct State(ProcessIdentity Helper, ProcessIdentity Owner, bool Restart)
    {
        public static State? Read(string path)
        {
            try
            {
                var fields = File.ReadAllLines(path);
                return fields.Length == 5 && ProcessIdentity.TryParse(fields[0], fields[1], out var helper) &&
                       ProcessIdentity.TryParse(fields[2], fields[3], out var owner) && fields[4] is "0" or "1"
                    ? new State(helper, owner, fields[4] == "1")
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Replaces the file in one step, so another editor never reads it half-written.</summary>
        public void Write(string path)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(temporary,
            [
                Helper.Id.ToString(CultureInfo.InvariantCulture), Helper.StartUtcTicks.ToString(CultureInfo.InvariantCulture),
                Owner.Id.ToString(CultureInfo.InvariantCulture), Owner.StartUtcTicks.ToString(CultureInfo.InvariantCulture),
                Restart ? "1" : "0"
            ]);
            File.Move(temporary, path, true);
        }
    }
}

/// <summary>A process, identified by its id and start time so that a reused id is not mistaken for it.</summary>
internal readonly record struct ProcessIdentity(int Id, long StartUtcTicks)
{
    public static ProcessIdentity Of(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks);

    public static ProcessIdentity Current()
    {
        using var process = Process.GetCurrentProcess();
        return Of(process);
    }

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
