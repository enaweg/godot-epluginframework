#if TOOLS
using System.IO;
using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// ePlugin Editor Plugin class. This represents the entry point for simple plugins.
/// </summary>
[Tool]
public abstract partial class EEditorPlugin : EEditorPluginBase
{
}

[Tool]
public abstract partial class EEditorPluginBase : EditorPlugin
{
    public string PluginSlug { get; private set; }

    public string PluginDirectory { get; private set; }

    public override void _EnterTree()
    {
        base._EnterTree();
        PluginDirectory = ((CSharpScript)GetScript()).GetPath().GetBaseDir();
        PluginSlug = Path.GetFileName(PluginDirectory);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
    }
}
#endif