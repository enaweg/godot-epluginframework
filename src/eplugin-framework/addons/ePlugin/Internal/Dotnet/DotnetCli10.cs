#if TOOLS
using System.Collections.Generic;
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal sealed class DotnetCli10(ILogger? logger, bool enableDebugLogging)
    : DotnetCliBase(logger, enableDebugLogging), IDotnetCli
{
    private const string CmdDotNet = "dotnet";

    public override void RebuildSolution()
    {
        TryRebuildSolution();
    }

    public override bool TryRebuildSolution() => Execute("build", null, [SolutionPath]).Item1 == 0;

    public override void RunTests()
    {
        Execute("test", null, []);
    }

    public override void RemoveProjectFromSolution(string projectPath)
    {
        TryRemoveProjectFromSolution(projectPath);
    }

    public override bool TryRemoveProjectFromSolution(string projectPath)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        return Execute(null, null,
        [
            "sln",
            $"\"{SolutionPath}\"",
            "remove",
            Path.Combine(pathToSolution, projectPath)
        ]).Item1 == 0;
    }

    public override void AddProjectToSolution(string projectPath, string? virtualFolderName = null)
    {
        TryAddProjectToSolution(projectPath, virtualFolderName);
    }

    public override bool TryAddProjectToSolution(string projectPath, string? virtualFolderName)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        if (virtualFolderName is null)
        {
            return Execute(null, null, [
                "sln",
                $"\"{SolutionPath}\"",
                "add",
                Path.Combine(pathToSolution, projectPath)
            ]).Item1 == 0;
        }
        else
        {
            return Execute(null, null, [
                "sln",
                $"\"{SolutionPath}\"",
                "add",
                "-s", virtualFolderName,
                Path.Combine(pathToSolution, projectPath)
            ]).Item1 == 0;
        }
    }

    public override bool AddNugetToProject(string nugetName, string? version = null, string? source = null,
        bool prerelease = false)
    {
        var globalSourcePath = ProjectSettings.GlobalizePath(source);
        var args = new List<string>();

        args.Add(nugetName);
        args.Add("--project");
        args.Add($"\"{GodotProjectPath}\"");
        if (version is not null)
        {
            args.Add("--version");
            args.Add(version);
        }

        if (source is not null)
        {
            args.Add("--source");
            args.Add($"\"{globalSourcePath}\"");
        }

        if (prerelease)
        {
            args.Add("--prerelease");
        }

        var result = Execute("package", "add", args.ToArray());

        var installSuccess = result.Item1 == 0;

        if (!installSuccess)
        {
            if (version is not null)
            {
                logger?.Error($"Installing {nugetName} v{version} failed!");
            }
            else
            {
                logger?.Error($"Installing {nugetName} failed!");
            }
        }

        return installSuccess;
    }

    public override void RemoveNugetFromProject(string nugetName)
    {
        TryRemoveNugetFromProject(nugetName);
    }

    public override bool TryRemoveNugetFromProject(string nugetName)
    {
        return Execute("package", "remove", [
            nugetName,
            "--project",
            $"\"{GodotProjectPath}\""
        ]).Item1 == 0;
    }

    public override void AddProjectReference(string projectReference)
    {
        TryAddProjectReference(projectReference);
    }

    public override bool TryAddProjectReference(string projectReference)
    {
        return Execute("reference", "add",
        [
            projectReference,
            "--project",
            GodotProjectPath,
        ]).Item1 == 0;
    }

    public override void RemoveProjectReference(string projectReference)
    {
        TryRemoveProjectReference(projectReference);
    }

    public override bool TryRemoveProjectReference(string projectReference)
    {
        return Execute("reference", "remove",
        [
            projectReference,
            "--project",
            GodotProjectPath,
        ]).Item1 == 0;
    }

    public override (int, string[]) Execute(string command, string[] args)
    {
        return ExecuteCall(command, args);
    }

    private (int, string[]) Execute(string? noun, string? verb, string[] args)
    {
        var allArgs = new List<string>();
        if (noun is not null)
        {
            allArgs.Add(noun);
        }

        if (verb is not null)
        {
            allArgs.Add(verb);
        }

        allArgs.AddRange(args);

        return ExecuteCall(CmdDotNet, allArgs.ToArray());
    }
}
#endif
