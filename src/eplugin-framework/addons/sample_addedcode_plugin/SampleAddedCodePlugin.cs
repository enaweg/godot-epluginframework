#if TOOLS
using Godot;
using Enaweg.Plugin;

[Tool]
public partial class SampleAddedCodePlugin : EditorPlugin, IEEditorPlugin
{
    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder.AddDirectory("res://addons/sample_addedcode_plugin/.src");
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