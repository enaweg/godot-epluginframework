#if TOOLS
using Godot;
using Enaweg.Plugin;

[Tool]
public partial class SampleOptionalPlugin : EditorPlugin, IEEditorPlugin
{
    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder
            // always installed while this plugin is active
            .AddDirectory($"{this.GetPluginDirectory()}/.src")

            // only installed when sample_plugin is already enabled and at least version 1.0.
            // sample_plugin is never enabled on our behalf: when it is missing or its version does
            // not match, the nested recipe is skipped and this plugin still activates normally.
            .AddOptionalPluginDependency("sample_plugin", ">1.0", optional => optional
                .AddDirectory($"{this.GetPluginDirectory()}/.optional-src"));
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
