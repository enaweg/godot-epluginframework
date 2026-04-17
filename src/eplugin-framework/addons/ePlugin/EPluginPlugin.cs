#if TOOLS
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public sealed partial class EPluginPlugin : EditorPlugin
{
    public ILogger Logger
    {
        get
        {
            field ??= new GodotConsoleLogger(this.GetPluginSlug());

            return field;
        }
        set => field = value;
    } = null;

    public override void _EnterTree()
    {
        base._EnterTree();
        EGlobal.Instance.Initialize(new GenericLoggerFactory(category => new GodotConsoleLogger(category)));
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

    public override void _Process(double delta)
    {
        base._Process(delta);
        EGlobal.Instance.GlobalProcessor();
    }
}
#endif