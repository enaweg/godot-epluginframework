namespace EPluginFramework.addons.sample_optional_plugin;

/// <summary>
/// Ships in the always-managed directory of this plugin: available as soon as the plugin is
/// activated, gone again once it is deactivated.
/// </summary>
public static class SampleOptionalCore
{
    public static string Describe() => "sample_optional_plugin is active.";
}
