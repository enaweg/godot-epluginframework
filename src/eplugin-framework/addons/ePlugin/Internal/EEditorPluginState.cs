#if TOOLS
namespace Enaweg.Plugin.Internal;

internal enum EEditorPluginState
{
    Created = 0,
    Activated = 100,
    Bootstrapped = 200,
    DeactivationRequested = 500,
    Deactivated = 510,
    Error = 9999
}
#endif