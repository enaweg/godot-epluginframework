#if TOOLS
using Enaweg.Plugin.Internal;
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Extension methods that bridge a Godot <see cref="EditorPlugin"/> to the ePlugin framework.
/// </summary>
[Tool]
public static class IEEditorPluginExtensions
{
    /// <summary>
    /// Installs the plugin into the Godot project via the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// Call this from <see cref="EditorPlugin._EnablePlugin"/> to trigger the framework's install
    /// pipeline. The framework ensures the <c>ePlugin</c> plugin itself is active, invokes
    /// <see cref="IEEditorPlugin.CreateRecipe"/> to build the install recipe, resolves declared
    /// plugin dependencies (enabling them first and verifying any version constraints), then
    /// applies the recipe — adding NuGet packages, solution projects, project references, managed
    /// directories, and autoload singletons. When all work is complete the editor's filesystem is
    /// rescanned and the solution is rebuilt.
    /// </remarks>
    public static void EnableEPlugin<TEPlugin>(this TEPlugin ePlugin) where TEPlugin : EditorPlugin, IEEditorPlugin
    {
        var context = EGlobal.Instance.GetOrCreateContext(ePlugin);
        EGlobal.Instance.EnableEPlugin(context);
    }

    /// <summary>
    /// Uninstalls the plugin from the Godot project via the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// Call this from <see cref="EditorPlugin._DisablePlugin"/> to trigger the framework's
    /// uninstall pipeline. The framework first disables every other active plugin that declared
    /// this one as a dependency, then reverses the install steps described by
    /// <see cref="IEEditorPlugin.CreateRecipe"/> — removing autoload singletons, hiding managed
    /// directories, dropping project references, removing solution projects, and uninstalling
    /// NuGet packages. When all work is complete the editor's filesystem is rescanned and the
    /// solution is rebuilt.
    /// </remarks>
    public static void DisableEPlugin<TEPlugin>(this TEPlugin ePlugin) where TEPlugin : EditorPlugin, IEEditorPlugin
    {
        var context = EGlobal.Instance.GetOrCreateContext(ePlugin);
        EGlobal.Instance.DisableEPlugin(context);
    }
}
#endif