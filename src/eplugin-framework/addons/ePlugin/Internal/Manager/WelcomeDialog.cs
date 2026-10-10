#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Welcomes;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Shows the welcome pages of installed plugins. Several pages are listed on the left. Closing the dialog counts every
/// page as read; nothing has to be confirmed one by one. The layout lives in WelcomeDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class WelcomeDialog : AcceptDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/WelcomeDialog.tscn";
    private readonly EditorSignalConnections _signals = new();
    private IReadOnlyList<WelcomeEntry> _welcomes = [];
    private Action<IReadOnlyList<WelcomeEntry>>? _closed;
    private ItemList _list = null!;
    private Label _summary = null!;
    private Label _name = null!;
    private Label _source = null!;
    private RichTextLabel _text = null!;
    private bool _done;

    /// <summary>
    /// Shows <paramref name="welcomes"/>; <paramref name="closed"/> is called with them once the dialog is closed, but not
    /// when an assembly reload removes it.
    /// </summary>
    public static WelcomeDialog Create(IReadOnlyList<WelcomeEntry> welcomes, Action<IReadOnlyList<WelcomeEntry>>? closed = null)
    {
        if (welcomes.Count == 0) throw new ArgumentException("There is no welcome page to show.", nameof(welcomes));
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<WelcomeDialog>();
        dialog.Initialize(welcomes.DistinctBy(w => w.Slug).ToArray(), closed);
        return dialog;
    }

    /// <summary>Opens above whatever exclusive editor window is open, e.g. Project Settings or the ePlugin Manager.</summary>
    public void Open() => EditorInterface.Singleton.PopupDialogCenteredClamped(this, Size, 0.9f);

    private void Initialize(IReadOnlyList<WelcomeEntry> welcomes, Action<IReadOnlyList<WelcomeEntry>>? closed)
    {
        _welcomes = welcomes; _closed = closed;
        _list = GetNode<ItemList>("%WelcomeList"); _summary = GetNode<Label>("%Summary");
        _name = GetNode<Label>("%WelcomeName"); _source = GetNode<Label>("%WelcomeSource"); _text = GetNode<RichTextLabel>("%WelcomeText");
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcon));
        _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(Finish));
        _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(Finish));
        _signals.Connect(_list, ItemList.SignalName.ItemSelected, Callable.From<long>(index => Display((int)index)));
        _signals.Connect(_text, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(meta =>
        {
            if (PluginManagerViewModel.IsWebUrl(meta.AsString())) OS.ShellOpen(meta.AsString());
        }));
        _list.Visible = _summary.Visible = welcomes.Count > 1;
        if (closed is null) Title = "ePlugin — Welcome page";
        foreach (var welcome in welcomes) _list.AddItem(welcome.Name);
        Display(0);
    }

    private void Display(int index)
    {
        var entry = _welcomes[index];
        _list.Select(index);
        _name.Text = string.IsNullOrWhiteSpace(entry.Version) ? entry.Name : $"{entry.Name} {entry.Version}";
        _source.Text = PluginLicense.Display(entry.Slug, entry.Source);
        _source.TooltipText = _source.Text;
        // the welcome page is BBCode; a problem is plain text
        _text.Text = entry.Problem is null ? entry.Text : $"[color=#ff7070]{entry.Problem.Replace("[", "[lb]")}[/color]";
        _text.ScrollToLine(0);
    }

    private void Finish()
    {
        if (_done) return;
        _done = true; Hide(); _closed?.Invoke(_welcomes); QueueFree();
    }

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    // An assembly reload drops the dialog without counting the pages as read, so they are shown again afterwards.
    public void OnBeforeSerialize()
    {
        _signals.Dispose();
        if (_done) return;
        _done = true; Hide(); QueueFree();
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
