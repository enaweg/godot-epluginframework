#if TOOLS
using System;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// What every framework dialog does around its own content: preparing its windows for the editor, the ePlugin icon in
/// their title bars, its managed signal callbacks, opening web links of its texts and closing once. Shared by
/// composition, as the dialogs derive from different Godot window types. Godot calls only methods of the dialog itself,
/// which an assembly reload resolves on the reloaded script, so each dialog keeps one-line forwarders: its icon refresh
/// (named in <see cref="Prepare"/>) and its reload, dispose and exit hooks.
/// </summary>
internal sealed class DialogLifetime
{
    private Window? _dialog;
    private Window[] _windows = [];

    /// <summary>The dialog's managed signal callbacks, disconnected by <see cref="ReleaseCallbacks"/>.</summary>
    public EditorSignalConnections Signals { get; } = new();

    /// <summary>The dialog was closed with its decision, or released for an assembly reload; it decides nothing more.</summary>
    public bool IsDone { get; private set; }

    /// <summary>
    /// Prepares <paramref name="dialog"/> and the windows it opens itself for the editor (see
    /// <see cref="EditorWindows.Prepare"/>). <paramref name="refreshIcons"/> is the dialog's method that calls
    /// <see cref="RefreshIcons"/>; Godot runs it deferred whenever one of the windows is shown.
    /// </summary>
    public void Prepare(Window dialog, StringName refreshIcons, params Window[] windows)
    {
        _dialog = dialog;
        _windows = [dialog, .. windows];
        foreach (var window in _windows) EditorWindows.Prepare(window, Signals, dialog, refreshIcons);
    }

    /// <summary>Shows the ePlugin icon in the title bars of the prepared windows, see <see cref="EditorWindows.ApplyIcon"/>.</summary>
    public void RefreshIcons()
    {
        foreach (var window in _windows) EditorWindows.ApplyIcon(window);
    }

    /// <summary>Opens the web links of <paramref name="text"/> in the system browser; it opens no other links.</summary>
    public void OpenWebLinks(RichTextLabel text) =>
        Signals.Connect(text, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(meta => EditorWindows.OpenWebLink(meta.AsString())));

    /// <summary>
    /// Closes the dialog once, with <paramref name="decided"/>: it is hidden first, and released also when the decision
    /// throws, as a dialog left valid keeps others from opening. Later calls, e.g. Godot's canceled and close_requested
    /// signals for the same close, do nothing.
    /// </summary>
    public void Finish(Action? decided = null)
    {
        if (IsDone) return;
        IsDone = true;
        _dialog?.Hide();
        try { decided?.Invoke(); }
        finally { if (_dialog is not null) EditorWindows.Release(_dialog); }
    }

    /// <summary>
    /// The dialog goes with an assembly reload, without its decision: its buttons would decide nothing afterwards.
    /// Reload serializes every script before disposing any of them, so callback targets are still valid here.
    /// </summary>
    public void ReleaseForReload()
    {
        ReleaseCallbacks();
        if (IsDone) return;
        IsDone = true;
        if (_dialog is not null) EditorWindows.Release(_dialog);
    }

    /// <summary>Disconnects the managed signal callbacks; the dialog's dispose and exit hooks call it.</summary>
    public void ReleaseCallbacks() => Signals.Dispose();
}
#endif
