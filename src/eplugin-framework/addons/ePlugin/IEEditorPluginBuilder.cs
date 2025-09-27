#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Build up a Reciept for installing and uninstalling a editor plugin.
/// </summary>
public interface IEEditorPluginBuilder
{
    /// <summary>
    /// Adds a Godot autoload element. This is the same mechanism as simple C# and GDScript Plugins use.
    /// </summary>
    /// <param name="name">Name of the global element</param>
    /// <param name="path">Path to the element (`res://..`)</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddAutoload(string name, string path);

    /// <summary>
    /// Add a dependency to another plugin (C# or GDScript).
    /// </summary>
    /// <param name="pluginSlug">the directory name the plugin is in.</param>
    /// <param name="version">expected version of the plugin.</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddPluginDependency(string pluginSlug, string? version = null);

    /// <summary>
    /// Add a C# project to the solution and optionally to the main project.
    /// </summary>
    /// <param name="path">Path to the project file</param>
    /// <param name="addReference">if true the project will be added to the main project as reference.</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddProject(string path, bool addReference = true);
    
    /// <summary>
    /// Add a C# project to the solution with an optional virtual folder. Also, adds it optionally to the main project.
    /// </summary>
    /// <param name="path">Path tot he project file</param>
    /// <param name="virtualFolderName">Virtual folder name.</param>
    /// <param name="addReference">if true the project will be added to the main project as reference.</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddProject(string path, string? virtualFolderName = null, bool addReference = true);
    
    /// <summary>
    /// Add latest version of nuget packages to the main project.
    /// </summary>
    /// <param name="nugetNames">name of the nuget packages</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddNuget(params string[] nugetNames);
    
    /// <summary>
    /// Add a nuget package with an optionally provided version and source.
    /// </summary>
    /// <param name="nugetName">name of the nuget package</param>
    /// <param name="version">version of the nuget package</param>
    /// <param name="source">source to get nuget from</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddNuget(string nugetName, string? version = null, string? source = null);

    /// <summary>
    /// Adds a directory to be shown/hidden to Godot/IDEs as the plugin is activated or deactivated.
    /// </summary>
    /// <param name="path">path to the directory</param>
    /// <returns>The builder itself</returns>
    IEEditorPluginBuilder AddDirectory(string path);
}

#endif