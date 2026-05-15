namespace Enaweg.Plugin;

/// <summary>
/// Marker interface for types that require one-time initialization when the ePlugin
/// framework starts up.
/// </summary>
/// <remarks>
/// On startup the framework scans the executing assembly for all exported types that
/// implement <see cref="IInitialize"/>, instantiates each with
/// <see cref="Activator.CreateInstance"/>, and calls <see cref="Initialize"/> once with
/// the live <see cref="EPluginPlugin"/> node.
/// </remarks>
public interface IInitialize
{
    /// <summary>
    /// Called once during framework startup after all active plugin contexts have been
    /// discovered.
    /// </summary>
    /// <param name="ePlugin">The <see cref="EPluginPlugin"/> editor node.</param>
    void Initialize(EPluginPlugin ePlugin);
}