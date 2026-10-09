#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Asks whether to keep an update the project no longer builds with or to roll it back. The layout lives in
/// UpdateFailureDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class UpdateFailureDialog : AcceptDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/UpdateFailureDialog.tscn";
    private readonly EditorSignalConnections _signals = new();
    private bool _decided;
    private Action<bool> _decide = null!;
    private string? _log;

    public static UpdateFailureDialog Create(UpdateJournal journal, Action<bool> decide)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<UpdateFailureDialog>();
        dialog.Initialize(journal, decide);
        return dialog;
    }

    private void Initialize(UpdateJournal journal, Action<bool> decide)
    {
        _decide = decide;
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcon));
        GetNode<Label>("%Summary").Text = string.Join("\n", journal.Plugins.Select(p => $"{p.Slug} {p.OldVersion} → {p.NewVersion}")) +
                                          "\nThe project does not compile with the installed version.";
        var logName = journal.Builds.LastOrDefault()?.LogFile;
        var log = logName is not null ? PackageFiles.Inside(journal.Directory, logName) : null;
        _log = log;
        GetNode<TextEdit>("%BuildOutput").Text = log is not null && File.Exists(log)
            ? string.Join("\n", File.ReadLines(log).Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(15))
            : journal.Failure ?? "Build failed.";
        var location = GetNode<Label>("%LogLocation");
        location.Text = "Rolling back restores the previous addon files and rebuilds.\n" + (log ?? journal.Directory);
        location.TooltipText = log ?? journal.Directory;
        AddButton("Keep new version", false, "keep"); AddButton("Open build log", true, "log");
        _signals.Connect(this, SignalName.Confirmed, Callable.From(Rollback));
        _signals.Connect(this, SignalName.Canceled, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CloseRequested, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CustomAction, Callable.From<StringName>(HandleCustomAction));
    }

    private void Rollback() => Choose(false);
    private void Choose(bool keep)
    {
        if (_decided) return;
        _decided = true; Hide(); _decide(keep); QueueFree();
    }
    private void HandleCustomAction(StringName action)
    {
        if (action == "keep") Choose(true);
        else if (action == "log" && _log is not null) OS.ShellOpen(_log);
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize() => _signals.Dispose();
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

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    public void Open() { PopupCentered(); GetOkButton().GrabFocus(); }
}
#endif
