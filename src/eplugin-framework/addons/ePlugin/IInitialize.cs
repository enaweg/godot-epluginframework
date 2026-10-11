namespace Enaweg.Plugin;

/// <summary>
/// Marker interface for types that require one-time initialization when the ePlugin
/// framework starts up.
/// </summary>
/// <remarks>
/// Register an instance with <c>EPlugin.RegisterInitializer</c>, for example from a
/// <c>[ModuleInitializer]</c> so it is registered again after every assembly reload. The framework
/// calls <see cref="Initialize"/> once it has finished starting up. An <c>EditorPlugin</c> that
/// implements this interface is initialized when the framework first creates its context, without
/// registering it.
/// </remarks>
public interface IInitialize
{
    /// <summary>
    /// Called once during framework startup after all active plugin contexts have been
    /// discovered.
    /// </summary>
    /// <param name="ePlugin">The <see cref="IEPlugin"/> editor node.</param>
    void Initialize(IEPlugin ePlugin);
}