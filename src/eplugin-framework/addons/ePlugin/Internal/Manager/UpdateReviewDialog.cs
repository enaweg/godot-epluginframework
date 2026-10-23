#if TOOLS
using System;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Lists the staged updates that need a decision before anything is installed (warnings, another repository, a
/// refusal), each with its findings and a check box that keeps it in the batch. The layout lives in
/// UpdateReviewDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class UpdateReviewDialog : ConfirmationDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/UpdateReviewDialog.tscn";
    private const int InstallColumn = 0, NameColumn = 1, VersionColumn = 2;
    private readonly EditorSignalConnections _signals = new();
    private UpdateReview _review = null!;
    private Action<bool> _finished = null!;
    private Tree _tree = null!;
    private Label _name = null!;
    private RichTextLabel _text = null!;
    private string? _selected;
    private bool _done;

    /// <summary>Calls <paramref name="finished"/> with true to install what the review selects, or false to cancel.</summary>
    public static UpdateReviewDialog Create(UpdateReview review, Action<bool> finished)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<UpdateReviewDialog>();
        dialog.Initialize(review, finished);
        return dialog;
    }

    /// <summary>Opens above the ePlugin Manager.</summary>
    public void Open() => EditorWindows.Popup(this);

    private void Initialize(UpdateReview review, Action<bool> finished)
    {
        _review = review; _finished = finished;
        _tree = GetNode<Tree>("%ReviewTree"); _name = GetNode<Label>("%ReviewName"); _text = GetNode<RichTextLabel>("%ReviewText");
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcon));
        var scale = EditorWindows.Scale;
        _text.AddThemeConstantOverride("line_separation", (int)(_text.GetThemeConstant("line_separation") * scale));
        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(InstallColumn, "Decision"); _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(VersionColumn, "Version");
        _tree.SetColumnExpand(InstallColumn, false); _tree.SetColumnCustomMinimumWidth(InstallColumn, (int)(150 * scale));
        // Plugin names are longer than version changes.
        _tree.SetColumnExpandRatio(NameColumn, 2);
        GetNode<Label>("%Summary").Text = review.Summary;
        _signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(() => Display(_tree.GetSelected()?.GetMetadata(NameColumn).AsString())));
        _signals.Connect(_tree, Tree.SignalName.ItemEdited, Callable.From(ItemEdited));
        _signals.Connect(_text, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(meta =>
        {
            if (PluginManagerViewModel.IsWebUrl(meta.AsString())) OS.ShellOpen(meta.AsString());
        }));
        _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(() => Finish(_review.InstallCount > 0)));
        _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(() => Finish(false)));
        _signals.Connect(this, Window.SignalName.CloseRequested, Callable.From(() => Finish(false)));
        Render();
        // Another repository is the decision that matters most, so it is shown first.
        Display((_review.Entries.FirstOrDefault(e => e.NeedsTrust) ?? _review.Entries[0]).Slug);
    }

    private void Render()
    {
        _tree.Clear(); var root = _tree.CreateItem();
        foreach (var entry in _review.Entries)
        {
            var item = _tree.CreateItem(root);
            item.SetMetadata(NameColumn, entry.Slug);
            item.SetCellMode(InstallColumn, TreeItem.TreeCellMode.Check);
            item.SetText(InstallColumn, entry.Decision);
            item.SetChecked(InstallColumn, entry.Install);
            item.SetEditable(InstallColumn, entry.CanInstall);
            item.SetTooltipText(InstallColumn, !entry.CanInstall ? "This package cannot be installed, see the details."
                : entry.NeedsTrust ? "Check only if you trust the new update source. Later updates come from there."
                : "Uncheck to leave this update out.");
            item.SetText(NameColumn, entry.Candidate.PluginName);
            item.SetText(VersionColumn, $"{entry.Candidate.InstalledVersion} → {entry.NewVersion}");
            var color = !entry.CanInstall ? EditorIcons.ErrorColor : entry.NeedsTrust ? EditorIcons.WarningColor : (Color?)null;
            if (color is { } c) { item.SetCustomColor(InstallColumn, c); item.SetCustomColor(NameColumn, c); }
            if (entry.Slug == _selected) item.Select(NameColumn);
        }
        GetOkButton().Text = _review.OkText;
        GetOkButton().Disabled = _review.InstallCount == 0;
    }

    private void Display(string? slug)
    {
        if (_review.Entries.FirstOrDefault(e => e.Slug == slug) is not { } entry) return;
        _selected = slug;
        _name.Text = entry.Candidate.PluginName;
        _text.Text = UpdateReview.Describe(entry);
        _text.ScrollToLine(0);
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
            if (item.GetMetadata(NameColumn).AsString() == slug && !item.IsSelected(NameColumn)) item.Select(NameColumn);
    }

    private void ItemEdited()
    {
        var item = _tree.GetEdited();
        var slug = item?.GetMetadata(NameColumn).AsString();
        if (item is null || _review.Entries.FirstOrDefault(e => e.Slug == slug) is not { CanInstall: true } entry) return;
        entry.Install = item.IsChecked(InstallColumn);
        GetOkButton().Text = _review.OkText;
        GetOkButton().Disabled = _review.InstallCount == 0;
    }

    private void Finish(bool install)
    {
        if (_done) return;
        _done = true; Hide(); _finished(install); EditorWindows.Release(this);
    }

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    // An assembly reload drops the staged batch with the manager, so the dialog goes with it; nothing is installed.
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
