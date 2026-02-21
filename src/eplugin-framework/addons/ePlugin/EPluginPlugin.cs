#if TOOLS
using System.Runtime.Loader;
using Godot;

namespace Enaweg.Plugin;

public partial class EEditorPluginBase : EditorPlugin
{
    
}


[Tool]
public partial class EPluginPlugin : EEditorPluginBase
{
}
#endif