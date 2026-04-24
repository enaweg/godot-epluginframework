#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Builds the installation/uninstallation recipe for an editor plugin.
/// </summary>
/// <remarks>
/// The builder is passed to <see cref="IEEditorPlugin.Bootstrap"/> and accumulates all
/// resources the plugin requires. The framework applies the resulting recipe when the plugin
/// is activated and reverses it when the plugin is deactivated.
/// All methods return the builder itself to support a fluent call chain.
/// </remarks>
public interface IEEditorPluginBuilder
{
    /// <summary>
    /// Adds a Godot autoload singleton that is registered when the plugin activates and
    /// removed when it deactivates.
    /// </summary>
    /// <param name="name">The global singleton name used to access it from scripts.</param>
    /// <param name="path">Resource path to the autoload scene or script (e.g. <c>res://addons/my-plugin/MyAutoload.tscn</c>).</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddAutoload(string name, string path);

    /// <summary>
    /// Declares a dependency on another plugin (C# or GDScript). The framework ensures the
    /// dependency is enabled and optionally at the required version before activating this plugin.
    /// </summary>
    /// <param name="pluginSlug">
    /// The directory name of the required plugin under <c>addons/</c>
    /// (e.g. <c>"my-dependency"</c> for <c>addons/my-dependency/</c>).
    /// </param>
    /// <param name="version">
    /// Optional version constraint. Use an exact version (<c>"1.2.3"</c>) or a minimum
    /// version with a <c>&gt;</c> prefix (<c>"&gt;1.2.0"</c>).
    /// When <see langword="null"/>, any installed version is accepted.
    /// </param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddPluginDependency(string pluginSlug, string? version = null);

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
    IEEditorPluginBuilder AddProject(string path, bool addReference = true);

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
    IEEditorPluginBuilder AddProject(string path, string? virtualFolderName = null, bool addReference = true);

    /// <summary>
    /// Adds one or more NuGet packages at their latest stable version to the main Godot project.
    /// </summary>
    /// <param name="nugetNames">Package IDs to install (e.g. <c>"Newtonsoft.Json"</c>).</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddNuget(params string[] nugetNames);

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
    IEEditorPluginBuilder AddNuget(string nugetName, string? version = null, string? source = null);

    /// <summary>
    /// Registers a directory whose visibility in Godot and IDEs is toggled with the plugin's
    /// activation state — shown when the plugin activates, hidden when it deactivates.
    /// </summary>
    /// <param name="path">Path to the directory to manage.</param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddDirectory(string path);
}

#endif