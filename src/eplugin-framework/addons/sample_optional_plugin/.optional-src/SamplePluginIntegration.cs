namespace EPluginFramework.addons.sample_optional_plugin;

/// <summary>
/// Ships in the directory managed by the optional dependency on sample_plugin: it only becomes part
/// of the project when sample_plugin was enabled at the time this plugin was activated.
/// </summary>
public static class SamplePluginIntegration
{
    public static string Describe() => "sample_plugin was present, integration code is installed.";
}
