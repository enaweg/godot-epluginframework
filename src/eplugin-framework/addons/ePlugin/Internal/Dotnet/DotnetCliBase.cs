#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

internal abstract class DotnetCliBase : ExecuteCliBase, IDotnetCli, ICheckedDotnetCli
{
    /// <summary>The solution the CLI changes and builds.</summary>
    internal string SolutionPath { get; }

    /// <summary>
    /// The main Godot project: named after the assembly, else the only project in the project directory, else next to the
    /// solution and named like it.
    /// </summary>
    internal string GodotProjectPath { get; }

    /// <summary>The Godot project directory, <c>res://</c>.</summary>
    internal string ProjectRoot { get; }

    /// <summary>The project assembly's name, which Godot loads from <see cref="OutputDirectory"/>.</summary>
    internal string AssemblyName { get; }

    /// <summary>Where the editor loads the project assembly from.</summary>
    internal string OutputDirectory => Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug");

    protected override string LoggedProjectRoot => ProjectRoot;

    protected readonly ILogger? Logger;

    /// <param name="projectRoot">The Godot project directory, <see cref="IEditorHost.ProjectRoot"/>. Tests use a scratch project.</param>
    /// <param name="assemblyName">The project's <see cref="IEditorHost.AssemblyName"/>; null or empty to use its only solution.</param>
    protected DotnetCliBase(ILogger? logger, bool enableDebugLogging, string projectRoot, string? assemblyName = null, IProcessRunner? runner = null)
        : base(logger, enableDebugLogging, runner)
    {
        Logger = logger;
        ProjectRoot = Path.GetFullPath(projectRoot);
        assemblyName ??= "";
        var solutionName = $"{assemblyName}.sln";

        var expectedSolutionPath = Path.GetFullPath(Path.Combine(ProjectRoot, solutionName));
        // Godot's assembly name need not match the solution name (the sample project uses
        // "MyGame" and "My Game.sln", for example).
        var solutions = Directory.GetFiles(ProjectRoot, "*.sln");
        if (solutions.Length == 0)
        {
            solutions = Directory.GetFiles(ProjectRoot, "*.slnx");
        }
        SolutionPath = File.Exists(expectedSolutionPath) || solutions.Length != 1
            ? expectedSolutionPath
            : solutions[0];
        // Godot names the project after the assembly, also when the solution has another name.
        var expectedProjectPath = Path.Combine(ProjectRoot, $"{assemblyName}.csproj");
        GodotProjectPath = assemblyName.Length > 0 && File.Exists(expectedProjectPath) ? expectedProjectPath
            : Directory.GetFiles(ProjectRoot, "*.csproj") is [var only] ? only
            : Path.ChangeExtension(SolutionPath, ".csproj");
        AssemblyName = string.IsNullOrEmpty(assemblyName) ? Path.GetFileNameWithoutExtension(GodotProjectPath) : assemblyName;

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
    /// or absolute. The CLI is given absolute paths, so it does not depend on the editor's working directory; it writes
    /// project references relative to the project and removes them again by their absolute path.
    /// </summary>
    internal string ProjectFile(string path) =>
        Path.GetFullPath(Path.Combine(ProjectRoot,
            path.StartsWith("res://", System.StringComparison.Ordinal) ? path["res://".Length..] : path));

    /// <summary>
    /// Runs a non-incremental build. It cleans the outputs first, so <see cref="BuildOutputGuard"/> puts the editor's
    /// assembly back when the build fails.
    /// </summary>
    protected BuildOutcome BuildKeepingOutput(string[] args, bool keepNewAssembly = true) =>
        BuildOutputGuard.Run(OutputDirectory, Path.Combine(ProjectRoot, ".godot", "eplugin", "build-output"), AssemblyName + ".dll", Logger, () =>
        {
            var (exitCode, output) = Execute(args[0], args[1..]);
            return new BuildOutcome(exitCode, output);
        }, keepNewAssembly);

    /// <summary>
    /// A NuGet source as the CLI takes it: URLs unchanged, <c>user://</c> made absolute, and local paths like
    /// <see cref="ProjectFile"/>, relative to the project as <c>nuget.config</c> records them (<see cref="NugetConfigManager"/>).
    /// </summary>
    internal string NugetSource(string source) =>
        source.StartsWith("user://", System.StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(source)
        : source.StartsWith("res://", System.StringComparison.Ordinal) || !source.Contains("://", System.StringComparison.Ordinal) ? ProjectFile(source)
        : source;

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
    public abstract BuildOutcome TryBuild(bool keepNewAssembly = true);
    public abstract BuildOutcome TryBuildPluginsOnly(PluginsOnlyBuild build);
    public abstract bool TryRebuildSolution();
    public abstract bool TryAddProjectToSolution(string projectPath, string? virtualFolderName);
    public abstract bool TryRemoveProjectFromSolution(string projectPath);
    public abstract bool TryAddProjectReference(string projectReference);
    public abstract bool TryRemoveProjectReference(string projectReference);
    public abstract bool TryRemoveNugetFromProject(string nugetName);
}
#endif
