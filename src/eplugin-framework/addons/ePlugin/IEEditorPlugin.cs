#if TOOLS
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Base interface for ePlugin Framework-managed editor plugins.
/// </summary>
/// <remarks>
/// This was originally an abstract base class derived from <see cref="EditorPlugin"/>. Due to C# bugs in Godot
/// (4.5.x, 4.6.x), it was converted to an interface to avoid build issues.
/// <para>
/// Implement this interface on your <see cref="EditorPlugin"/> class and call
/// <c>this.EPluginService.Register()</c> / <c>Activate()</c> / <c>Deactivate()</c> from the corresponding
/// Godot lifecycle methods. The framework will then drive the install/uninstall pipeline
/// described in <see cref="Bootstrap"/>.
/// </para>
/// </remarks>
public interface IEEditorPlugin
{
    /// <summary>
    /// The underlying Godot <see cref="EditorPlugin"/> node.
    /// </summary>
    /// <remarks>
    /// Because direct C# inheritance from <see cref="EditorPlugin"/> caused build failures in
    /// Godot 4.5.x–4.6.x, this property gives the framework access to the Godot node without
    /// requiring a common base class.
    /// </remarks>
    public EditorPlugin GodotPlugin { get; }

    /// <summary>
    /// Declares the plugin's requirements so the framework can install and uninstall them
    /// automatically.
    /// </summary>
    /// <param name="builder">
    /// A builder that accumulates autoloads, NuGet packages, project references, solution
    /// projects, managed directories, and plugin dependencies for this plugin.
    /// </param>
    /// <remarks>
    /// <para>
    /// Called by the framework once during the <c>Created → Bootstrapped</c> state transition,
    /// and again when deactivating a plugin that was already active when the editor started
    /// (so the framework can reconstruct the recipe for cleanup).
    /// </para>
    /// <para>
    /// If this method throws, the plugin is moved to the <c>Error</c> state and disabled via
    /// <see cref="EditorInterface"/>.
    /// </para>
    /// </remarks>
    public void Bootstrap(IEEditorPluginBuilder builder);
}
#endif