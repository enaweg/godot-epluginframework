#if TOOLS
using Godot;

namespace Enaweg.Plugin.Sample;

[Tool]
public partial class SamplePlugin : EditorPlugin, IEEditorPlugin
{
    public EditorPlugin GodotPlugin => this;

    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder.AddNuget("ZLogger");
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