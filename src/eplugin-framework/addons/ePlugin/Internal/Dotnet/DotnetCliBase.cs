#if TOOLS
using System.Collections.Generic;
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Runs the dotnet CLI for the Godot project and its solution. The CLI generations differ only in how packages and
/// project references are added and removed, so <see cref="DotnetCli9"/> and <see cref="DotnetCli10"/> give just those
/// argument lists.
/// </summary>
internal abstract class DotnetCliBase : ExecuteCliBase, IDotnetCli, ICheckedDotnetCli
{
    /// <summary>
    /// The solution the CLI changes and builds, in the solution directory: named after the assembly (<c>.sln</c>, else
    /// <c>.slnx</c>), else its only <c>.sln</c>, else its only <c>.slnx</c>, else the <c>.sln</c> named after the assembly.
    /// </summary>
    internal string SolutionPath { get; }

    /// <summary>
    /// The main Godot project, always in the project directory: named after the assembly, else the only project there,
    /// else named like the solution.
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
    /// <param name="solutionDirectory">The absolute <see cref="IEditorHost.SolutionDirectory"/>; null for the project directory.</param>
    protected DotnetCliBase(ILogger? logger, bool enableDebugLogging, string projectRoot, string? assemblyName = null, IProcessRunner? runner = null,
        string? solutionDirectory = null)
        : base(logger, enableDebugLogging, runner)
    {
        Logger = logger;
        ProjectRoot = Path.GetFullPath(projectRoot);
        assemblyName ??= "";
        SolutionPath = FindSolution(string.IsNullOrEmpty(solutionDirectory) ? ProjectRoot : Path.GetFullPath(solutionDirectory), assemblyName);
        // Godot names the project after the assembly, also when the solution has another name, and keeps it in res://
        // wherever the solution is.
        var expectedProjectPath = Path.Combine(ProjectRoot, $"{assemblyName}.csproj");
        GodotProjectPath = assemblyName.Length > 0 && File.Exists(expectedProjectPath) ? expectedProjectPath
            : Directory.GetFiles(ProjectRoot, "*.csproj") is [var only] ? only
            : Path.Combine(ProjectRoot, Path.GetFileNameWithoutExtension(SolutionPath) + ".csproj");
        AssemblyName = string.IsNullOrEmpty(assemblyName) ? Path.GetFileNameWithoutExtension(GodotProjectPath) : assemblyName;

        if (enableDebugLogging)
        {
            logger?.Log($"Using {this.GetType().Name} for dotnet commands.");
        }
    }

    /// <summary>See <see cref="SolutionPath"/>.</summary>
    private static string FindSolution(string directory, string assemblyName)
    {
        var expected = Path.Combine(directory, $"{assemblyName}.sln");
        if (assemblyName.Length > 0)
        {
            // GodotTools creates <assembly>.sln; a project migrated to the XML format has <assembly>.slnx instead.
            if (File.Exists(expected)) return expected;
            if (File.Exists(Path.ChangeExtension(expected, ".slnx"))) return Path.ChangeExtension(expected, ".slnx");
        }

        if (!Directory.Exists(directory)) return expected;
        // Godot's assembly name need not match the solution name (the sample project uses
        // "MyGame" and "My Game.sln", for example).
        var solutions = Directory.GetFiles(directory, "*.sln");
        if (solutions.Length == 0)
        {
            solutions = Directory.GetFiles(directory, "*.slnx");
        }

        return solutions is [var only] ? only : expected;
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
            Logger?.Error(LastFailure = $"Cannot remove NuGet '{nugetName}': it is not a valid package id.");
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
            Logger?.Error(LastFailure = $"Cannot remove project reference '{projectReference}': it is not a project file path.");
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
            Logger?.Error(LastFailure = $"Cannot remove '{projectPath}' from the solution: it is not a project file path.");
            return false;
        }

        if (ProjectReferences.SolutionContains(SolutionPath, ProjectFile(projectPath)) != false) return null;
        Logger?.Warn($"{Path.GetFileName(SolutionPath)} does not contain {projectPath}; nothing to remove.");
        return true;
    }

    /// <summary>
    /// After <paramref name="projectPath"/> was added to the solution or referenced (<paramref name="reference"/>), makes
    /// that entry build it as <c>Release</c> when the game is exported for release, unless the project optimizes
    /// <c>ExportRelease</c> itself (<see cref="ReleaseConfiguration"/>). The project was added either way, so a failure
    /// here is only a warning: the game still builds, with an unoptimized library.
    /// </summary>
    protected void MapExportRelease(string projectPath, bool reference)
    {
        var project = ProjectFile(projectPath);
        var (exitCode, output) = Execute("msbuild", ReleaseConfiguration.ProbeArguments(project));
        var mapped = exitCode == 0 && (ReleaseConfiguration.Optimizes(output) ||
            (reference ? ReleaseConfiguration.MapReference(GodotProjectPath, project) : ReleaseConfiguration.MapSolution(SolutionPath, project)));
        if (mapped) return;
        // the project was added, so its addition did not fail
        LastFailure = null;
        Logger?.Warn($"Cannot make {(reference ? Path.GetFileName(GodotProjectPath) : Path.GetFileName(SolutionPath))} build {projectPath} " +
                     $"as {ReleaseConfiguration.Release} in {ReleaseConfiguration.Export}; release exports will contain it unoptimized.");
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
    /// Local paths have no trailing separator: quoted for the command line, a path ending in a backslash would escape its
    /// closing quote on Windows.
    /// </summary>
    internal string NugetSource(string source) =>
        source.StartsWith("user://", System.StringComparison.Ordinal) ? Path.TrimEndingDirectorySeparator(ProjectSettings.GlobalizePath(source))
        : source.StartsWith("res://", System.StringComparison.Ordinal) || !source.Contains("://", System.StringComparison.Ordinal) ? Path.TrimEndingDirectorySeparator(ProjectFile(source))
        : source;

    /// <summary>The arguments of <c>dotnet</c> that add <paramref name="nugetName"/> to the Godot project; the options follow them.</summary>
    protected abstract string[] AddPackageArguments(string nugetName);

    /// <summary>The arguments of <c>dotnet</c> that remove <paramref name="nugetName"/> from the Godot project.</summary>
    protected abstract string[] RemovePackageArguments(string nugetName);

    /// <summary>The arguments of <c>dotnet</c> that make the Godot project reference <paramref name="projectFile"/>.</summary>
    /// <param name="projectFile">Absolute path of the referenced project, see <see cref="ProjectFile"/>.</param>
    protected abstract string[] AddReferenceArguments(string projectFile);

    /// <summary>The arguments of <c>dotnet</c> that remove the Godot project's reference to <paramref name="projectFile"/>.</summary>
    /// <param name="projectFile">Absolute path of the referenced project, see <see cref="ProjectFile"/>.</param>
    protected abstract string[] RemoveReferenceArguments(string projectFile);

    public void RebuildSolution()
    {
        TryRebuildSolution();
    }

    public bool TryRebuildSolution() => TryBuild().ExitCode == 0;

    public BuildOutcome TryBuild(bool keepNewAssembly = true) => BuildKeepingOutput(["build", SolutionPath, "--no-incremental"], keepNewAssembly);

    public BuildOutcome TryBuildPluginsOnly(PluginsOnlyBuild build)
    {
        build.WriteTargets(GodotProjectPath);
        var result = Execute(build.Arguments(GodotProjectPath));
        return new BuildOutcome(result.Item1, result.Item2);
    }

    public void RunTests()
    {
        Execute(["test", SolutionPath]);
    }

    public void RemoveProjectFromSolution(string projectPath)
    {
        TryRemoveProjectFromSolution(projectPath);
    }

    public bool TryRemoveProjectFromSolution(string projectPath)
    {
        if (CheckSolutionRemoval(projectPath) is { } settled) return settled;
        return Execute(["sln", SolutionPath, "remove", ProjectFile(projectPath)]).Item1 == 0;
    }

    public void AddProjectToSolution(string projectPath, string? virtualFolderName = null)
    {
        TryAddProjectToSolution(projectPath, virtualFolderName);
    }

    public bool TryAddProjectToSolution(string projectPath, string? virtualFolderName)
    {
        if (Execute(virtualFolderName is null
            ? ["sln", SolutionPath, "add", ProjectFile(projectPath)]
            : ["sln", SolutionPath, "add", "-s", virtualFolderName, ProjectFile(projectPath)]).Item1 != 0) return false;
        MapExportRelease(projectPath, reference: false);
        return true;
    }

    public bool AddNugetToProject(string nugetName, string? version = null, string? source = null,
        bool prerelease = false)
    {
        var args = new List<string>(AddPackageArguments(nugetName));
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

    public void RemoveNugetFromProject(string nugetName)
    {
        TryRemoveNugetFromProject(nugetName);
    }

    public bool TryRemoveNugetFromProject(string nugetName)
    {
        if (CheckPackageRemoval(nugetName) is { } settled) return settled;
        return Execute(RemovePackageArguments(nugetName)).Item1 == 0;
    }

    public void AddProjectReference(string projectReference)
    {
        TryAddProjectReference(projectReference);
    }

    public bool TryAddProjectReference(string projectReference)
    {
        if (Execute(AddReferenceArguments(ProjectFile(projectReference))).Item1 != 0) return false;
        MapExportRelease(projectReference, reference: true);
        return true;
    }

    public void RemoveProjectReference(string projectReference)
    {
        TryRemoveProjectReference(projectReference);
    }

    public bool TryRemoveProjectReference(string projectReference)
    {
        if (CheckProjectReferenceRemoval(projectReference) is { } settled) return settled;
        return Execute(RemoveReferenceArguments(ProjectFile(projectReference))).Item1 == 0;
    }

    public (int, string[]) Execute(string command, string[] args)
    {
        return Execute([command, ..args]);
    }

    private (int, string[]) Execute(string[] args)
    {
        return ExecuteCall(CmdDotNet, args);
    }
}
#endif
