#if TOOLS
using Enaweg.Plugin.Internal;
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Convenience extensions on <see cref="IEEditorPlugin"/> for common dotnet CLI operations.
/// </summary>
/// <remarks>
/// These members are intended for use inside <see cref="IEEditorPlugin.Bootstrap"/> or other
/// editor-only code paths. They delegate to the shared <see cref="IDotnetCli"/> instance
/// obtained from the framework.
/// </remarks>
[Tool]
public static class IEEditorPluginExtensions
{
    /// <summary>
    /// Returns the shared <see cref="IDotnet"/> entry point to the dotnet CLI.
    /// </summary>
    /// <returns>
    /// An <see cref="IDotnet"/> instance whose <see cref="IDotnet.Call"/> property
    /// exposes the full <see cref="IDotnetCli"/> API.
    /// </returns>
    public static IDotnet Cli(this IEEditorPlugin ePlugin)
    {
        var context = EGlobal.Instance.GetOrCreateContext(ePlugin);

        var cli = EGlobal.Instance.GetCli(ePlugin);
        cli.UseLogger(context?.Logger);
        return cli;
    }

    /// <summary>
    /// Installs the plugin into the Godot project via the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// Call this from <see cref="Godot.EditorPlugin._EnablePlugin"/> to trigger the
    /// framework's install pipeline. The framework ensures the ePlugin plugin itself is
    /// active, and then runs the plugin's <see cref="EEditorPluginRecipe"/> — resolves
    /// declared dependencies (enabling them first when needed), registering autoloads,
    /// copying assets, and performing any other install steps.
    /// </remarks>
    public static void EnableEPlugin(this IEEditorPlugin ePlugin)
    {
        var context = EGlobal.Instance.GetOrCreateContext(ePlugin);
        EGlobal.Instance.EnableEPlugin(context);
    }

    /// <summary>
    /// Uninstalls the plugin from the Godot project via the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// Call this from <see cref="Godot.EditorPlugin._DisablePlugin"/> to trigger the
    /// framework's uninstall pipeline. The framework runs the reverse of the install
    /// steps defined in the plugin's <see cref="EEditorPluginRecipe"/> — removing
    /// autoloads and cleaning up any other registered resources. It will also Disable
    /// all plugins that depend on this plugin.
    /// </remarks>
    public static void DisableEPlugin(this IEEditorPlugin ePlugin)
    {
        var context = EGlobal.Instance.GetOrCreateContext(ePlugin);
        EGlobal.Instance.DisableEPlugin(context);
    }

    /// <summary>
    /// Adds a NuGet package to the main Godot project.
    /// </summary>
    /// <param name="nugetName">The package ID to install.</param>
    /// <param name="version">
    /// Exact version to install. When <see langword="null"/>, the latest stable version
    /// is resolved.
    /// </param>
    /// <param name="source">
    /// Optional NuGet feed URL or local path. When <see langword="null"/>, the
    /// configured feeds are used.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the package was installed successfully;
    /// <see langword="false"/> otherwise.
    /// </returns>
    public static bool AddNuget(this IEEditorPlugin ePlugin, string nugetName, string? version = null,
        string? source = null)
    {
        return ePlugin.Cli().Call.AddNugetToProject(nugetName, version, source);
    }

    /// <summary>
    /// Removes one or more NuGet packages from the main Godot project.
    /// </summary>
    /// <param name="nugetNames">Package IDs to remove.</param>
    public static void RemoveNuget(this IEEditorPlugin ePlugin, params string[] nugetNames)
    {
        foreach (var nugetName in nugetNames)
        {
            ePlugin.Cli().Call.RemoveNugetFromProject(nugetName);
        }
    }

    /// <summary>
    /// Adds a C# project to the solution and optionally as a reference to the main
    /// Godot project.
    /// </summary>
    /// <param name="projectPath">Path to the <c>.csproj</c> file.</param>
    /// <param name="virtualFolderName">
    /// Solution folder to place the project under. When <see langword="null"/>, the
    /// project is added at the solution root.
    /// </param>
    /// <param name="addReference">
    /// When <see langword="true"/> (default), a project reference is also added to the
    /// main Godot <c>.csproj</c>.
    /// </param>
    public static void AddProject(this IEEditorPlugin ePlugin, string projectPath, string? virtualFolderName = null,
        bool addReference = true)
    {
        ePlugin.Cli().Call.AddProjectToSolution(projectPath, virtualFolderName);
        if (addReference)
        {
            ePlugin.Cli().Call.AddProjectReference(projectPath);
        }
    }

    /// <summary>
    /// Removes one or more C# projects from the solution and removes their project
    /// references from the main Godot project.
    /// </summary>
    /// <param name="projectPaths">Paths to the <c>.csproj</c> files to remove.</param>
    public static void RemoveProject(this IEEditorPlugin ePlugin, params string[] projectPaths)
    {
        foreach (var projectPath in projectPaths)
        {
            ePlugin.Cli().Call.RemoveProjectReference(projectPath);
            ePlugin.Cli().Call.RemoveProjectFromSolution(projectPath);
        }
    }

    /// <summary>
    /// Adds one or more project-to-project references to the main Godot project without
    /// touching the solution file.
    /// </summary>
    /// <param name="projectReference">Paths to the <c>.csproj</c> files to reference.</param>
    public static void AddProjectReference(this IEEditorPlugin ePlugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            ePlugin.Cli().Call.AddProjectReference(reference);
        }
    }

    /// <summary>
    /// Removes one or more project-to-project references from the main Godot project
    /// without touching the solution file.
    /// </summary>
    /// <param name="projectReference">Paths to the <c>.csproj</c> files to dereference.</param>
    public static void RemoveProjectReference(this IEEditorPlugin ePlugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            ePlugin.Cli().Call.RemoveProjectReference(reference);
        }
    }

    /// <summary>
    /// Rebuilds the entire solution.
    /// </summary>
    public static void RebuildAll(this IEEditorPlugin ePlugin)
    {
        ePlugin.Cli().Call.RebuildSolution();
    }
}
#endif