#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Provides an abstraction over the <c>dotnet</c> CLI for managing the Godot project's solution,
/// NuGet packages, and project references from editor tooling.
/// </summary>
/// <remarks>
/// All operations target the solution and main <c>.csproj</c> derived from the Godot project's
/// <c>dotnet/project/assembly_name</c> setting. This interface is only available in editor builds.
/// </remarks>
public interface IDotnetCli
{
    /// <summary>
    /// Rebuilds the entire solution.
    /// </summary>
    void RebuildSolution();

    /// <summary>
    /// Runs all tests in the solution.
    /// </summary>
    void RunTests();

    /// <summary>
    /// Removes a project from the solution file.
    /// </summary>
    /// <param name="projectPath">
    /// Path to the <c>.csproj</c> file to remove, relative to the Godot project root (<c>res://</c>).
    /// </param>
    void RemoveProjectFromSolution(string projectPath);

    /// <summary>
    /// Adds a project to the solution file.
    /// </summary>
    /// <param name="projectPath">
    /// Path to the <c>.csproj</c> file to add, relative to the Godot project root (<c>res://</c>).
    /// </param>
    /// <param name="virtualFolderName">
    /// Optional solution folder to place the project under.
    /// When <see langword="null"/>, the project is added at the solution root.
    /// </param>
    void AddProjectToSolution(string projectPath, string? virtualFolderName = null);

    /// <summary>
    /// Adds a NuGet package to the Godot project.
    /// </summary>
    /// <param name="nugetName">The package ID to install (e.g. <c>Newtonsoft.Json</c>).</param>
    /// <param name="version">
    /// Exact version to install. When <see langword="null"/>, the latest stable version is resolved.
    /// </param>
    /// <param name="source">
    /// Optional NuGet feed URL or local path (<c>res://</c>-relative paths are globalized automatically).
    /// When <see langword="null"/>, the configured feeds are used.
    /// </param>
    /// <param name="prerelease">
    /// When <see langword="true"/>, allows pre-release versions to be selected.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the package was installed successfully;
    /// <see langword="false"/> otherwise. On failure an error is written to the logger.
    /// </returns>
    bool AddNugetToProject(string nugetName, string? version = null, string? source = null, bool prerelease = false);

    /// <summary>
    /// Removes a NuGet package from the Godot project.
    /// </summary>
    /// <param name="nugetName">The package ID to remove.</param>
    void RemoveNugetFromProject(string nugetName);

    /// <summary>
    /// Adds a project-to-project reference to the Godot project.
    /// </summary>
    /// <param name="projectReference">Path to the referenced <c>.csproj</c> file.</param>
    void AddProjectReference(string projectReference);

    /// <summary>
    /// Removes a project-to-project reference from the Godot project.
    /// </summary>
    /// <param name="projectReference">Path to the referenced <c>.csproj</c> file to remove.</param>
    void RemoveProjectReference(string projectReference);

    /// <summary>
    /// Executes an arbitrary <c>dotnet</c> sub-command directly.
    /// </summary>
    /// <param name="command">
    /// The dotnet sub-command to run (e.g. <c>"build"</c>, <c>"test"</c>).
    /// </param>
    /// <param name="args">Additional arguments appended after the command.</param>
    /// <returns>
    /// A tuple of <c>(exitCode, outputLines)</c> where <c>exitCode</c> is the process exit code
    /// and <c>outputLines</c> contains the captured standard output lines.
    /// </returns>
    public (int, string[]) Execute(string command, string[] args);
}
#endif