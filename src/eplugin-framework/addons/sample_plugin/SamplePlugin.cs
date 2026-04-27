#if TOOLS
using Godot;
using Enaweg.Plugin;

namespace Enaweg.Plugin.Sample;

[Tool]
public partial class SamplePlugin : EditorPlugin, IEEditorPlugin
{
    public EditorPlugin GodotPlugin => this;

    public void Bootstrap(IEEditorPluginBuilder builder)
    {
        builder.AddNuget("ZLogger");
    }

    public void Reinitialize()
    {
        GD.Print("Reinitializing");
    }
}
#endif