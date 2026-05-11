#if TOOLS
namespace Enaweg.Plugin.Internal;

internal enum EEditorPluginState
{
    Created = 0,
    Activated = 100,
    Deactivated = 500,
    Error = 9999
}
#endif