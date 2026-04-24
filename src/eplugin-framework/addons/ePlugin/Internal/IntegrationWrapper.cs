#if TOOLS
namespace Enaweg.Plugin.Internal;

internal sealed class IntegrationWrapper(IEEditorPlugin ePlugin) : IEEditorPluginService
{
    public void Register()
    {
        EGlobal.Instance.EnsureEEditorPluginEnabled();
        EGlobal.Instance.Register(ePlugin);
    }

    public void Activate()
    {
        EGlobal.Instance.Activate(ePlugin);
    }

    public void Deactivate()
    {
        EGlobal.Instance.Deactivate(ePlugin);
    }
}
#endif