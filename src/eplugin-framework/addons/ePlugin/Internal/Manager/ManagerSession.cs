#if TOOLS
namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The ePlugin Manager's busy state: which of its work runs, whether it waits for the editor to load a build, and what it
/// does once it is idle again. Pure, so its rules are tested without the editor: the manager asks whether plugins change
/// and whether a reload is due, and passes that in. A reload creates a new session with the new dialog.
/// </summary>
internal sealed class ManagerSession
{
    /// <summary>The plugin operation, which locks the manager until it ended and, when it built, until the reload.</summary>
    public PluginOperationGate Operation { get; } = new();

    /// <summary>
    /// The manager's own work runs: checking, reading a ZIP file, staging or installing, or a plugin operation until its
    /// work ended (dependencies may still change afterwards).
    /// </summary>
    public bool Working { get; set; }

    /// <summary>An installation downloads or waits for a review, which Cancel stops; otherwise the dialog has no Close button.</summary>
    public bool Installing { get; set; }

    /// <summary>An installation replaces addon files; it can no longer be canceled.</summary>
    public bool Swapping { get; set; }

    /// <summary>
    /// A build of this assembly waits for Godot's hot reload (see <see cref="AssemblyReload"/>), as last asked; the manager
    /// renders again when that changes.
    /// </summary>
    public bool ReloadPending { get; set; }

    /// <summary>The local index or a setting changed while plugins were changing or work ran; the list is read again afterwards.</summary>
    public bool RefreshWhenIdle { get; set; }

    /// <summary>Whether the operation that ended while plugins were still changing reads the list again once they are done.</summary>
    public bool RefreshOnCompletion { get; set; } = true;

    /// <summary>
    /// The notice of work that ended while the manager was closed: Escape and the window's close button hide it also while
    /// its work goes on, so the next open shows how it ended instead of dropping it.
    /// </summary>
    public bool KeepNotice { get; set; }

    /// <summary>
    /// Nothing may start: work runs, a plugin operation or the reload it built is not done, plugins change, or a reload is
    /// due, which a plugin operation would run on the old code.
    /// </summary>
    public bool IsBusy(bool changingPlugins, bool reloadPending) => Working || Operation.IsBusy || changingPlugins || reloadPending;

    /// <summary>
    /// The dialog's native input is locked: set before a plugin operation is queued, it stays locked while the C# state is
    /// reloaded. Checking or staging alone leaves it open, so they can be canceled.
    /// </summary>
    public bool LocksInput(bool changingPlugins, bool reloadPending) => Operation.IsBusy || changingPlugins || reloadPending;

    /// <summary>Closing the manager (Escape, its close or Cancel button) is possible: nothing is replaced or locked.</summary>
    public bool CanClose(bool changingPlugins, bool reloadPending) => !Swapping && !LocksInput(changingPlugins, reloadPending);

    /// <summary>The Cancel button is shown while an installation can still be canceled.</summary>
    public bool ShowsCancel => Installing && !Swapping;

    /// <summary>A plugin operation's work ended and plugins stopped changing, so it can be completed.</summary>
    public bool CanCompleteOperation(bool changingPlugins) => Operation.IsRunning && !Working && !changingPlugins;

    /// <summary>The list waits to be read again, and nothing locks it any more.</summary>
    public bool ShouldRefresh(bool locked) => RefreshWhenIdle && !locked && !Working;

    /// <summary>The work ended, whichever stage it reached.</summary>
    public void EndWork() => Working = Installing = Swapping = false;
}
#endif
