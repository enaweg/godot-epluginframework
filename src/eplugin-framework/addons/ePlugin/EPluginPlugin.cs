#if TOOLS
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public partial class EPluginPlugin : EEditorPluginBase
{
    protected override ILogger InitializeLogging()
    {
        return new GodotConsoleLogger(PluginSlug);
    }

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

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
    }

    public override void _DisablePlugin()
    {
        EGlobal.Instance.DeactivateAllEEditorPlugins();
        base._DisablePlugin();
    }

    public override void _ExitTree()
    {
        EGlobal.Instance.StopProcessing();
        base._ExitTree();
    }
}
#endif