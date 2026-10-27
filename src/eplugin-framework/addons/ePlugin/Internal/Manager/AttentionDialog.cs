#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>What the user decided for a plugin in the Needs attention dialog; the ePlugin Manager carries it out.</summary>
internal enum AttentionAction { Retry, RetryAll, Disable, Rollback }

/// <summary>
/// Lists only the plugins that need attention (failed or interrupted attempts, updates kept with build errors, plugins in
/// error, GDExtensions Godot does not run) with what happened and the decisions each offers. The ePlugin Manager owns it,
/// carries out the decisions and shows the list again afterwards. The layout lives in AttentionDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class AttentionDialog : AcceptDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/AttentionDialog.tscn";
    private const string RetryAllAction = "retry_all";
    private readonly EditorSignalConnections _signals = new();
    private IReadOnlyList<AttentionItem> _items = [];
    private ItemList _list = null!;
    private Label _name = null!;
    private RichTextLabel _text = null!;
    private Button _retry = null!;
    private Button _rollback = null!;
    private Button _disable = null!;
    private Button _log = null!;
    private Button _folder = null!;
    private Button _retryAll = null!;
    private ConfirmationDialog _rollbackConfirm = null!;
    private string? _selected;
    // The plugin and kept update the rollback confirmation names; the list may change while it is open.
    private (string Slug, string Journal)? _confirming;
    private bool _busy;

    /// <summary>Raised for a decision; the item is null for <see cref="AttentionAction.RetryAll"/>.</summary>
    public event Action<AttentionAction, AttentionItem?>? Requested;

    public void Initialize()
    {
        _list = GetNode<ItemList>("%AttentionList"); _name = GetNode<Label>("%AttentionName"); _text = GetNode<RichTextLabel>("%AttentionText");
        _retry = GetNode<Button>("%RetryPluginButton"); _rollback = GetNode<Button>("%RollbackButton"); _disable = GetNode<Button>("%DisablePluginButton");
        _log = GetNode<Button>("%OpenLogButton"); _folder = GetNode<Button>("%OpenPluginFolderButton");
        _rollbackConfirm = GetNode<ConfirmationDialog>("%RollbackConfirm");
        foreach (var window in new Window[] { this, _rollbackConfirm }) EditorWindows.Prepare(window, _signals, this, nameof(QueueWindowIcons));
        var scale = EditorWindows.Scale;
        _text.AddThemeConstantOverride("line_separation", (int)(_text.GetThemeConstant("line_separation") * scale));
        _retryAll = AddButton("Retry all", false, RetryAllAction);
        _retryAll.TooltipText = "Retry every failed or interrupted plugin activation, deactivation and update.";
        _signals.Connect(_list, ItemList.SignalName.ItemSelected, Callable.From<long>(index => Display(_items[(int)index].Slug)));
        _signals.Connect(_text, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(meta =>
        {
            if (PluginManagerViewModel.IsWebUrl(meta.AsString())) OS.ShellOpen(meta.AsString());
        }));
        _signals.Connect(_retry, BaseButton.SignalName.Pressed, Callable.From(() => Request(AttentionAction.Retry)));
        _signals.Connect(_disable, BaseButton.SignalName.Pressed, Callable.From(() => Request(AttentionAction.Disable)));
        _signals.Connect(_rollback, BaseButton.SignalName.Pressed, Callable.From(ConfirmRollback));
        _signals.Connect(_rollbackConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(RollbackConfirmed));
        _signals.Connect(_log, BaseButton.SignalName.Pressed, Callable.From(() => { if (Selected?.Log is { } log) OS.ShellOpen(log); }));
        _signals.Connect(_folder, BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            if (Selected is { } item) OS.ShellShowInFileManager(ProjectSettings.GlobalizePath("res://addons/" + item.Slug), true);
        }));
        _signals.Connect(this, AcceptDialog.SignalName.CustomAction, Callable.From<StringName>(action =>
        {
            if (action == RetryAllAction && !_busy) Requested?.Invoke(AttentionAction.RetryAll, null);
        }));
    }

    private AttentionItem? Selected => _items.FirstOrDefault(i => i.Slug == _selected);

    /// <summary>Opens the list at <paramref name="slug"/>, or at the first plugin.</summary>
    public void Open(IReadOnlyList<AttentionItem> items, string? slug)
    {
        if (items.Count == 0) return;
        _selected = slug;
        Update(items);
        PopupCentered();
    }

    /// <summary>
    /// Shows the plugins that need attention now, e.g. after a decision was carried out; closes once none is left.
    /// </summary>
    public void Update(IReadOnlyList<AttentionItem> items)
    {
        _items = items;
        if (items.Count == 0)
        {
            if (Visible) Hide();
            return;
        }
        if (Selected is null) _selected = items[0].Slug;
        var theme = EditorWindows.Theme;
        Texture2D? Icon(string name) => theme is not null && theme.HasIcon(name, "EditorIcons") ? theme.GetIcon(name, "EditorIcons") : null;
        _list.Clear();
        foreach (var item in items)
        {
            var index = _list.AddItem(item.Plugin.Name, Icon(item.CanRetry || item.Plugin.State == EEditorPluginState.Error ? "StatusError" : "StatusWarning"));
            _list.SetItemTooltip(index, item.Summary);
            if (item.Slug == _selected) _list.Select(index);
        }
        _retryAll.Visible = items.Count(i => i.CanRetry) > 1;
        // read on every update, so they follow theme changes
        _retry.Icon = _retryAll.Icon = EditorIcons.Retry; _rollback.Icon = EditorIcons.Downgrade;
        Display(_selected!);
    }

    /// <summary>Locks the decisions while the manager carries one out.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (_items.Count > 0) Buttons();
    }

    private void Display(string slug)
    {
        _selected = slug;
        if (Selected is not { } item) return;
        _name.Text = PluginManagerViewModel.DetailsTitle(item.Plugin);
        _text.Text = item.Describe();
        _text.ScrollToLine(0);
        Buttons();
    }

    private void Buttons()
    {
        var item = Selected;
        _retry.Visible = item?.CanRetry == true;
        _rollback.Visible = item?.CanRollback == true;
        _disable.Visible = item?.CanDisable == true;
        _log.Visible = item?.Log is not null;
        _folder.Visible = item is { Plugin.Missing: false };
        foreach (var button in new[] { _retry, _rollback, _disable, _retryAll }) button.Disabled = _busy;
        _retry.TooltipText = item is { RetriedWith.Count: > 0 } ? $"Also retries {string.Join(", ", item.RetriedWith)}." : "";
    }

    private void Request(AttentionAction action)
    {
        if (!_busy && Selected is { } item) Requested?.Invoke(action, item);
    }

    private void ConfirmRollback()
    {
        if (_busy || Selected is not { Kept: { } kept } item) return;
        _confirming = (item.Slug, kept.Id);
        var versions = string.Join(", ", kept.Plugins.Select(p => $"{p.Slug} {p.OldVersion}"));
        _rollbackConfirm.DialogText = $"Restore {versions}?\n\nOpen scenes are saved first. Changes made inside the plugin's folder since the update are lost. " +
            (kept.BeforeReconcile
                ? "Project files you changed since then (.csproj, .sln, nuget.config) stay as they are. "
                : "Project files (.csproj, .sln, nuget.config) stay as they are; only the changes of the new version's recipe are undone. ") +
            "If you already adapted the project's code to the new version, it will not build with the old one.";
        _rollbackConfirm.PopupCentered();
    }

    /// <summary>Rolls back the update the confirmation named, if it is still the one selected and still kept.</summary>
    internal void RollbackConfirmed()
    {
        var confirmed = _confirming;
        _confirming = null;
        if (Selected is { Kept: { } kept } item && confirmed == (item.Slug, kept.Id)) Request(AttentionAction.Rollback);
    }

    private void QueueWindowIcons() => CallDeferred(nameof(RefreshWindowIcons));
    private void RefreshWindowIcons()
    {
        foreach (var window in new Window[] { this, _rollbackConfirm }) EditorWindows.ApplyIcon(window);
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
}
#endif
