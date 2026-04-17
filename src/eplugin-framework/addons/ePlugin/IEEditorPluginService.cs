namespace Enaweg.Plugin;

/// <summary>
/// Needed service calls between the plugin and EPlugin Framework.
/// </summary>
public interface IEEditorPluginService
{
    /// <summary>
    /// Registers the plugin with the ePlugin Framework.
    /// </summary>
    void Register();

    /// <summary>
    /// Activates the plugin.
    /// </summary>
    void Activate();

    /// <summary>
    /// Deactivates the plugin.
    /// </summary>
    void Deactivate();
}