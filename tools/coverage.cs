#:property TargetFramework=net10.0
#:property PublishAot=false
// Runs the gdUnit4 test suite with code coverage and writes a Cobertura report of the framework (addons/ePlugin).
//
// gdUnit4 runs the tests inside a Godot process, and Godot loads the project assembly from a memory stream. Profiler
// based collectors (`dotnet test --collect "Code Coverage"`, coverlet) therefore never see its code. Instead the built
// assembly is instrumented on disk with dotnet-coverage, a dotnet-coverage server session collects what the Godot
// process reports, and the original assembly is restored afterwards.
//
// Usage: dotnet run tools/coverage.cs -- <report.cobertura.xml> [dotnet test arguments...]
// Needs a Debug build of the solution, GODOT_BIN (like dotnet test) and dotnet-coverage on PATH
// (`dotnet tool install --global dotnet-coverage`). Exits with the exit code of dotnet test.
using System.Diagnostics;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run tools/coverage.cs -- <report.cobertura.xml> [dotnet test arguments...]");
    return 2;
}
var report = Path.GetFullPath(args[0]);
var testArguments = args[1..];

var scriptDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Environment.CurrentDirectory;
var project = Path.GetFullPath(Path.Combine(scriptDirectory, "../src/eplugin-framework"));
var assembly = Path.Combine(project, ".godot/mono/temp/bin/Debug/EPluginFramework.dll");
var settings = Path.Combine(project, "coverage.settings.xml");
var session = $"eplugin-coverage-{Environment.ProcessId}";

if (!File.Exists(assembly))
{
    Console.Error.WriteLine($"error: {assembly} not found, build the solution first");
    return 2;
}
Directory.CreateDirectory(Path.GetDirectoryName(report)!);

var collecting = false;
// Like a shell trap: Ctrl+C stops dotnet test (it gets the signal too), and the assembly is still restored below.
Console.CancelKeyPress += (_, e) => e.Cancel = true;
try
{
    Check(Run("dotnet-coverage", ["instrument", assembly, "--session-id", session, "--settings", settings]));
    // The background server inherits the output of the command that starts it, and keeps it open until it is shut down.
    // Its output is therefore discarded, and the start is not waited for with an infinite timeout, which would wait for
    // the output to end too.
    Check(Run("dotnet-coverage", ["collect", "--session-id", session, "--server-mode", "--background", "--settings", settings,
        "--output-format", "cobertura", "--output", report], discardOutput: true));
    collecting = true;

    var status = Run("dotnet", ["test", "EPluginFramework.sln", "--configuration", "Debug", "--no-build", "--settings", ".runsettings",
        .. testArguments], workingDirectory: project);

    collecting = false;
    Check(Run("dotnet-coverage", ["shutdown", session]));
    if (!File.Exists(report) || new FileInfo(report).Length == 0)
    {
        Console.Error.WriteLine($"error: no coverage report was written to {report}");
        return 1;
    }
    return status;
}
catch (CommandFailedException exception)
{
    return exception.ExitCode;
}
finally
{
    // Shutting down a session that is not running waits forever.
    if (collecting)
        Run("dotnet-coverage", ["shutdown", session], discardOutput: true);
    // instrument keeps the originals; an instrumented assembly would otherwise survive an incremental build.
    foreach (var file in new[] { assembly, Path.ChangeExtension(assembly, ".pdb") })
    {
        if (File.Exists(file + ".orig"))
            File.Move(file + ".orig", file, true);
    }
}

static int Run(string command, IEnumerable<string> arguments, string? workingDirectory = null, bool discardOutput = false)
{
    var start = new ProcessStartInfo(command)
    {
        WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        RedirectStandardOutput = discardOutput,
        RedirectStandardError = discardOutput,
    };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    start.Environment["DOTNET_COVERAGE_NOLOGO"] = "1";
    start.Environment["DOTNET_COVERAGE_TELEMETRY_OPTOUT"] = "1";
    UseDotnetOnPath(start);
    using var process = Process.Start(start)!;
    if (discardOutput)
    {
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(TimeSpan.FromHours(1));
    }
    else
    {
        process.WaitForExit();
    }
    return process.ExitCode;
}

// `dotnet run` points DOTNET_ROOT and DOTNET_HOST_PATH at the SDK that runs this file. The tests have to use the dotnet
// on PATH instead: in CI that is another installation, whose SDK decides which dotnet CLI generation ePlugin uses.
static void UseDotnetOnPath(ProcessStartInfo start)
{
    var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, name))
        .FirstOrDefault(File.Exists);
    if (dotnet is null)
        return;
    var host = new FileInfo(dotnet).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(dotnet);
    foreach (var variable in start.Environment.Keys.Where(key => key.StartsWith("DOTNET_ROOT", StringComparison.Ordinal)).ToList())
        start.Environment.Remove(variable);
    start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host);
    start.Environment["DOTNET_HOST_PATH"] = host;
}

static void Check(int exitCode)
{
    if (exitCode != 0)
        throw new CommandFailedException(exitCode);
}

sealed class CommandFailedException(int exitCode) : Exception($"exit code {exitCode}")
{
    public int ExitCode { get; } = exitCode;
}
