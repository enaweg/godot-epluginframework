#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class IDotnetCliTests
{
    /// <summary>Records the commands instead of running them and answers with the given exit code and output.</summary>
    private sealed class RecordingRunner(int exitCode = 0, params string[] output) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public (int ExitCode, string[] Output) Run(string command, string[] arguments)
        {
            Calls.Add([command, ..arguments]);
            return (exitCode, output);
        }
    }

    private static string ProjectRoot => Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));

    [TestCase]
    public static void VersionTest()
    {
        var dotnetVersionManager = new DotnetVersionManager(new NullLogger(), false);

        Assertions.AssertThat(dotnetVersionManager.DotnetVersion).IsNotEmpty();
        Assertions.AssertBool(dotnetVersionManager.IsDotnetAvailable).IsTrue();
    }

    [TestCase]
    public static void ProjectPathUsesResolvedSolutionName()
    {
        var expectedPath = Path.Combine(ProjectRoot, "EPluginFramework.csproj");

        foreach (var cli in new DotnetCliBase[] { new DotnetCli9(new NullLogger(), false), new DotnetCli10(new NullLogger(), false) })
        {
            Assertions.AssertThat(cli.GodotProjectPath).IsEqual(expectedPath);
        }
    }

    [TestCase]
    public static void TheDetectedGenerationChoosesTheCli()
    {
        foreach (var (version, expected) in new[] { ("10.0.401", typeof(DotnetCli10)), ("11.0.100-preview.1", typeof(DotnetCli10)), ("8.0.425", typeof(DotnetCli9)) })
        {
            var runner = new RecordingRunner(0, version);
            var manager = new DotnetVersionManager(new NullLogger(), false, runner);

            Assertions.AssertString(manager.DotnetVersion).IsEqual(version);
            Assertions.AssertObject(manager.Create(new NullLogger())).IsInstanceOf<IDotnetCli>();
            Assertions.AssertString(manager.Create(new NullLogger()).GetType().Name).IsEqual(expected.Name);
            Assertions.AssertThat(runner.Calls.Single()).ContainsExactly("dotnet", "--version");
        }
    }

    [TestCase]
    public void MissingDotnetIsReportedInsteadOfThrowing()
    {
        // `dotnet` fails to start (-1), a global.json names a missing SDK (non-zero), or it prints nothing.
        foreach (var runner in new[] { new RecordingRunner(-1), new RecordingRunner(1, "A compatible .NET SDK was not found."), new RecordingRunner(0) })
        {
            var manager = new DotnetVersionManager(new NullLogger(), false, runner);

            Assertions.AssertBool(manager.IsDotnetAvailable).IsFalse();
            Assertions.AssertObject(manager.DotnetVersion).IsNull();
            Assertions.AssertString(manager.Create(new NullLogger()).GetType().Name).IsEqual(nameof(DotnetCli9));
        }
    }

    [TestCase]
    public static void BothGenerationsPassUnquotedPathsAndAcceptResPaths()
    {
        var project = Path.Combine(ProjectRoot, "EPluginFramework.csproj");
        var solution = Path.Combine(ProjectRoot, "EPluginFramework.sln");
        var absolute = Path.Combine(ProjectRoot, "addons", "other", "Other.csproj");

        var cli9Runner = new RecordingRunner();
        var cli9 = new DotnetCli9(new NullLogger(), false, cli9Runner);
        cli9.TryBuild();
        cli9.AddNugetToProject("Some.Package", "1.2.3", "res://feed");
        cli9.TryAddProjectToSolution("res://addons/other/Other.csproj", null);
        cli9.TryAddProjectToSolution("addons/other/Other.csproj", "Folder");
        cli9.TryAddProjectReference("res://addons/other/Other.csproj");
        cli9.TryRemoveNugetFromProject("ZLogger"); // referenced by the test project, so the CLI is asked
        cli9.RunTests();
        cli9.Execute("restore", ["--force"]);

        AssertCalls(cli9Runner,
            ["dotnet", "build", solution, "--no-incremental"],
            ["dotnet", "add", project, "package", "Some.Package", "--version", "1.2.3", "--source", ProjectSettings.GlobalizePath("res://feed")],
            ["dotnet", "sln", solution, "add", absolute],
            ["dotnet", "sln", solution, "add", "-s", "Folder", absolute],
            ["dotnet", "add", project, "reference", absolute],
            ["dotnet", "remove", project, "package", "ZLogger"],
            ["dotnet", "test", solution],
            ["dotnet", "restore", "--force"]);

        var cli10Runner = new RecordingRunner();
        var cli10 = new DotnetCli10(new NullLogger(), false, cli10Runner);
        cli10.TryBuild();
        cli10.AddNugetToProject("Some.Package", null, "https://example.org/v3/index.json", prerelease: true);
        cli10.TryAddProjectToSolution("res://addons/other/Other.csproj", "Folder");
        cli10.TryAddProjectReference("addons/other/Other.csproj");
        cli10.TryRemoveNugetFromProject("ZLogger");
        cli10.RunTests();
        cli10.Execute("restore", ["--force"]);

        AssertCalls(cli10Runner,
            ["dotnet", "build", solution, "--no-incremental"],
            ["dotnet", "package", "add", "Some.Package", "--project", project, "--source", "https://example.org/v3/index.json", "--prerelease"],
            ["dotnet", "sln", solution, "add", "-s", "Folder", absolute],
            ["dotnet", "reference", "add", absolute, "--project", project],
            ["dotnet", "package", "remove", "ZLogger", "--project", project],
            ["dotnet", "test", solution],
            ["dotnet", "restore", "--force"]);
    }

    [TestCase]
    public static void RemovingWhatIsAlreadyGoneDoesNotRunTheCli()
    {
        foreach (var generation in new[] { 9, 10 })
        {
            var runner = new RecordingRunner();
            DotnetCliBase cli = generation == 9
                ? new DotnetCli9(new NullLogger(), false, runner)
                : new DotnetCli10(new NullLogger(), false, runner);
            Assertions.AssertBool(cli.TryRemoveNugetFromProject("Not.Referenced.Package")).IsTrue();
            Assertions.AssertBool(cli.TryRemoveProjectReference("res://addons/missing/Missing.csproj")).IsTrue();
            Assertions.AssertBool(cli.TryRemoveProjectFromSolution("res://addons/missing/Missing.csproj")).IsTrue();
            Assertions.AssertBool(cli.TryRemoveNugetFromProject("not a package id")).IsFalse();
            Assertions.AssertInt(runner.Calls.Count).IsEqual(0);
        }
    }

    [TestCase]
    public static void AFailingCommandIsReportedWithoutThrowing()
    {
        // a command that cannot start answers without output; the error message must not need any
        foreach (DotnetCliBase cli in new DotnetCliBase[] { new DotnetCli9(new NullLogger(), false, new RecordingRunner(-1)), new DotnetCli10(new NullLogger(), false, new RecordingRunner(-1)) })
        {
            Assertions.AssertBool(cli.AddNugetToProject("Some.Package", "1.0.0")).IsFalse();
            Assertions.AssertBool(cli.AddNugetToProject("Some.Package")).IsFalse();
            Assertions.AssertBool(cli.TryRebuildSolution()).IsFalse();
        }
    }

    [TestCase(Timeout = 120000)]
    public static void BothGenerationsChangeARealProjectAndUndoIt()
    {
        // The installed SDK runs both command forms; the .NET 10 form needs SDK 10 or newer.
        var sdk = new DotnetVersionManager(new NullLogger(), false);
        var generations = DotnetVersionManager.ParseMajorVersion(sdk.DotnetVersion) >= 10 ? new[] { 9, 10 } : [9];
        foreach (var generation in generations)
        {
            var root = Path.Combine(Path.GetTempPath(), "eplugin-cli-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(root, "addons", "my lib"));
            try
            {
                const string project = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";
                File.WriteAllText(Path.Combine(root, "Game.csproj"), project);
                File.WriteAllText(Path.Combine(root, "addons", "my lib", "Lib.csproj"), project);
                File.WriteAllText(Path.Combine(root, "Game.sln"),
                    "Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nGlobal\nEndGlobal\n");
                DotnetCliBase cli = generation == 9
                    ? new DotnetCli9(new NullLogger(), false, projectRoot: root)
                    : new DotnetCli10(new NullLogger(), false, projectRoot: root);
                const string lib = "res://addons/my lib/Lib.csproj"; // a path with a space, as a res:// path
                bool References() => File.ReadAllText(Path.Combine(root, "Game.csproj")).Contains("Lib.csproj");
                bool Contains() => File.ReadAllText(Path.Combine(root, "Game.sln")).Contains("Lib.csproj");

                Assertions.AssertBool(cli.TryAddProjectToSolution(lib, "Libraries")).IsTrue();
                Assertions.AssertBool(cli.TryAddProjectReference(lib)).IsTrue();
                Assertions.AssertBool(Contains() && References()).IsTrue();

                Assertions.AssertBool(cli.TryRemoveProjectReference(lib)).IsTrue();
                Assertions.AssertBool(cli.TryRemoveProjectFromSolution(lib)).IsTrue();
                Assertions.AssertBool(Contains() || References()).IsFalse();
                // already gone counts as done
                Assertions.AssertBool(cli.TryRemoveProjectReference(lib)).IsTrue();
                Assertions.AssertBool(cli.TryRemoveProjectFromSolution(lib)).IsTrue();
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void AssertCalls(RecordingRunner runner, params string[][] expected)
    {
        Assertions.AssertInt(runner.Calls.Count).IsEqual(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assertions.AssertString(string.Join(" | ", runner.Calls[i])).IsEqual(string.Join(" | ", expected[i]));
        }

        Assertions.AssertBool(runner.Calls.SelectMany(c => c).Any(argument => argument.Contains('"'))).IsFalse();
    }
}
#endif
