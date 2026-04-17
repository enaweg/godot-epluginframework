#if TOOLS
using Godot;
using Enaweg.Plugin;

[Tool]
public partial class SamplePlugin : EditorPlugin, IEEditorPlugin
{
    public EditorPlugin GodotPlugin => this;

    public void Bootstrap(IEEditorPluginBuilder builder)
    {
        builder.AddNuget("ZLogger");
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        this.EPluginService.Register();
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
        this.EPluginService.Activate();
    }

    public override void _DisablePlugin()
    {
        this.EPluginService.Deactivate();
        base._DisablePlugin();
    }
}
#endif