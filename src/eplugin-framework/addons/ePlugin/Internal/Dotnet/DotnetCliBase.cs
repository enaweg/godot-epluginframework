#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal abstract class DotnetCliBase : ExecuteCliBase, IDotnetCli, ICheckedDotnetCli
{
    protected readonly string SolutionPath;
    protected readonly string GodotProjectPath;

    protected DotnetCliBase(ILogger? logger, bool enableDebugLogging) : base(logger, enableDebugLogging)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");
        var solutionName = $"{ProjectSettings.GetSetting("dotnet/project/assembly_name")}.sln";
        var projectName = $"{ProjectSettings.GetSetting("dotnet/project/assembly_name")}.csproj";

        var expectedSolutionPath = Path.GetFullPath(Path.Combine(pathToSolution, solutionName));
        // Godot's assembly name need not match the solution name (the sample project uses
        // EPluginFramework and "EPlugin Framework.sln", for example).
        var solutions = Directory.GetFiles(pathToSolution, "*.sln");
        if (solutions.Length == 0)
        {
            solutions = Directory.GetFiles(pathToSolution, "*.slnx");
        }
        SolutionPath = File.Exists(expectedSolutionPath) || solutions.Length != 1
            ? expectedSolutionPath
            : solutions[0];
        GodotProjectPath = Path.GetFullPath(Path.Combine(pathToSolution, ProjectSettings.GlobalizePath(projectName)));

        if (enableDebugLogging)
        {
            logger?.Log($"Using {this.GetType().Name} for dotnet commands.");
        }
    }

    public abstract void RebuildSolution();
    public abstract void RunTests();
    public abstract void RemoveProjectFromSolution(string projectPath);
    public abstract void AddProjectToSolution(string projectPath, string? virtualFolderName = null);

    public abstract bool AddNugetToProject(string nugetName, string? version = null, string? source = null,
        bool prerelease = false);

    public abstract void RemoveNugetFromProject(string nugetName);
    public abstract void AddProjectReference(string projectReference);
    public abstract void RemoveProjectReference(string projectReference);
    public abstract (int, string[]) Execute(string command, string[] args);
    public abstract bool TryRebuildSolution();
    public abstract bool TryAddProjectToSolution(string projectPath, string? virtualFolderName);
    public abstract bool TryRemoveProjectFromSolution(string projectPath);
    public abstract bool TryAddProjectReference(string projectReference);
    public abstract bool TryRemoveProjectReference(string projectReference);
    public abstract bool TryRemoveNugetFromProject(string nugetName);
}
#endif
