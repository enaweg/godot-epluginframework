#if TOOLS
using Enaweg.Plugin.Internal;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public partial class EPluginPlugin : EditorPlugin
{

    public override void _EnterTree()
    {
        base._EnterTree();
        EGlobal.Instance.StartProcessing(this);
    }

    public override void _Ready()
    {
        base._Ready();
        EGlobal.Instance.StartProcessing(this);
    }
    
    public override void _ExitTree()
    {
        EGlobal.Instance.StopProcessing();
        base._ExitTree();
    }
}
#endif