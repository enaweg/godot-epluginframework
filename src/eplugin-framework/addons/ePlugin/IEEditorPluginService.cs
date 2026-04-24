#if TOOLS
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Mediates lifecycle calls between an <see cref="EditorPlugin"/> and the ePlugin Framework.
/// </summary>
/// <remarks>
/// Obtain an instance via the <c>EPluginService</c> extension property on
/// <see cref="IEEditorPlugin"/> and forward the corresponding Godot
/// <see cref="Godot.EditorPlugin"/> lifecycle methods to it.
/// </remarks>
public interface IEEditorPluginService
{
    /// <summary>
    /// Registers the plugin with the ePlugin Framework.
    /// </summary>
    /// <remarks>
    /// Ensures the ePlugin base autoload is enabled, then adds this plugin to the framework's
    /// internal registry and triggers the processing loop. Call this from
    /// <see cref="Godot.Node._EnterTree"/> or the equivalent Godot entry point.
    /// </remarks>
    void Register();

    /// <summary>
    /// Notifies the framework that the plugin has been activated by the editor.
    /// </summary>
    /// <remarks>
    /// Resets the plugin's state to allow the bootstrap and install pipeline to run
    /// (autoloads added, NuGet packages installed, project references added, etc.).
    /// Call this from <see cref="Godot.EditorPlugin._EnablePlugin"/>.
    /// </remarks>
    void Activate();

    /// <summary>
    /// Notifies the framework that the plugin is being deactivated by the editor.
    /// </summary>
    /// <remarks>
    /// Marks the plugin for uninstallation, which removes autoloads, hides managed directories,
    /// and removes project references and NuGet packages added during activation.
    /// Call this from <see cref="Godot.EditorPlugin._DisablePlugin"/>.
    /// </remarks>
    void Deactivate();
}
#endif