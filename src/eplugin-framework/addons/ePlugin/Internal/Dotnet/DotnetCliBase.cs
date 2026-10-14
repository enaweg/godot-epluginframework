#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal abstract class DotnetCliBase : ExecuteCliBase, IDotnetCli, ICheckedDotnetCli
{
    /// <summary>The solution the CLI changes and builds.</summary>
    internal string SolutionPath { get; }

    /// <summary>The main Godot project, next to the solution and named like it.</summary>
    internal string GodotProjectPath { get; }

    protected readonly ILogger? Logger;

    protected DotnetCliBase(ILogger? logger, bool enableDebugLogging, IProcessRunner? runner = null)
        : base(logger, enableDebugLogging, runner)
    {
        Logger = logger;
        var pathToSolution = ProjectSettings.GlobalizePath("res://");
        var solutionName = $"{ProjectSettings.GetSetting("dotnet/project/assembly_name")}.sln";

        var expectedSolutionPath = Path.GetFullPath(Path.Combine(pathToSolution, solutionName));
        // Godot's assembly name need not match the solution name (the sample project uses
        // "MyGame" and "My Game.sln", for example).
        var solutions = Directory.GetFiles(pathToSolution, "*.sln");
        if (solutions.Length == 0)
        {
            solutions = Directory.GetFiles(pathToSolution, "*.slnx");
        }
        SolutionPath = File.Exists(expectedSolutionPath) || solutions.Length != 1
            ? expectedSolutionPath
            : solutions[0];
        GodotProjectPath = Path.ChangeExtension(SolutionPath, ".csproj");

        if (enableDebugLogging)
        {
            logger?.Log($"Using {this.GetType().Name} for dotnet commands.");
        }
    }

    /// <summary>
    /// Settles a package removal before the CLI runs, because the CLI fails to remove a package the project no longer
    /// references although that is the state the caller wants: false for an invalid package id, true (with a warning)
    /// when the project does not reference it, null to let the CLI remove it.
    /// </summary>
    protected bool? CheckPackageRemoval(string nugetName)
    {
        if (!ProjectReferences.IsValidPackageId(nugetName))
        {
            Logger?.Error($"Cannot remove NuGet '{nugetName}': it is not a valid package id.");
            return false;
        }

        if (ProjectReferences.ReferencesPackage(GodotProjectPath, nugetName) != false) return null;
        Logger?.Warn($"NuGet {nugetName} is not referenced by {Path.GetFileName(GodotProjectPath)}; nothing to remove.");
        return true;
    }

    /// <summary>Like <see cref="CheckPackageRemoval"/>, for a project reference given relative to <c>res://</c>.</summary>
    protected bool? CheckProjectReferenceRemoval(string projectReference)
    {
        if (!ProjectReferences.IsValidProjectPath(projectReference))
        {
            Logger?.Error($"Cannot remove project reference '{projectReference}': it is not a project file path.");
            return false;
        }

        if (ProjectReferences.ReferencesProject(GodotProjectPath, ProjectFile(projectReference)) != false) return null;
        Logger?.Warn($"{Path.GetFileName(GodotProjectPath)} does not reference {projectReference}; nothing to remove.");
        return true;
    }

    /// <summary>Like <see cref="CheckPackageRemoval"/>, for a solution entry given relative to <c>res://</c>.</summary>
    protected bool? CheckSolutionRemoval(string projectPath)
    {
        if (!ProjectReferences.IsValidProjectPath(projectPath))
        {
            Logger?.Error($"Cannot remove '{projectPath}' from the solution: it is not a project file path.");
            return false;
        }

        if (ProjectReferences.SolutionContains(SolutionPath, ProjectFile(projectPath)) != false) return null;
        Logger?.Warn($"{Path.GetFileName(SolutionPath)} does not contain {projectPath}; nothing to remove.");
        return true;
    }

    /// <summary>
    /// The absolute path of a project a recipe names: relative to the Godot project directory, as a <c>res://</c> path,
    /// or absolute.
    /// </summary>
    internal static string ProjectFile(string path) =>
        Path.GetFullPath(path.StartsWith("res://", System.StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : Path.Combine(ProjectSettings.GlobalizePath("res://"), path));

    /// <summary>
    /// A referenced project relative to the main project's directory, the form the CLI writes into the project and finds
    /// again when it is removed. The editor runs in that directory.
    /// </summary>
    internal string ReferencePath(string path) =>
        Path.GetRelativePath(Path.GetDirectoryName(GodotProjectPath)!, ProjectFile(path));

    /// <summary>A NuGet source as the CLI takes it: <c>res://</c> and <c>user://</c> paths made absolute, URLs unchanged.</summary>
    internal static string NugetSource(string source) => ProjectSettings.GlobalizePath(source);

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
    public abstract BuildOutcome TryBuild();
    public abstract bool TryRebuildSolution();
    public abstract bool TryAddProjectToSolution(string projectPath, string? virtualFolderName);
    public abstract bool TryRemoveProjectFromSolution(string projectPath);
    public abstract bool TryAddProjectReference(string projectReference);
    public abstract bool TryRemoveProjectReference(string projectReference);
    public abstract bool TryRemoveNugetFromProject(string nugetName);
}
#endif
