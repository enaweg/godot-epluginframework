#:property TargetFramework=net10.0
#:property PublishAot=false
// Exercises the published progress helper's startup and shutdown protocol on a desktop session.
//
// Usage: dotnet run tools/progress_smoke.cs -- <command that starts the helper>
//        e.g. dotnet run tools/progress_smoke.cs -- dotnet path/to/ActivationProgress.dll
// Exits with 0 when every check passed.
using System.Diagnostics;
using System.Text;

// Another editor process appending to the command file and closing it again (see the shared command file checks).
if (args is ["--append-editor-commands", var target])
{
    File.AppendAllText(target, "HEARTBEAT\nTEXT V29ya2luZy4uLg==\n");
    return 0;
}

// The command to start the helper, e.g. `dotnet path/to/ActivationProgress.dll`.
var command = args;
if (command.Length == 0 || !File.Exists(command[^1]))
{
    Console.Error.WriteLine("Usage: dotnet run tools/progress_smoke.cs -- <command that starts the helper>");
    return 2;
}

try
{
    Smoke(command);
}
catch (SmokeException exception)
{
    Console.Error.WriteLine($"Smoke test failed: {exception.Message}");
    return 1;
}
finally
{
    foreach (var helper in Helpers.Started)
    {
        if (!helper.HasExited)
            helper.Kill(true);
        helper.Dispose();
    }
}
Console.WriteLine("Progress helper smoke test passed.");
return 0;

static void Smoke(string[] command)
{
    var label = Text("Activating sample plugin");
    {
        var process = StartHelper(command);
        // Past the helper's show delay, so the window is visible when the text changes.
        Thread.Sleep(1000);
        Send(process, $"TEXT {label}\nCLOSE\n");
        Check(Exits(process, 5), "helper did not close after CLOSE");
    }

    // Separate editor processes append to one command channel. Closing each writer (even before the
    // helper starts) must leave the same helper alive through two restarts, until the final CLOSE.
    using (var directory = new TemporaryDirectory())
    {
        var commandFile = directory.Create("progress.commands");
        {
            var process = Start(command, ["--command-file", commandFile, "--max-lifetime-seconds", "5"]);
            for (var writer = 0; writer < 3; writer++)
            {
                AppendFromAnotherProcess(commandFile);
                Thread.Sleep(1000);
                Check(!process.HasExited, "helper exited when an editor writer closed");
            }
            // A partial command must wait for its newline, including when the next editor appends it.
            File.AppendAllText(commandFile, "CLO");
            Thread.Sleep(200);
            Check(!process.HasExited, "helper ran a partial command");
            File.AppendAllText(commandFile, "SE\n");
            // CLOSE ends the anonymous user; the helper exits once its lifetime has passed since that last message.
            Check(Exits(process, 15), "helper did not exit after CLOSE from the command file");
        }

        // An abandoned restart retains the same bounded lifetime as a stalled editor operation.
        File.WriteAllText(commandFile, "");
        {
            var process = Start(command, ["--command-file", commandFile, "--max-lifetime-seconds", "2"]);
            Check(Exits(process, 10), "helper outlived its lifetime without commands");
        }
    }

    // Several users share one helper through its command file. The window follows them, but the helper keeps running until
    // no user has sent anything for its lifetime; then it withdraws its record (<token>.helper), exits and removes its files.
    using (var directory = new TemporaryDirectory())
    {
        var commandFile = directory.Create("token.commands");
        directory.Create("token.helper");
        void Append(string text) => File.AppendAllText(commandFile, text);

        var process = Start(command,
            ["--command-file", commandFile, "--max-lifetime-seconds", "4", "--window-timeout-seconds", "1"]);
        Append("first TEXT QQ==\nsecond TEXT Qg==\n");
        for (var beat = 0; beat < 3; beat++)
        {
            Thread.Sleep(1000);
            Append("first HEARTBEAT\nsecond HOLD 30\n");
        }
        Append("first END\n");
        // Both users are still known: the helper outlives the window until its lifetime has passed since the last message.
        Thread.Sleep(2000);
        Check(!process.HasExited, "helper exited while a user holds it");
        Append("second END\n");
        Check(Exits(process, 15), "helper did not exit after its last user ended");
        var left = Directory.GetFileSystemEntries(directory.Path);
        Check(left.Length == 0, $"helper left files behind: {string.Join(", ", left.Select(Path.GetFileName))}");
    }

    {
        var process = StartHelper(command);
        process.StandardInput.Close();
        Check(Exits(process, 5), "helper did not exit when its standard input closed");
    }

    // Placement arguments; each platform ignores the one it does not use.
    {
        var process = StartHelper(command, ["--editor-window", "0", "--editor-center", "400", "300"]);
        Thread.Sleep(1000);
        Send(process, "CLOSE\n");
        Check(Exits(process, 5), "helper with placement arguments did not close");
    }

    // The window has no close button, so the helper must end itself when the host never closes it.
    {
        var process = StartHelper(command, ["--max-lifetime-seconds", "2"]);
        Check(Exits(process, 10), "helper outlived its lifetime");
    }

    // Each HEARTBEAT restarts the lifetime, so a long chain of plugin installs keeps the window; without more
    // heartbeats it still ends on its own.
    {
        var process = StartHelper(command, ["--max-lifetime-seconds", "3"]);
        // Outlives the 3 s lifetime only if the heartbeats restart it.
        for (var beat = 0; beat < 5; beat++)
        {
            Thread.Sleep(1000);
            Send(process, "HEARTBEAT\n");
        }
        Check(!process.HasExited, "helper exited despite heartbeats");
        Check(Exits(process, 10), "helper did not end after the heartbeats stopped");
    }

    // The host never waits for READY: commands are queued before the helper has started, and a helper closed
    // before its show delay must exit without ever showing its window.
    {
        var process = Start(command, [], keepInput: true);
        Send(process, $"TEXT {Text("Deactivating sample plugin")}\nCLOSE\n");
        // A cold .NET start can be slow on CI runners.
        Check(Exits(process, 20), "helper closed before its show delay did not exit");
    }
}

static string Text(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

/// Starts the helper with standard input kept open for commands; without it the helper sees the end of its input at once.
static Process Start(string[] command, string[] extra, bool keepInput = false)
{
    var start = new ProcessStartInfo(command[0])
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false),
    };
    foreach (var argument in command[1..].Concat(extra))
        start.ArgumentList.Add(argument);
    var process = Process.Start(start)!;
    Helpers.Started.Add(process);
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();
    if (!keepInput)
        process.StandardInput.Close();
    return process;
}

/// Starts the helper and waits for it to report READY.
static Process StartHelper(string[] command, string[]? extra = null)
{
    var process = Start(command, extra ?? [], keepInput: true);
    // A cold .NET start can be slow on CI runners.
    var line = process.StandardOutput.ReadLineAsync();
    if (line.Wait(TimeSpan.FromSeconds(20)) && line.Result == "READY")
        return process;
    process.Kill(true);
    throw new SmokeException("helper did not become ready");
}

static void Send(Process process, string commands)
{
    process.StandardInput.Write(commands);
    process.StandardInput.Flush();
}

/// Whether the helper exits with 0 within the timeout; it is killed when it does not exit.
static bool Exits(Process process, int seconds)
{
    if (process.WaitForExit(TimeSpan.FromSeconds(seconds)))
        return process.ExitCode == 0;
    process.Kill(true);
    return false;
}

/// Appends commands from a separate process that closes the file again, like another editor.
static void AppendFromAnotherProcess(string commandFile)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!);
    start.ArgumentList.Add("--append-editor-commands");
    start.ArgumentList.Add(commandFile);
    using var writer = Process.Start(start)!;
    writer.WaitForExit();
    Check(writer.ExitCode == 0, "another editor could not append to the command file");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new SmokeException(message);
}

sealed class SmokeException(string message) : Exception(message);

/// Every helper started, so none survives a failed check; they are disposed at the end.
static class Helpers
{
    public static readonly List<Process> Started = [];
}

sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("eplugin-progress-smoke-").FullName;

    public string Create(string name)
    {
        var file = System.IO.Path.Combine(Path, name);
        File.WriteAllText(file, "");
        return file;
    }

    public void Dispose() => Directory.Delete(Path, true);
}
