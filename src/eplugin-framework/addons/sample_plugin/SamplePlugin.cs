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

    public void Reinitialize(IEEditorPluginService service)
    {
        GD.Print("Reinitializing");
    }
}
#endif