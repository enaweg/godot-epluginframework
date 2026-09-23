#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Builds a set of resources — autoloads, NuGet packages, projects and managed directories — that the
/// framework installs and uninstalls as a unit.
/// </summary>
/// <remarks>
/// This is the shared surface of every recipe. The root recipe of a plugin is built through
/// <see cref="IEEditorPluginBuilder"/>, which extends this interface with plugin dependency declarations.
/// The nested recipe of an optional plugin dependency
/// (<see cref="IEEditorPluginBuilder.AddOptionalPluginDependency"/>) is built through this interface alone —
/// nested recipes cannot declare further dependencies.
/// All methods return the builder itself to support a fluent call chain.
/// </remarks>
public interface IEEditorPluginRecipeBuilder
{
    /// <summary>
    /// Adds a Godot autoload singleton that is registered when the recipe is installed and
    /// removed when it is uninstalled.
    /// </summary>
    /// <param name="name">The global singleton name used to access it from scripts.</param>
    /// <param name="path">Resource path to the autoload scene or script (e.g. <c>res://addons/my-plugin/MyAutoload.tscn</c>).</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddAutoload(string name, string path);

    /// <summary>
    /// Adds a C# project to the solution, optionally also adding it as a project reference to
    /// the main Godot project.
    /// </summary>
    /// <param name="path">Path to the <c>.csproj</c> file.</param>
    /// <param name="addReference">
    /// When <see langword="true"/> (default), a project reference is added to the main
    /// Godot <c>.csproj</c> in addition to the solution entry.
    /// </param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddProject(string path, bool addReference = true);

    /// <summary>
    /// Adds a C# project to the solution inside an optional solution folder, optionally also
    /// adding it as a project reference to the main Godot project.
    /// </summary>
    /// <param name="path">Path to the <c>.csproj</c> file.</param>
    /// <param name="virtualFolderName">
    /// Solution folder to place the project under. When <see langword="null"/>, the project is
    /// added at the solution root.
    /// </param>
    /// <param name="addReference">
    /// When <see langword="true"/> (default), a project reference is added to the main
    /// Godot <c>.csproj</c> in addition to the solution entry.
    /// </param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddProject(string path, string? virtualFolderName = null, bool addReference = true);

    /// <summary>
    /// Adds one or more NuGet packages at their latest stable version to the main Godot project.
    /// </summary>
    /// <param name="nugetNames">Package IDs to install (e.g. <c>"Newtonsoft.Json"</c>).</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddNugets(params string[] nugetNames);

    /// <summary>
    /// Adds a NuGet package to the main Godot project with an optional version pin and feed source.
    /// </summary>
    /// <param name="nugetName">The package ID to install (e.g. <c>"Newtonsoft.Json"</c>).</param>
    /// <param name="version">
    /// Exact version to install. When <see langword="null"/>, the latest stable version is resolved.
    /// </param>
    /// <param name="source">
    /// Optional NuGet feed URL or local path. <c>res://</c>-relative paths are globalized
    /// automatically. When <see langword="null"/>, the configured feeds are used.
    /// </param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddNuget(string nugetName, string? version = null, string? source = null);

    /// <summary>
    /// Registers a directory whose visibility in Godot and IDEs is toggled with the recipe's
    /// installation state — shown when the recipe is installed, hidden when it is uninstalled.
    /// </summary>
    /// <param name="path">Path to the directory to manage.</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginRecipeBuilder AddDirectory(string path);
}
#endif
