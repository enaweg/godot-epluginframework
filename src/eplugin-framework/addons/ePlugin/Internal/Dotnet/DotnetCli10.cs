#if TOOLS
using System.Collections.Generic;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>The dotnet CLI from .NET 10: <c>dotnet package|reference add|remove ... --project &lt;project&gt;</c>.</summary>
internal sealed class DotnetCli10(ILogger? logger, bool enableDebugLogging, string projectRoot, string? assemblyName = null,
    IProcessRunner? runner = null, string? solutionDirectory = null)
    : DotnetCliBase(logger, enableDebugLogging, projectRoot, assemblyName, runner, solutionDirectory)
{
    private const string CmdDotNet = "dotnet";

    public override void RebuildSolution()
    {
        TryRebuildSolution();
    }

    public override bool TryRebuildSolution() => TryBuild().ExitCode == 0;

    public override BuildOutcome TryBuild(bool keepNewAssembly = true) => BuildKeepingOutput(["build", SolutionPath, "--no-incremental"], keepNewAssembly);

    public override BuildOutcome TryBuildPluginsOnly(PluginsOnlyBuild build)
    {
        build.WriteTargets(GodotProjectPath);
        var result = Execute(build.Arguments(GodotProjectPath));
        return new BuildOutcome(result.Item1, result.Item2);
    }

    public override void RunTests()
    {
        Execute(["test", SolutionPath]);
    }

    public override void RemoveProjectFromSolution(string projectPath)
    {
        TryRemoveProjectFromSolution(projectPath);
    }

    public override bool TryRemoveProjectFromSolution(string projectPath)
    {
        if (CheckSolutionRemoval(projectPath) is { } settled) return settled;
        return Execute(["sln", SolutionPath, "remove", ProjectFile(projectPath)]).Item1 == 0;
    }

    public override void AddProjectToSolution(string projectPath, string? virtualFolderName = null)
    {
        TryAddProjectToSolution(projectPath, virtualFolderName);
    }

    public override bool TryAddProjectToSolution(string projectPath, string? virtualFolderName)
    {
        if (Execute(virtualFolderName is null
            ? ["sln", SolutionPath, "add", ProjectFile(projectPath)]
            : ["sln", SolutionPath, "add", "-s", virtualFolderName, ProjectFile(projectPath)]).Item1 != 0) return false;
        MapExportRelease(projectPath, reference: false);
        return true;
    }

    public override bool AddNugetToProject(string nugetName, string? version = null, string? source = null,
        bool prerelease = false)
    {
        var args = new List<string> { "package", "add", nugetName, "--project", GodotProjectPath };
        if (version is not null)
        {
            args.Add("--version");
            args.Add(version);
        }

        if (source is not null)
        {
            args.Add("--source");
            args.Add(NugetSource(source));
        }

        if (prerelease)
        {
            args.Add("--prerelease");
        }

        var installSuccess = Execute(args.ToArray()).Item1 == 0;
        if (!installSuccess)
        {
            // the command's output is logged with its exit code
            Logger?.Error(version is not null ? $"Installing {nugetName} v{version} failed!" : $"Installing {nugetName} failed!");
        }

        return installSuccess;
    }

    public override void RemoveNugetFromProject(string nugetName)
    {
        TryRemoveNugetFromProject(nugetName);
    }

    public override bool TryRemoveNugetFromProject(string nugetName)
    {
        if (CheckPackageRemoval(nugetName) is { } settled) return settled;
        return Execute(["package", "remove", nugetName, "--project", GodotProjectPath]).Item1 == 0;
    }

    public override void AddProjectReference(string projectReference)
    {
        TryAddProjectReference(projectReference);
    }

    public override bool TryAddProjectReference(string projectReference)
    {
        if (Execute(["reference", "add", ProjectFile(projectReference), "--project", GodotProjectPath]).Item1 != 0) return false;
        MapExportRelease(projectReference, reference: true);
        return true;
    }

    public override void RemoveProjectReference(string projectReference)
    {
        TryRemoveProjectReference(projectReference);
    }

    public override bool TryRemoveProjectReference(string projectReference)
    {
        if (CheckProjectReferenceRemoval(projectReference) is { } settled) return settled;
        return Execute(["reference", "remove", ProjectFile(projectReference), "--project", GodotProjectPath]).Item1 == 0;
    }

    public override (int, string[]) Execute(string command, string[] args)
    {
        return Execute([command, ..args]);
    }

    private (int, string[]) Execute(string[] args)
    {
        return ExecuteCall(CmdDotNet, args);
    }
}
#endif
