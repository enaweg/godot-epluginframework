namespace Enaweg.Plugin;

/// <summary>
/// Marker interface for types that require one-time initialization when the ePlugin
/// framework starts up.
/// </summary>
/// <remarks>
/// Register an instance with <c>EPlugin.RegisterInitializer</c>, for example from a
/// <c>[ModuleInitializer]</c> so it is registered again after every assembly reload. The framework
/// calls <see cref="Initialize"/> once it has finished starting up, or right away when it is
/// registered later. An <c>EditorPlugin</c> that implements this interface is initialized the same
/// way once the framework has created its context, without registering it.
/// </remarks>
public interface IInitialize
{
    /// <summary>
    /// Called once the framework has finished starting up: after all active plugin contexts have
    /// been discovered and the plugins waiting for it, interrupted updates and license reviews have
    /// been resumed. A start that fails calls none of them; the start tried next calls each once.
    /// </summary>
    /// <param name="ePlugin">The <see cref="IEPlugin"/> editor node.</param>
    void Initialize(IEPlugin ePlugin);
}