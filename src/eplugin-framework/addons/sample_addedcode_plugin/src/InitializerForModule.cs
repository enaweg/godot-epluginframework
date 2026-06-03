using Enaweg.Plugin;

namespace EPluginFramework.addons.sample_addedcode_plugin;

public class InitializerForModule : IInitialize
{
    public void Initialize(IEPlugin ePlugin)
    {
        ePlugin.Logger.Log($"Hello from the initializer of {this.GetType().FullName}!");
    }
}