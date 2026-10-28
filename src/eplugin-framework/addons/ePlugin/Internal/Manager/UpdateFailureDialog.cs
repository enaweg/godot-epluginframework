#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        var log = AttentionItem.FailedBuildLog(journal);
        _log = log;
        var errors = Errors(journal, log);
        GetNode<Label>("%Summary").Text = Summary(journal, errors);
        GetNode<TextEdit>("%BuildOutput").Text = errors.Count > 0 ? string.Join("\n", errors.Select(BuildDiagnostics.Format)) : journal.Failure ?? "Build failed.";
        var location = GetNode<Label>("%LogLocation");
        location.Text = "Rolling back restores the previous addon files and rebuilds.\n" + (log ?? journal.Directory);
        location.TooltipText = log ?? journal.Directory;
        _keep = AddButton("Keep new version", false, "keep"); AddButton("Open build log", true, "log");
        _signals.Connect(this, SignalName.Confirmed, Callable.From(Rollback));
        _signals.Connect(this, SignalName.Canceled, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CloseRequested, Callable.From(Rollback));
        _signals.Connect(this, SignalName.CustomAction, Callable.From<StringName>(HandleCustomAction));
    }

    /// <summary>
    /// The errors of the failed build: the project's own when only they failed, otherwise those the build log names; empty
    /// when it names none (then the journal's failure is shown).
    /// </summary>
    internal static IReadOnlyList<BuildError> Errors(UpdateJournal journal, string? log)
    {
        if (journal.KeptForProjectCode && journal.ProjectErrors.Count > 0) return journal.ProjectErrors;
        if (log is null) return [];
        try { return File.Exists(log) ? BuildDiagnostics.Parse(File.ReadLines(log)).Take(BuildDiagnostics.MaxProjectErrors).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The versions, where the errors are and what to do about them; closing the dialog rolls back, as Godot's close button
    /// and Escape cannot keep a version that does not build.
    /// </summary>
    internal static string Summary(UpdateJournal journal, IReadOnlyList<BuildError> errors)
    {
        var text = new StringBuilder(string.Join("\n", journal.Plugins.Select(p => $"{p.Slug} {p.OldVersion} → {p.NewVersion}")));
        if (journal.KeptForProjectCode)
        {
            text.Append("\nOnly the project's own code does not compile with the installed version; the plugins compile on their own.")
                .Append("\nKeep it and adapt the project's code, then use Retry under Needs attention in the ePlugin Manager to finish the update.");
        }
        else
        {
            text.Append("\nThe project does not compile with the installed version.");
            if (journal.FailureCause == BuildFailureCause.PluginCode) text.Append(" The errors are in plugin code.");
            if (errors.Any(e => e.Code.Equals("NU1301", StringComparison.OrdinalIgnoreCase)))
                text.Append("\nNuGet could not reach a package source (NU1301). Check your network connection, then retry: roll back and update again, " +
                            "or keep it and use Retry under Needs attention in the ePlugin Manager.");
            else if (journal.FailureCause == BuildFailureCause.Transient)
                text.Append("\nThe errors may pass on another attempt, e.g. once another program releases a locked file. Retry: roll back and " +
                            "update again, or keep it and use Retry under Needs attention in the ePlugin Manager.");
            else text.Append("\nKeep it to fix the errors yourself, then use Retry under Needs attention in the ePlugin Manager to finish the update.");
        }
        return text.Append("\nClosing this dialog rolls back.").ToString();
    }

    private void Rollback() => Choose(false);
    private void Choose(bool keep)
    {
        if (_decided) return;
        _decided = true; Hide();
        // released also when the decision throws: a dialog left valid keeps others from opening
        try { _decide(keep); }
        finally { EditorWindows.Release(this); }
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
