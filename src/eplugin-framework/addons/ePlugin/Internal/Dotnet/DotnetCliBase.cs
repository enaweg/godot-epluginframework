#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal abstract class DotnetCliBase : ExecuteCliBase, IDotnetCli
{
    protected readonly string SolutionPath;
    protected readonly string GodotProjectPath;
    protected ILogger Logger;

    protected DotnetCliBase(ILogger logger)
    {
        Logger = logger;

        var pathToSolution = ProjectSettings.GlobalizePath("res://");
        var solutionName = $"{ProjectSettings.GetSetting("dotnet/project/assembly_name")}.sln";
        var projectName = $"{ProjectSettings.GetSetting("dotnet/project/assembly_name")}.csproj";

        SolutionPath = Path.Combine(pathToSolution, solutionName);
        GodotProjectPath = Path.Combine(pathToSolution, projectName);
    }

    public void UseLogger(ILogger logger)
    {
        Logger = logger;
    }

    public abstract void RebuildSolution();
    public abstract void RunTests();
    public abstract void RemoveProjectFromSolution(string projectPath);
    public abstract void AddProjectToSolution(string projectPath, string virtualFolderName = null);

    public abstract bool AddNugetToProject(string nugetName, string version = null, string source = null,
        bool prerelease = false);

    public abstract void RemoveNugetFromProject(string nugetName);
    public abstract void AddProjectReference(string projectReference);
    public abstract void RemoveProjectReference(string projectReference);
    public abstract (int, string[]) Execute(string command, string[] args);
}
#endif