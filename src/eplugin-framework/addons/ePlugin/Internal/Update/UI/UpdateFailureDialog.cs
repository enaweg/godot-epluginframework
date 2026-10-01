#if TOOLS
using System;
using System.IO;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Update.UI;

[Tool]
internal sealed partial class UpdateFailureDialog : AcceptDialog
{
    private bool _decided;
    public void Initialize(UpdateJournal journal, Action<bool> decide)
    {
        Title = "ePlugin — Update failed to build"; DialogText = ""; DialogHideOnOk = true;
        var box = new VBoxContainer { CustomMinimumSize = new(780, 380) }; AddChild(box);
        box.AddChild(new Label { Text = string.Join("\n", journal.Plugins.Select(p => $"{p.Slug} {p.OldVersion} → {p.NewVersion}")) + "\nThe project does not compile with the installed version." });
        var output = new TextEdit { Editable = false, CustomMinimumSize = new(770, 250), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var logName = journal.Builds.LastOrDefault()?.LogFile;
        var log = logName is not null ? PackageFiles.Inside(journal.Directory, logName) : null;
        output.Text = log is not null && File.Exists(log) ? string.Join("\n", File.ReadLines(log).Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(15)) : journal.Failure ?? "Build failed.";
        box.AddChild(output); box.AddChild(new Label { Text = "Rolling back restores the previous addon files and rebuilds.\n" + (log ?? journal.Directory) });
        GetOkButton().Text = "Roll back"; AddButton("Keep new version", false, "keep"); AddButton("Open build log", true, "log");
        void Choose(bool keep) { if (_decided) return; _decided = true; Hide(); decide(keep); QueueFree(); }
        Confirmed += () => Choose(false); Canceled += () => Choose(false); CloseRequested += () => Choose(false);
        CustomAction += action => { if (action == "keep") Choose(true); else if (action == "log" && log is not null) OS.ShellOpen(log); };
    }
    public void Open() { PopupCentered(new(830, 480)); GetOkButton().GrabFocus(); }
}
#endif
