#if TOOLS
using Godot;
using System;
using Enaweg.Plugin;
using Enaweg.Plugin.Internal;

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
        EGlobal.Instance.EnsureEEditorPluginEnabled();
        EGlobal.Instance.Register(this);
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
        EGlobal.Instance.Activate(this);
    }

    public override void _DisablePlugin()
    {
        EGlobal.Instance.Deactivate(this);
        base._DisablePlugin();
    }
}
#endif