#if TOOLS
using System.Linq;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public static class EPlugin
{
    private static EPluginPlugin? _instanceCache;

    public static EPluginPlugin Instance
    {
        get
        {
            _instanceCache ??= (EPluginPlugin)EditorInterface.Singleton.GetBaseControl().GetParent().GetChildren()
                .First(c => c is EPluginPlugin);

            return _instanceCache;
        }
    }
}
#endif