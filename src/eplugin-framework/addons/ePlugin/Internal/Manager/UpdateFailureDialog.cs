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
internal sealed partial class UpdateFailureDialog : AcceptDialog
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/UpdateFailureDialog.tscn";
    private bool _decided;

    public static UpdateFailureDialog Create(UpdateJournal journal, Action<bool> decide)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<UpdateFailureDialog>();
        dialog.Initialize(journal, decide);
        return dialog;
    }

    private void Initialize(UpdateJournal journal, Action<bool> decide)
    {
        EditorWindows.Prepare(this);
        GetNode<Label>("%Summary").Text = string.Join("\n", journal.Plugins.Select(p => $"{p.Slug} {p.OldVersion} → {p.NewVersion}")) +
                                          "\nThe project does not compile with the installed version.";
        var logName = journal.Builds.LastOrDefault()?.LogFile;
        var log = logName is not null ? PackageFiles.Inside(journal.Directory, logName) : null;
        GetNode<TextEdit>("%BuildOutput").Text = log is not null && File.Exists(log)
            ? string.Join("\n", File.ReadLines(log).Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(15))
            : journal.Failure ?? "Build failed.";
        var location = GetNode<Label>("%LogLocation");
        location.Text = "Rolling back restores the previous addon files and rebuilds.\n" + (log ?? journal.Directory);
        location.TooltipText = log ?? journal.Directory;
        AddButton("Keep new version", false, "keep"); AddButton("Open build log", true, "log");
        void Choose(bool keep) { if (_decided) return; _decided = true; Hide(); decide(keep); QueueFree(); }
        Confirmed += () => Choose(false); Canceled += () => Choose(false); CloseRequested += () => Choose(false);
        CustomAction += action => { if (action == "keep") Choose(true); else if (action == "log" && log is not null) OS.ShellOpen(log); };
    }

    public void Open() { PopupCentered(); GetOkButton().GrabFocus(); }
}
#endif
