#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal sealed class DotnetCli9(ILogger logger) : DotnetCliBase(logger), IDotnetCli
{
    private const string CmdDotNet = "dotnet";

    public override void RebuildSolution()
    {
        Execute(["build"]);
    }

    public override void RunTests()
    {
        Execute(["test"]);
    }

    public override void RemoveProjectFromSolution(string projectPath)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        Execute([
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
            Execute([
                "sln",
                SolutionPath,
                "add",
                Path.Combine(pathToSolution, projectPath)
            ]);
        }
        else
        {
            Execute([
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

        args.Add("add");
        args.Add(GodotProjectPath);
        args.Add("package");
        args.Add(nugetName);
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

        var result = Execute(args.ToArray());

        var installSuccess = result.Item1 == 0;

        if (!installSuccess)
        {
            if (version is not null)
            {
                Logger.Error($"Installing {nugetName} v{version} failed! {result.Item2.First()}");
            }
            else
            {
                Logger.Error($"Installing {nugetName} failed! {result.Item2.First()}");
            }
        }

        return installSuccess;
    }

    public override void RemoveNugetFromProject(string nugetName)
    {
        Execute([
            "remove",
            GodotProjectPath,
            "package",
            nugetName
        ]);
    }

    public override void AddProjectReference(string projectReference)
    {
        Execute([
            "add",
            GodotProjectPath,
            "reference",
            projectReference,
        ]);
    }

    public override void RemoveProjectReference(string projectReference)
    {
        Execute([
            "remove",
            GodotProjectPath,
            "reference",
            projectReference,
        ]);
    }

    public override (int, string[]) Execute(string command, string[] args)
    {
        return ExecuteCall(Logger, CmdDotNet, args);
    }

    private (int, string[]) Execute(string[] args)
    {
        return ExecuteCall(Logger, CmdDotNet, args);
    }
}
#endif