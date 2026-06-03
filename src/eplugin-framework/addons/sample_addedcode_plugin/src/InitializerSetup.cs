using System.Runtime.CompilerServices;
using Enaweg.Plugin;

namespace EPluginFramework.addons.sample_addedcode_plugin;

public static class InitializerSetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        EPlugin.RegisterInitializer(new InitializerForModule());
    }
}