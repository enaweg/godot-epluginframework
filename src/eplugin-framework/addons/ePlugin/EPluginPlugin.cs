#if TOOLS
using System.Runtime.Loader;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public partial class EPluginPlugin : EditorPlugin
{
    private static EPluginPlugin _instance;
    private static bool _shouldProcess = false;

    public EPluginPlugin()
    {
        _instance = this;
        AssemblyLoadContext.GetLoadContext(GetType().Assembly).Unloading += context =>
        {
            GD.PushWarning("Unloading Assembly");
        };
        GD.PushWarning("Created");
    }
    
    public override void _EnterTree()
    {
        base._EnterTree();
        _shouldProcess = true;
       
        GD.PushWarning("Enter Tree");
    }

    public override void _DisablePlugin()
    {
        base._DisablePlugin();
        GD.PushWarning("Disable Plugin");
    }

    public override void _ApplyChanges()
    {
        base._ApplyChanges();
        GD.PushWarning("Apply Changes");
    }

    public override void _Ready()
    {
        base._Ready();
        GD.PushWarning("Ready");
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
        GD.PushWarning("Enable Plugin");
    }

    public override void _ExitTree()
    {
        GD.PushWarning("Exit Tree");
        _shouldProcess = false;
        base._ExitTree();
        
    }

    public override void _SaveExternalData()
    {
        base._SaveExternalData();
        GD.PushWarning("Save External Data");
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        EPluginPlugin.GlobalProcessor();
    }


    private static void GlobalProcessor()
    {
        if (!_shouldProcess)
        {
            // GD.PushWarning("Not processing");
        }
    }
}
#endif