#if TOOLS
namespace Enaweg.Plugin.Internal.Manager;

/// <summary>A plugin operation ends only when it needs no reload; a reload creates a new gate with the new dialog.</summary>
internal sealed class PluginOperationGate
{
    public bool IsRunning { get; private set; }
    public bool IsAwaitingReload { get; private set; }
    public bool IsBusy => IsRunning || IsAwaitingReload;

    public bool TryBegin()
    {
        if (IsBusy) return false;
        IsRunning = true;
        return true;
    }

    public void Complete(bool reloadPending)
    {
        if (!IsRunning) return;
        IsRunning = false;
        IsAwaitingReload = reloadPending;
    }
}
#endif
