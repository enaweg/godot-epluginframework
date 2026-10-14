#if TOOLS
using System.Linq;
using Enaweg.Plugin.Internal;
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
    private static IEPlugin? _instanceCache;

    /// <summary>Gets the progress service for custom C# editor operations.</summary>
    /// <remarks>
    /// Available without looking up the framework's editor node. Begin on the editor thread to allow the native
    /// helper to start, then report from any thread. A missing or failing helper leaves the operation running without
    /// a window. Scopes leave Project Settings visible and must be disposed when their work ends.
    /// </remarks>
    public static IEPluginProgress Progress { get; } = new EPluginProgress();

    /// <summary>
    /// Gets the singleton <see cref="IEPlugin"/> node hosting the ePlugin framework.
    /// </summary>
    /// <remarks>
    /// The first access walks the editor's scene tree to find the <see cref="EPluginPlugin"/> child of
    /// the editor base control's parent; subsequent accesses return the cached reference.
    /// </remarks>
    public static IEPlugin? Instance
    {
        get
        {
            _instanceCache ??= (IEPlugin?)(EditorInterface.Singleton.GetBaseControl().GetParent().GetChildren()
                .FirstOrDefault(c => c is IEPlugin));

            return _instanceCache;
        }
    }

    /// <summary>
    /// Queues an initializer whose <see cref="IInitialize.Initialize"/> is called once the framework has finished
    /// starting up.
    /// </summary>
    /// <param name="initializer">The initializer to call.</param>
    public static void RegisterInitializer(IInitialize initializer)
    {
        EGlobal.Instance.AddInitializer(initializer);
    }
}
#endif
