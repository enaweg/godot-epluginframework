#if TOOLS
using System;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Tells why ePlugin could not start, which otherwise only the Output panel shows, and offers to try again right away.
/// The toolbar button opens it while the framework is not running. The layout lives in StartupFailureDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class StartupFailureDialog : AcceptDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/StartupFailureDialog.tscn";
    private const string RetryAction = "retry";
    private const string CopyAction = "copy";
    private readonly EditorSignalConnections _signals = new();
    private Action? _retry;
    private bool _done;

    /// <param name="error">The exception's message, shown in the dialog.</param>
    /// <param name="details">Everything known about the failure, which the user can copy.</param>
    /// <param name="retry">Starts ePlugin again; not called when an assembly reload removes the dialog.</param>
    public static StartupFailureDialog Create(string error, string details, Action retry)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<StartupFailureDialog>();
        dialog.Initialize(error, details, retry);
        return dialog;
    }

    /// <summary>Opens above whatever exclusive editor window is open.</summary>
    public void Open() => EditorWindows.Popup(this);

    private void Initialize(string error, string details, Action retry)
    {
        _retry = retry;
        GetNode<Label>("%StartupMessage").Text = Message(error);
        GetNode<TextEdit>("%StartupDetails").Text = details;
        AddButton("Copy details", false, CopyAction);
        AddButton("Retry now", true, RetryAction);
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcon));
        _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(() => Finish(false)));
        _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(() => Finish(false)));
        _signals.Connect(this, AcceptDialog.SignalName.CustomAction, Callable.From<StringName>(action =>
        {
            if (action == CopyAction) DisplayServer.ClipboardSet(GetNode<TextEdit>("%StartupDetails").Text);
            else if (action == RetryAction) Finish(true);
        }));
    }

    internal static string Message(string error) => $"ePlugin could not start: {error}";

    private void Finish(bool retry)
    {
        if (_done) return;
        _done = true; Hide();
        try { if (retry) _retry?.Invoke(); }
        finally { EditorWindows.Release(this); }
    }

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize()
    {
        _signals.Dispose();
        if (_done) return;
        _done = true; EditorWindows.Release(this);
    }
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _signals.Dispose();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        _signals.Dispose();
        base._ExitTree();
    }
}
#endif
