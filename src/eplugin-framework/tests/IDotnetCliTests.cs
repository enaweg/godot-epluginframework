#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;
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

    private static string AssemblyName => ProjectSettings.GetSetting("dotnet/project/assembly_name").AsString();

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

        foreach (var cli in new DotnetCliBase[] { new DotnetCli9(new NullLogger(), false, ProjectRoot, AssemblyName), new DotnetCli10(new NullLogger(), false, ProjectRoot, AssemblyName) })
        {
            Assertions.AssertThat(cli.GodotProjectPath).IsEqual(expectedPath);
        }
    }

    [TestCase]
    public static void TheProjectIsNamedAfterTheAssemblyAndLocalSourcesAreRelativeToIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "eplugin-cli-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            // Godot names the project after the assembly; the solution may be named otherwise
            File.WriteAllText(Path.Combine(root, "My Game.sln"), "");
            File.WriteAllText(Path.Combine(root, "MyGame.csproj"), "<Project />");
            var runner = new RecordingRunner();
            var cli = new DotnetCli10(new NullLogger(), false, root, "MyGame", runner);
            Assertions.AssertString(cli.SolutionPath).IsEqual(Path.Combine(root, "My Game.sln"));
            Assertions.AssertString(cli.GodotProjectPath).IsEqual(Path.Combine(root, "MyGame.csproj"));
            Assertions.AssertString(new DotnetCli9(new NullLogger(), false, root, null, runner).GodotProjectPath).IsEqual(Path.Combine(root, "MyGame.csproj"));

            // the CLI's project, not the editor's or its working directory
            foreach (var source in new[] { "res://feed", "feed", "./feed", "res://feed/", "feed/" })
                Assertions.AssertString(cli.NugetSource(source)).IsEqual(Path.Combine(root, "feed"));
            Assertions.AssertString(cli.NugetSource("https://example.org/v3/index.json")).IsEqual("https://example.org/v3/index.json");
            Assertions.AssertString(cli.NugetSource(Path.Combine(root, "other"))).IsEqual(Path.Combine(root, "other"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase]
    public static void TheSolutionIsFoundInTheSolutionDirectory()
    {
        // GodotTools reads dotnet/project/solution_directory relative to res://
        Assertions.AssertString(Internal.EditorHost.SolutionDirectoryPath("")).IsEqual("res://");
        Assertions.AssertString(Internal.EditorHost.SolutionDirectoryPath("..")).IsEqual("res://..");
        Assertions.AssertString(Internal.EditorHost.SolutionDirectoryPath("res://sln")).IsEqual("res://sln");

        var repository = Path.Combine(Path.GetTempPath(), "eplugin-cli-" + Path.GetRandomFileName());
        var root = Path.Combine(repository, "game");
        Directory.CreateDirectory(root);
        try
        {
            // a solution in the repository's root (solution_directory ".."), which lists the game's library
            File.WriteAllText(Path.Combine(repository, "MyGame.sln"),
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"game\\Lib\\Lib.csproj\", \"{00000000-0000-0000-0000-000000000001}\"\nEndProject\n");
            File.WriteAllText(Path.Combine(repository, "Tools.sln"), "");
            File.WriteAllText(Path.Combine(root, "Other.sln"), "");
            foreach (var generation in new[] { 9, 10 })
            {
                var runner = new RecordingRunner();
                DotnetCliBase cli = generation == 9
                    ? new DotnetCli9(new NullLogger(), false, root, "MyGame", runner, Path.Combine(root, ".."))
                    : new DotnetCli10(new NullLogger(), false, root, "MyGame", runner, Path.Combine(root, ".."));
                Assertions.AssertString(cli.SolutionPath).IsEqual(Path.Combine(repository, "MyGame.sln"));
                // the Godot project stays in res://
                Assertions.AssertString(cli.GodotProjectPath).IsEqual(Path.Combine(root, "MyGame.csproj"));

                Assertions.AssertBool(cli.TryAddProjectToSolution("res://Lib/Lib.csproj", null)).IsTrue();
                Assertions.AssertBool(cli.TryRemoveProjectFromSolution("res://Lib/Lib.csproj")).IsTrue();
                // already gone from that solution: nothing to run
                Assertions.AssertBool(cli.TryRemoveProjectFromSolution("res://Gone/Gone.csproj")).IsTrue();
                var library = Path.Combine(root, "Lib", "Lib.csproj");
                Assertions.AssertThat(runner.Calls[0]).ContainsExactly("dotnet", "sln", Path.Combine(repository, "MyGame.sln"), "add", library);
                Assertions.AssertThat(runner.Calls[1]).ContainsExactly("dotnet", "sln", Path.Combine(repository, "MyGame.sln"), "remove", library);
                Assertions.AssertInt(runner.Calls.Count).IsEqual(2);
            }

            // the solution named after the assembly also in the XML format, before another solution
            File.Delete(Path.Combine(repository, "MyGame.sln"));
            File.WriteAllText(Path.Combine(repository, "MyGame.slnx"), "<Solution />");
            Assertions.AssertString(new DotnetCli10(new NullLogger(), false, root, "MyGame", new RecordingRunner(), repository).SolutionPath)
                .IsEqual(Path.Combine(repository, "MyGame.slnx"));
            // without one, the only solution there
            File.Delete(Path.Combine(repository, "MyGame.slnx"));
            Assertions.AssertString(new DotnetCli10(new NullLogger(), false, root, "MyGame", new RecordingRunner(), repository).SolutionPath)
                .IsEqual(Path.Combine(repository, "Tools.sln"));
            // and the project's folder without a solution directory
            Assertions.AssertString(new DotnetCli10(new NullLogger(), false, root, "MyGame", new RecordingRunner()).SolutionPath)
                .IsEqual(Path.Combine(root, "Other.sln"));
        }
        finally { Directory.Delete(repository, true); }
    }

    [TestCase]
    public static void TheDetectedGenerationChoosesTheCli()
    {
        foreach (var (version, expected) in new[] { ("10.0.401", typeof(DotnetCli10)), ("11.0.100-preview.1", typeof(DotnetCli10)), ("8.0.425", typeof(DotnetCli9)) })
        {
            var runner = new RecordingRunner(0, version);
            var manager = new DotnetVersionManager(new NullLogger(), false, runner);

            Assertions.AssertString(manager.DotnetVersion).IsEqual(version);
            Assertions.AssertObject(manager.Create(new NullLogger(), ProjectRoot, AssemblyName, ProjectRoot)).IsInstanceOf<IDotnetCli>();
            Assertions.AssertString(manager.Create(new NullLogger(), ProjectRoot, AssemblyName, ProjectRoot).GetType().Name).IsEqual(expected.Name);
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
            Assertions.AssertString(manager.Create(new NullLogger(), ProjectRoot, AssemblyName, ProjectRoot).GetType().Name).IsEqual(nameof(DotnetCli9));
        }
    }

    [TestCase]
    public static void ArgumentsReachTheCommandUnchangedThroughTheShellGodotUsesOutsideWindows()
    {
        Assertions.AssertString(GodotProcessRunner.QuotedForShell("a b'c")).IsEqual("a b'c");
        Assertions.AssertString(GodotProcessRunner.QuotedForShell("$HOME `x` \"q\" \\")).IsEqual("\\$HOME \\`x\\` \\\"q\\\" \\\\");
        // Windows starts the process without a shell, so only the other platforms run it.
        if (OperatingSystem.IsWindows()) return;
        const string argument = "/tmp/a b 'c' $HOME `id` \"q\" \\n;|&*";
        var (exitCode, output) = GodotProcessRunner.Instance.Run("printf", ["%s", argument]);
        Assertions.AssertInt(exitCode).IsEqual(0);
        Assertions.AssertString(string.Concat(output)).IsEqual(argument);
    }

    [TestCase]
    public static void BothGenerationsPassUnquotedPathsAndAcceptResPaths()
    {
        var project = Path.Combine(ProjectRoot, "EPluginFramework.csproj");
        var solution = Path.Combine(ProjectRoot, "EPluginFramework.sln");
        var absolute = Path.Combine(ProjectRoot, "addons", "other", "Other.csproj");

        var cli9Runner = new RecordingRunner();
        var cli9 = new DotnetCli9(new NullLogger(), false, ProjectRoot, AssemblyName, cli9Runner);
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
            ["dotnet", "add", project, "package", "Some.Package", "--version", "1.2.3", "--source", Path.Combine(ProjectRoot, "feed")],
            ["dotnet", "sln", solution, "add", absolute],
            ["dotnet", "sln", solution, "add", "-s", "Folder", absolute],
            ["dotnet", "add", project, "reference", absolute],
            ["dotnet", "remove", project, "package", "ZLogger"],
            ["dotnet", "test", solution],
            ["dotnet", "restore", "--force"]);

        var cli10Runner = new RecordingRunner();
        var cli10 = new DotnetCli10(new NullLogger(), false, ProjectRoot, AssemblyName, cli10Runner);
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
                ? new DotnetCli9(new NullLogger(), false, ProjectRoot, AssemblyName, runner)
                : new DotnetCli10(new NullLogger(), false, ProjectRoot, AssemblyName, runner);
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
        foreach (DotnetCliBase cli in new DotnetCliBase[] { new DotnetCli9(new NullLogger(), false, ProjectRoot, AssemblyName, new RecordingRunner(-1)), new DotnetCli10(new NullLogger(), false, ProjectRoot, AssemblyName, new RecordingRunner(-1)) })
        {
            Assertions.AssertBool(cli.AddNugetToProject("Some.Package", "1.0.0")).IsFalse();
            Assertions.AssertBool(cli.AddNugetToProject("Some.Package")).IsFalse();
            Assertions.AssertBool(cli.TryRebuildSolution()).IsFalse();
        }
    }

    [TestCase]
    public static void TheVoidFormsRunTheSameCommandsAndDebugLoggingShowsThem()
    {
        var project = Path.Combine(ProjectRoot, "EPluginFramework.csproj");
        var solution = Path.Combine(ProjectRoot, "EPluginFramework.sln");
        var absolute = Path.Combine(ProjectRoot, "addons", "other", "Other.csproj");
        foreach (var generation in new[] { 9, 10 })
        {
            var log = new List<string>();
            var runner = new RecordingRunner();
            DotnetCliBase cli = generation == 9
                ? new DotnetCli9(new RecordingLogger(log, "cli"), true, ProjectRoot, AssemblyName, runner)
                : new DotnetCli10(new RecordingLogger(log, "cli"), true, ProjectRoot, AssemblyName, runner);

            cli.RebuildSolution();
            cli.AddProjectToSolution("addons/other/Other.csproj");
            cli.AddProjectReference("addons/other/Other.csproj");
            cli.RemoveNugetFromProject("ZLogger");
            // not referenced: settled without the CLI
            cli.RemoveProjectReference("addons/other/Other.csproj");
            cli.RemoveProjectFromSolution("addons/other/Other.csproj");
            cli.AddNugetToProject("Some.Package", prerelease: true);

            Assertions.AssertInt(runner.Calls.Count).IsEqual(5);
            Assertions.AssertThat(runner.Calls[0]).ContainsExactly("dotnet", "build", solution, "--no-incremental");
            Assertions.AssertThat(runner.Calls[1]).ContainsExactly("dotnet", "sln", solution, "add", absolute);
            Assertions.AssertBool(runner.Calls[2].Contains(absolute) && runner.Calls[2].Contains(project)).IsTrue();
            Assertions.AssertBool(runner.Calls[3].Contains("ZLogger") && runner.Calls[3].Contains("remove")).IsTrue();
            Assertions.AssertString(runner.Calls[4][^1]).IsEqual("--prerelease");
            Assertions.AssertString(log[0]).IsEqual($"log: cli: Using {cli.GetType().Name} for dotnet commands.");
            // the project directory is shortened in the log
            Assertions.AssertBool(log.Any(l => l.StartsWith("log: cli: Executing: dotnet build <project>", System.StringComparison.Ordinal))).IsTrue();
            Assertions.AssertInt(log.Count(l => l.StartsWith("warn:", System.StringComparison.Ordinal))).IsEqual(2);
        }
    }

    [TestCase]
    public static void RemovingSomethingThatIsNotAProjectFileFailsWithoutTheCli()
    {
        foreach (var generation in new[] { 9, 10 })
        {
            var log = new List<string>();
            var runner = new RecordingRunner();
            DotnetCliBase cli = generation == 9
                ? new DotnetCli9(new RecordingLogger(log, "cli"), false, ProjectRoot, AssemblyName, runner)
                : new DotnetCli10(new RecordingLogger(log, "cli"), false, ProjectRoot, AssemblyName, runner);

            Assertions.AssertBool(cli.TryRemoveProjectReference("addons/other/readme.txt")).IsFalse();
            Assertions.AssertBool(cli.TryRemoveProjectFromSolution("addons/other/readme.txt")).IsFalse();

            Assertions.AssertInt(runner.Calls.Count).IsEqual(0);
            Assertions.AssertInt(log.Count(l => l.StartsWith("error:", System.StringComparison.Ordinal) && l.Contains("not a project file path"))).IsEqual(2);
        }
    }

    [TestCase]
    public static void ARunnerThatThrowsIsReportedAsAFailedCommand()
    {
        var log = new List<string>();
        var cli = new DotnetCli10(new RecordingLogger(log, "cli"), false, ProjectRoot, AssemblyName, new ThrowingRunner());

        Assertions.AssertInt(cli.Execute("restore", []).Item1).IsEqual(-1);
        Assertions.AssertBool(cli.TryRebuildSolution()).IsFalse();
        Assertions.AssertBool(log.Contains("error: cli: dotnet is not installed")).IsTrue();
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public (int ExitCode, string[] Output) Run(string command, string[] arguments) =>
            throw new IOException("dotnet is not installed");
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
                    ? new DotnetCli9(new NullLogger(), false, root)
                    : new DotnetCli10(new NullLogger(), false, root);
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

    [TestCase]
    public static void BothGenerationsBuildPluginsOnlyWithTheSameArguments()
    {
        var project = Path.Combine(ProjectRoot, "EPluginFramework.csproj");
        var work = Path.Combine(Path.GetTempPath(), "eplugin-plugins-only-" + Path.GetRandomFileName());
        try
        {
            var build = new PluginsOnlyBuild(work, [Path.Combine(ProjectRoot, "addons")], Path.Combine(work, "errors.log"));
            foreach (var generation in new[] { 9, 10 })
            {
                var runner = new RecordingRunner();
                DotnetCliBase cli = generation == 9 ? new DotnetCli9(new NullLogger(), false, ProjectRoot, AssemblyName, runner) : new DotnetCli10(new NullLogger(), false, ProjectRoot, AssemblyName, runner);
                cli.TryBuildPluginsOnly(build);
                AssertCalls(runner, ["dotnet", "build", project, "-c", "Debug", "--no-incremental",
                    "-p:CustomAfterMicrosoftCommonTargets=" + build.TargetsFile, "-p:OutDir=" + build.OutputDirectory,
                    "-fileLoggerParameters:LogFile=" + build.ErrorLog + ";ErrorsOnly;Encoding=UTF-8"]);
                Assertions.AssertString(File.ReadAllText(build.TargetsFile)).Contains(Path.Combine(ProjectRoot, "addons") + Path.DirectorySeparatorChar);
            }
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    [TestCase(Timeout = 180000)]
    public static void APluginsOnlyBuildLeavesOutTheProjectsCode()
    {
        // The project's own code and its own library use an API the plugin no longer has, as after a breaking update.
        var root = Path.Combine(Path.GetTempPath(), "eplugin plugins-only 'a$b,c' " + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "addons", "plug"));
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Game.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Remove="Lib/**" /><ProjectReference Include="Lib/Lib.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Game.sln"), "");
            File.WriteAllText(Path.Combine(root, "addons", "plug", "Plug.cs"), "public static class Plug { public static int ApiV2() => 2; }");
            File.WriteAllText(Path.Combine(root, "Main.cs"), "public static class Main { public static int Run() => Plug.Api(); }");
            File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(root, "Lib", "Lib.cs"), "public static class Lib { public static int Run() => Missing.Value; }");
            var sdk = new DotnetVersionManager(new NullLogger(), false);
            DotnetCliBase cli = DotnetVersionManager.ParseMajorVersion(sdk.DotnetVersion) >= 10
                ? new DotnetCli10(new NullLogger(), false, root)
                : new DotnetCli9(new NullLogger(), false, root);
            var full = cli.Execute("build", [Path.Combine(root, "Game.csproj")]);
            Assertions.AssertInt(full.Item1).IsNotEqual(0);
            Assertions.AssertObject(BuildDiagnostics.Classify(full.Item2, root).Cause).IsEqual(BuildFailureCause.ProjectCode);

            var work = Path.Combine(root, ".godot", "plugins-only");
            var build = new PluginsOnlyBuild(work, [Path.Combine(root, "addons")], Path.Combine(root, ".godot", "errors.log"));
            var outcome = cli.TryBuildPluginsOnly(build);
            Assertions.AssertInt(outcome.ExitCode).OverrideFailureMessage(string.Join("\n", outcome.Output)).IsEqual(0);
            Assertions.AssertBool(File.Exists(Path.Combine(build.OutputDirectory, "Game.dll"))).IsTrue();

            // The same build fails when the plugin itself is broken.
            File.WriteAllText(Path.Combine(root, "addons", "plug", "Plug.cs"), "public static class Plug { public static int ApiV2() => Missing.Value; }");
            outcome = cli.TryBuildPluginsOnly(build);
            Assertions.AssertInt(outcome.ExitCode).IsNotEqual(0);
            Assertions.AssertObject(BuildDiagnostics.Classify(File.ReadAllLines(build.ErrorLog), root).Cause).IsEqual(BuildFailureCause.PluginCode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Runs <paramref name="build"/> for every command, like a Rebuild that cleans the outputs first.</summary>
    private sealed class BuildingRunner(Func<int> build) : IProcessRunner
    {
        public (int ExitCode, string[] Output) Run(string command, string[] arguments) => (build(), []);
    }

    [TestCase]
    public static void AFailedBuildPutsBackTheAssemblyItRemoved()
    {
        foreach (var generation in new[] { 9, 10 })
        {
            var root = Path.Combine(Path.GetTempPath(), "eplugin-output-" + Path.GetRandomFileName());
            var output = Path.Combine(root, ".godot", "mono", "temp", "bin", "Debug");
            Directory.CreateDirectory(Path.Combine(output, "runtimes"));
            try
            {
                File.WriteAllText(Path.Combine(root, "Game.sln"), "");
                File.WriteAllText(Path.Combine(output, "Game.dll"), "old");
                File.WriteAllText(Path.Combine(output, "runtimes", "native.dll"), "old");
                var built = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(output, "Game.dll"), built);
                var failing = new BuildingRunner(() => { Directory.Delete(output, true); Directory.CreateDirectory(output); return 1; });
                DotnetCliBase cli = generation == 9
                    ? new DotnetCli9(new NullLogger(), false, root, runner: failing)
                    : new DotnetCli10(new NullLogger(), false, root, runner: failing);

                Assertions.AssertInt(cli.TryBuild().ExitCode).IsEqual(1);
                Assertions.AssertString(File.ReadAllText(Path.Combine(output, "Game.dll"))).IsEqual("old");
                Assertions.AssertString(File.ReadAllText(Path.Combine(output, "runtimes", "native.dll"))).IsEqual("old");
                // The same timestamp, so the editor does not take it for a new build and reload.
                Assertions.AssertThat(File.GetLastWriteTimeUtc(Path.Combine(output, "Game.dll"))).IsEqual(built);
                Assertions.AssertBool(Directory.Exists(Path.Combine(root, ".godot", "eplugin", "build-output"))).IsFalse();

                var succeeding = new BuildingRunner(() => { File.WriteAllText(Path.Combine(output, "Game.dll"), "new"); return 0; });
                cli = generation == 9 ? new DotnetCli9(new NullLogger(), false, root, runner: succeeding) : new DotnetCli10(new NullLogger(), false, root, runner: succeeding);
                Assertions.AssertInt(cli.TryBuild().ExitCode).IsEqual(0);
                Assertions.AssertString(File.ReadAllText(Path.Combine(output, "Game.dll"))).IsEqual("new");

                // A build that failed after the project compiled keeps the new assembly.
                var lateFailure = new BuildingRunner(() => { File.WriteAllText(Path.Combine(output, "Game.dll"), "newer"); return 1; });
                cli = generation == 9 ? new DotnetCli9(new NullLogger(), false, root, runner: lateFailure) : new DotnetCli10(new NullLogger(), false, root, runner: lateFailure);
                Assertions.AssertInt(cli.TryBuild().ExitCode).IsEqual(1);
                Assertions.AssertString(File.ReadAllText(Path.Combine(output, "Game.dll"))).IsEqual("newer");
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
