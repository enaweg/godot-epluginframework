#if TOOLS
namespace Enaweg.Plugin.Internal;

internal sealed class IntegrationWrapper(IEEditorPlugin ePlugin) : IEEditorPluginService
{
    public void Activate()
    {
        EGlobal.Instance.EnsureEEditorPluginEnabled();
    }
}
#endif