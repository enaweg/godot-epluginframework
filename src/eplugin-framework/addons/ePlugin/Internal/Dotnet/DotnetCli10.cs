#if TOOLS
using System.Collections.Generic;
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal sealed class DotnetCli10(ILogger logger) : DotnetCliBase(logger), IDotnetCli
{
    private const string CmdDotNet = "dotnet";

    public override void RebuildSolution()
    {
        Execute("build", null, []);
    }

    public override void RunTests()
    {
        Execute("test", null, []);
    }

    public override void RemoveProjectFromSolution(string projectPath)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        Execute(null, null,
        [
            "sln",
            SolutionPath,
            "remove",
            Path.Combine(pathToSolution, projectPath)
        ]);
    }

    public override void AddProjectToSolution(string projectPath, string virtualFolderName = null)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        if (virtualFolderName is null)
        {
            Execute(null, null, [
                "sln",
                SolutionPath,
                "add",
                Path.Combine(pathToSolution, projectPath)
            ]);
        }
        else
        {
            Execute(null, null, [
                "sln",
                SolutionPath,
                "add",
                "-s", virtualFolderName,
                Path.Combine(pathToSolution, projectPath)
            ]);
        }
    }

    public override bool AddNugetToProject(string nugetName, string version = null, string source = null,
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
                Logger.Error($"Installing {nugetName} v{version} failed!");
            }
            else
            {
                Logger.Error($"Installing {nugetName} failed!");
            }
        }

        return installSuccess;
    }

    public override void RemoveNugetFromProject(string nugetName)
    {
        Execute("package", "remove", [
            nugetName,
            "--project",
            GodotProjectPath
        ]);
    }

    public override void AddProjectReference(string projectReference)
    {
        Execute("add", null,
        [
            "add",
            GodotProjectPath,
            "reference",
            projectReference,
        ]);
    }

    public override void RemoveProjectReference(string projectReference)
    {
        Execute("remove", null,
        [
            "remove",
            GodotProjectPath,
            "reference",
            projectReference,
        ]);
    }

    public override (int, string[]) Execute(string command, string[] args)
    {
        return ExecuteCall(Logger, command, args);
    }

    private (int, string[]) Execute(string noun, string verb, string[] args)
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

        return ExecuteCall(Logger, CmdDotNet, allArgs.ToArray());
    }
}
#endif