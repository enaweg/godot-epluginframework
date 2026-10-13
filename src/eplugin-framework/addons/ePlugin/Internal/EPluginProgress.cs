#if TOOLS
namespace Enaweg.Plugin.Internal;

/// <summary>Public progress feedback using the existing scope, heartbeat and helper recovery machinery.</summary>
internal sealed class EPluginProgress : IEPluginProgress
{
    public IEPluginProgressScope Begin(string text) => ActivationProgress.Begin(text, hideSettingsDialog: false);
}
#endif
