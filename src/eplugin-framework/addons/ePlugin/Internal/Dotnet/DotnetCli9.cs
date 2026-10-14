#if TOOLS
using System.Collections.Generic;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>The dotnet CLI before .NET 10: <c>dotnet add|remove &lt;project&gt; package|reference</c>.</summary>
internal sealed class DotnetCli9(ILogger? logger, bool enableDebugLogging, IProcessRunner? runner = null,
    string? projectRoot = null)
    : DotnetCliBase(logger, enableDebugLogging, runner, projectRoot)
{
    private const string CmdDotNet = "dotnet";

    public override void RebuildSolution()
    {
        TryRebuildSolution();
    }

    public override bool TryRebuildSolution() => TryBuild().ExitCode == 0;

    public override BuildOutcome TryBuild()
    {
        var result = Execute(["build", SolutionPath, "--no-incremental"]);
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
        return Execute(virtualFolderName is null
            ? ["sln", SolutionPath, "add", ProjectFile(projectPath)]
            : ["sln", SolutionPath, "add", "-s", virtualFolderName, ProjectFile(projectPath)]).Item1 == 0;
    }

    public override bool AddNugetToProject(string nugetName, string? version = null, string? source = null,
        bool prerelease = false)
    {
        var args = new List<string> { "add", GodotProjectPath, "package", nugetName };
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
        return Execute(["remove", GodotProjectPath, "package", nugetName]).Item1 == 0;
    }

    public override void AddProjectReference(string projectReference)
    {
        TryAddProjectReference(projectReference);
    }

    public override bool TryAddProjectReference(string projectReference)
    {
        return Execute(["add", GodotProjectPath, "reference", ProjectFile(projectReference)]).Item1 == 0;
    }

    public override void RemoveProjectReference(string projectReference)
    {
        TryRemoveProjectReference(projectReference);
    }

    public override bool TryRemoveProjectReference(string projectReference)
    {
        if (CheckProjectReferenceRemoval(projectReference) is { } settled) return settled;
        return Execute(["remove", GodotProjectPath, "reference", ProjectFile(projectReference)]).Item1 == 0;
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
