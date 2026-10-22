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
    // Only project code failed: keeping is the expected answer, so it gets the focus.
    private bool _projectCode;
    private Button? _keep;

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
        _projectCode = journal.KeptForProjectCode;
        GetNode<Label>("%Summary").Text = string.Join("\n", journal.Plugins.Select(p => $"{p.Slug} {p.OldVersion} → {p.NewVersion}")) +
                                          (_projectCode
                                              ? "\nOnly the project's own code does not compile with the installed version; the plugins compile on their own." +
                                                "\nKeep it and adapt the project's code, then use Retry under Needs attention in the ePlugin Manager to finish the update."
                                              : "\nThe project does not compile with the installed version." +
                                                (journal.FailureCause == BuildFailureCause.PluginCode ? " The errors are in plugin code." : "") +
                                                "\nKeep it to fix the errors yourself, then use Retry under Needs attention in the ePlugin Manager to finish the update.");
        var log = AttentionItem.FailedBuildLog(journal);
        _log = log;
        GetNode<TextEdit>("%BuildOutput").Text = _projectCode && journal.ProjectErrors.Count > 0
            ? string.Join("\n", journal.ProjectErrors.Select(BuildDiagnostics.Format))
            : log is not null && File.Exists(log)
                ? string.Join("\n", File.ReadLines(log).Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(15))
                : journal.Failure ?? "Build failed.";
        var location = GetNode<Label>("%LogLocation");
        location.Text = "Rolling back restores the previous addon files and rebuilds.\n" + (log ?? journal.Directory);
        location.TooltipText = log ?? journal.Directory;
        _keep = AddButton("Keep new version", false, "keep"); AddButton("Open build log", true, "log");
        _signals.Connect(this, SignalName.Confirmed, Callable.From(Rollback));
        _signals.Connect(this, SignalName.Canceled, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CloseRequested, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CustomAction, Callable.From<StringName>(HandleCustomAction));
    }

    private void Rollback() => Choose(false);
    private void Choose(bool keep)
    {
        if (_decided) return;
        _decided = true; Hide(); _decide(keep); EditorWindows.Release(this);
    }
    private void HandleCustomAction(StringName action)
    {
        if (action == "keep") Choose(true);
        else if (action == "log" && _log is not null) OS.ShellOpen(_log);
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here. Its buttons
    // would decide nothing afterwards, so the dialog goes with the reload; the journal still awaits the decision, which
    // the reloaded framework asks for again.
    public void OnBeforeSerialize()
    {
        _signals.Dispose();
        if (_decided) return;
        _decided = true; EditorWindows.Release(this);
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

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    public void Open() { PopupCentered(); (_projectCode && _keep is not null ? _keep : GetOkButton()).GrabFocus(); }
}
#endif
