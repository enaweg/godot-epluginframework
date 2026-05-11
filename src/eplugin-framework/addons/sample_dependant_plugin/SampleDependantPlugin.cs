#if TOOLS
using Godot;
using System;
using Enaweg.Plugin;

[Tool]
public partial class SampleDependantPlugin : EditorPlugin, IEEditorPlugin
{
    public EditorPlugin GodotPlugin => this;

    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder.AddPluginDependency("sample_plugin");
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
        this.EnableEPlugin();
    }

    public override void _DisablePlugin()
    {
        base._DisablePlugin();
        this.DisableEPlugin();
    }
}
#endif