#if TOOLS
using System.Linq;
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Public entry point for code that needs to reach the running ePlugin framework instance.
/// </summary>
/// <remarks>
/// The framework is itself a Godot <see cref="EditorPlugin"/> (<see cref="EPluginPlugin"/>) loaded into the
/// editor's scene tree. This helper locates that node so callers can access framework-level services
/// (such as the global logger) without performing the scene-tree lookup themselves.
/// <para>
/// Only available in editor builds (the type is guarded by <c>#if TOOLS</c>).
/// </para>
/// </remarks>
[Tool]
public static class EPlugin
{
    private static EPluginPlugin? _instanceCache;

    /// <summary>
    /// Gets the singleton <see cref="EPluginPlugin"/> node hosting the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// The first access walks the editor's scene tree to find the <see cref="EPluginPlugin"/> child of
    /// the editor base control's parent; subsequent accesses return the cached reference.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown by <see cref="Enumerable.First{TSource}(System.Collections.Generic.IEnumerable{TSource}, System.Func{TSource, bool})"/>
    /// when the <c>ePlugin</c> editor plugin is not enabled and therefore no <see cref="EPluginPlugin"/>
    /// node exists in the editor's scene tree.
    /// </exception>
    public static EPluginPlugin Instance
    {
        get
        {
            _instanceCache ??= (EPluginPlugin)EditorInterface.Singleton.GetBaseControl().GetParent().GetChildren()
                .First(c => c is EPluginPlugin);

            return _instanceCache;
        }
    }
}
#endif