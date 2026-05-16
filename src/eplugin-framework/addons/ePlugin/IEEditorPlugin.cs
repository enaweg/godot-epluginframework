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
/// Implement this interface on your <see cref="EditorPlugin"/> class and, from the corresponding Godot
/// lifecycle methods, call the extension methods
/// <see cref="IEEditorPluginExtensions.EnableEPlugin{TEPlugin}"/> from
/// <see cref="EditorPlugin._EnablePlugin"/> and
/// <see cref="IEEditorPluginExtensions.DisableEPlugin{TEPlugin}"/> from
/// <see cref="EditorPlugin._DisablePlugin"/>. The framework will then drive the install/uninstall pipeline
/// described in <see cref="CreateRecipe"/>.
/// </para>
/// </remarks>
public interface IEEditorPlugin
{
    /// <summary>
    /// Declares the plugin's requirements so the framework can install and uninstall them
    /// automatically.
    /// </summary>
    /// <param name="builder">
    /// A builder that accumulates autoloads, NuGet packages, project references, solution
    /// projects, managed directories, and plugin dependencies for this plugin.
    /// </param>
    /// <remarks>
    /// Called by the framework when the recipe is needed — typically the first time the plugin is
    /// enabled or disabled in the current editor session. The framework also re-invokes this when
    /// deactivating an already-active plugin so the cleanup steps can be derived. Implementations
    /// should be deterministic and side-effect free: just add items to <paramref name="builder"/>.
    /// </remarks>
    public void CreateRecipe(IEEditorPluginBuilder builder);
}
#endif