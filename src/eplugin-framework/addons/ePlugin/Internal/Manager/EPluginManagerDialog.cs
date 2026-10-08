#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Lists every addon with its details and installs updates. The layout lives in EPluginManagerDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class EPluginManagerDialog : ConfirmationDialog
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/EPluginManagerDialog.tscn";
    internal const string EPluginIconPath = "res://addons/ePlugin/icons/eplugin.svg";
    internal const string UpdateIconPath = "res://addons/ePlugin/icons/update.svg";
    private const int CheckColumn = 0, NameColumn = 1, TypeColumn = 2, VersionColumn = 3, UpdateColumn = 4;

    private EGlobal _global = null!;
    private CancellationToken _lifetime;
    private CancellationTokenSource? _cancel;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _check = null!;
    private Button _retry = null!;
    private Button _release = null!;
    private CheckBox _trust = null!;
    private ProgressBar _progress = null!;
    private TextureRect _detailsIcon = null!;
    private Label _detailsName = null!;
    private RichTextLabel _detailsText = null!;
    private Texture2D? _ePluginIcon;
    private Texture2D? _updateIcon;
    private PluginManagerViewModel _model = null!;
    private IReadOnlyList<ValidatedPackage>? _staged;
    private string? _directory;
    private string? _selectedSlug;
    private bool _working;
    private bool _swapping;

    public static EPluginManagerDialog Create(EGlobal global, CancellationToken lifetime)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<EPluginManagerDialog>();
        dialog.Initialize(global, lifetime);
        return dialog;
    }

    private void Initialize(EGlobal global, CancellationToken lifetime)
    {
        _global = global; _lifetime = lifetime;
        _tree = GetNode<Tree>("%PluginTree"); _status = GetNode<Label>("%Status");
        _check = GetNode<Button>("%CheckButton"); _retry = GetNode<Button>("%RetryButton"); _release = GetNode<Button>("%ReleaseButton");
        _trust = GetNode<CheckBox>("%TrustCheck"); _progress = GetNode<ProgressBar>("%Progress");
        _detailsIcon = GetNode<TextureRect>("%DetailsIcon"); _detailsName = GetNode<Label>("%DetailsName"); _detailsText = GetNode<RichTextLabel>("%DetailsText");
        _ePluginIcon = ResourceLoader.Exists(EPluginIconPath) ? GD.Load<Texture2D>(EPluginIconPath) : null;
        _updateIcon = ResourceLoader.Exists(UpdateIconPath) ? GD.Load<Texture2D>(UpdateIconPath) : null;

        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(TypeColumn, "Type");
        _tree.SetColumnTitle(VersionColumn, "Version"); _tree.SetColumnTitle(UpdateColumn, "Update");
        _tree.SetColumnExpand(CheckColumn, false); _tree.SetColumnExpand(TypeColumn, false);
        _tree.SetColumnExpand(VersionColumn, false); _tree.SetColumnExpand(UpdateColumn, false);
        var scale = EditorInterface.Singleton.GetEditorScale();
        Size = (Vector2I)((Vector2)Size * scale); MinSize = (Vector2I)((Vector2)MinSize * scale);
        _tree.SetColumnCustomMinimumWidth(CheckColumn, (int)(32 * scale)); _tree.SetColumnCustomMinimumWidth(TypeColumn, (int)(130 * scale));
        _tree.SetColumnCustomMinimumWidth(VersionColumn, (int)(80 * scale)); _tree.SetColumnCustomMinimumWidth(UpdateColumn, (int)(100 * scale));

        _check.Pressed += CheckNow;
        _retry.Pressed += RetryFailed;
        _release.Pressed += OpenRelease;
        _tree.ItemEdited += SelectionChanged;
        _tree.ItemSelected += () => ShowDetails(SlugOf(_tree.GetSelected()));
        _tree.ItemActivated += OpenRelease;
        _trust.Toggled += value => { _model.TrustChangedSource = value; Buttons(); };
        Confirmed += Confirm;
        Canceled += Cancel;
        CloseRequested += Cancel;
    }

    public void Open()
    {
        Refresh();
        PopupCenteredClamped(Size, 0.9f);
        GetCancelButton().GrabFocus();
    }

    public void Refresh()
    {
        if (_working) return;
        ClearStaging();
        var plugins = _global.CollectPlugins();
        var targets = _global.CollectUpdateTargets();
        _model = new(plugins, _global.PendingUpdates, targets, _global.UpdateCache, candidate =>
        {
            var target = targets.FirstOrDefault(t => t.Slug == candidate.Slug);
            var findings = _global.UpdatePreflightFindings([candidate]).ToList();
            if (target is not null && Directory.Exists(target.Directory) && PackageFiles.Files(target.Directory).Any(f => f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase)))
                findings.Add(new("R10", FindingSeverity.Error, "GDExtension plugins are not supported by ePlugin updates yet."));
            return findings;
        });
        _trust.SetPressedNoSignal(false);
        Render();
    }

    private void Render()
    {
        _tree.Clear(); var root = _tree.CreateItem();
        TreeItem? selected = null;
        var editable = !_working && _staged is null;
        foreach (var row in _model.Plugins)
        {
            var plugin = row.Plugin;
            var item = _tree.CreateItem(root);
            item.SetMetadata(NameColumn, plugin.Slug);
            if (row.Update is { } update)
            {
                item.SetCellMode(CheckColumn, TreeItem.TreeCellMode.Check);
                item.SetEditable(CheckColumn, !update.HasError && editable);
                item.SetChecked(CheckColumn, update.Selected);
                item.SetTooltipText(CheckColumn, update.HasError ? "This update cannot be installed, see details." : "Install this update");
            }
            item.SetText(NameColumn, plugin.Name);
            item.SetTooltipText(NameColumn, $"{plugin.Name} (res://addons/{plugin.Slug})");
            if (row.IsEPlugin && _ePluginIcon is not null) item.SetIcon(NameColumn, _ePluginIcon);
            if (!plugin.Enabled) item.SetCustomColor(NameColumn, DisabledColor());
            if (plugin.FailedAttempt is not null || plugin.State == EEditorPluginState.Error) item.SetCustomColor(NameColumn, ErrorColor());
            item.SetText(TypeColumn, PluginCatalog.KindName(plugin.Kind));
            item.SetText(VersionColumn, plugin.Version);
            item.SetTooltipText(VersionColumn, PluginManagerViewModel.StatusText(plugin));
            if (row.IsUpdatable)
            {
                if (_updateIcon is not null) item.SetIcon(UpdateColumn, _updateIcon);
                item.SetIconModulate(UpdateColumn, row.HasUpdate ? (row.Update!.HasError ? ErrorColor() : UpdateColor()) : DisabledColor());
                item.SetText(UpdateColumn, row.Update?.Candidate.NewVersion ?? "");
                item.SetTooltipText(UpdateColumn, row.Update is { } available
                    ? $"Update available: {available.Candidate.InstalledVersion} → {available.Candidate.NewVersion}"
                    : "Updatable, no update known");
            }
            if (plugin.Slug == _selectedSlug) selected = item;
        }
        if (selected is not null) { selected.Select(NameColumn); _tree.ScrollToItem(selected); }
        else ShowDetails(null);
        var count = _model.Updates.Count;
        _status.Text = (count == 0 ? "No updates known" : $"{count} update{(count == 1 ? "" : "s")} available") +
                       " · Last checked: " + (_global.LastUpdateCheck?.ToLocalTime().ToString("g") ?? "never");
        Buttons();
    }

    private void ShowDetails(string? slug)
    {
        _selectedSlug = slug;
        var row = slug is null ? null : _model.Find(slug);
        _detailsIcon.Texture = row?.IsEPlugin == true ? _ePluginIcon : null;
        _detailsName.Text = row?.Plugin.Name ?? "No plugin selected";
        _detailsText.Text = row is null ? "Select a plugin to see its details." : PluginManagerViewModel.Describe(row);
        _release.Visible = IsSafeUrl(row?.Update?.Candidate.ReleaseUrl);
    }

    private void Buttons()
    {
        GetOkButton().Text = _staged is null ? _model.OkText : "Install reviewed updates";
        GetOkButton().Disabled = _working || !_model.CanApply;
        GetOkButton().Visible = _model.Updates.Count > 0 || _staged is not null;
        GetCancelButton().Disabled = _swapping;
        GetCancelButton().Text = _working && !_swapping ? "Cancel" : "Close";
        _trust.Visible = _model.RequiresTrust;
        _check.Disabled = _working || _staged is not null;
        _retry.Disabled = _working || _staged is not null || !_model.CanRetry;
    }

    private static string? SlugOf(TreeItem? item) =>
        item?.GetMetadata(NameColumn).VariantType == Variant.Type.String ? item.GetMetadata(NameColumn).AsString() : null;

    private void SelectionChanged()
    {
        if (_working || _staged is not null) return;
        var item = _tree.GetEdited();
        var update = SlugOf(item) is { } slug ? _model.Find(slug)?.Update : null;
        if (update is not null) update.Selected = item!.IsChecked(CheckColumn);
        Buttons();
    }

    private void OpenRelease()
    {
        var url = _selectedSlug is null ? null : _model.Find(_selectedSlug)?.Update?.Candidate.ReleaseUrl;
        if (IsSafeUrl(url)) OS.ShellOpen(url);
    }

    private static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0;

    private void RetryFailed()
    {
        if (_working || _staged is not null) return;
        _global.RetryFailedPlugins();
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Refresh();
    }

    private async void CheckNow()
    {
        if (_working) return;
        ClearStaging(); _working = true; Buttons(); _status.Text = "Checking for updates...";
        string? failure = null;
        try
        {
            var result = await _global.CheckForUpdatesAsync(true);
            if (result.Failures.Count > 0) failure = string.Join("; ", result.Failures.Select(f => $"{f.Slug}: {f.Message}"));
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            _working = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) { Refresh(); if (failure is not null) _status.Text = "Update check failed: " + failure; }
        }
    }

    private async void Confirm()
    {
        if (_working || !_model.CanApply) return;
        _working = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        try
        {
            if (_staged is null)
            {
                _progress.Visible = true; _status.Text = "Downloading and validating selected addons...";
                var relay = new InlineProgress(value => Callable.From(() => { if (GodotObject.IsInstanceValid(this)) _progress.Value = value * 100; }).CallDeferred());
                var staged = await _global.StageUpdatesAsync(_model.Selected, relay, _cancel.Token);
                _staged = staged.Packages; _directory = staged.Directory;
                if (_lifetime.IsCancellationRequested || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) { ClearStaging(); return; }
                foreach (var package in _staged)
                {
                    var row = _model.Updates.First(r => r.Candidate.Slug == package.Candidate.Slug);
                    row.Findings.AddRange(package.Findings);
                }
                // Warnings discovered inside the package are reviewed before the first project side effect.
                if (_staged.Any(p => p.Findings.Any(f => f.Severity == FindingSeverity.Warning)))
                { _working = false; _progress.Visible = false; Render(); _status.Text = "Review package warnings in the details, then confirm installation."; return; }
            }
            _cancel.Token.ThrowIfCancellationRequested();
            if (!_model.CanApply) return;
            _swapping = true; Buttons(); _status.Text = "Installing selected addons...";
            var selected = _staged.Where(p => _model.Selected.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
            var outcome = await _global.ApplyUpdatesAsync(selected, _directory!, _model.TrustChangedSource);
            _directory = null; _staged = null; Hide();
            if (outcome == UpdateOutcome.Completed) { _working = false; _swapping = false; Refresh(); }
        }
        catch (OperationCanceledException) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Download canceled. Addon files were not changed."; ClearStaging(); }
        catch (Exception ex) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Update failed: " + ex.Message; if (!_swapping) ClearStaging(); }
        finally
        {
            _working = false; _swapping = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) { _progress.Visible = false; Render(); }
        }
    }

    private void Cancel()
    {
        if (_swapping) return;
        _cancel?.Cancel();
        if (!_working) ClearStaging();
        Hide();
    }

    private void ClearStaging()
    {
        if (_directory is not null && !File.Exists(Path.Combine(_directory, "journal.json")) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        _directory = null; _staged = null;
    }

    private static Color ThemeColor(string name, string type, Color fallback)
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        return theme is not null && theme.HasColor(name, type) ? theme.GetColor(name, type) : fallback;
    }
    private static Color UpdateColor() => ThemeColor("success_color", "Editor", new Color(0.45f, 0.95f, 0.5f));
    private static Color ErrorColor() => ThemeColor("error_color", "Editor", new Color(1f, 0.47f, 0.42f));
    private static Color DisabledColor() => ThemeColor("font_disabled_color", "Button", new Color(0.5f, 0.5f, 0.5f));

    public override void _ExitTree()
    {
        _cancel?.Cancel(); _cancel?.Dispose();
        if (!_swapping) ClearStaging();
        base._ExitTree();
    }
}
#endif
