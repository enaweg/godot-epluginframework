#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;

namespace Enaweg.Plugin.Internal.Update.UI;

[Tool]
internal sealed partial class UpdateDialog : ConfirmationDialog
{
    private EGlobal _global = null!;
    private CancellationToken _lifetime;
    private CancellationTokenSource? _cancel;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _check = null!;
    private CheckBox _trust = null!;
    private ProgressBar _progress = null!;
    private UpdateDialogViewModel _model = null!;
    private System.Collections.Generic.IReadOnlyList<ValidatedPackage>? _staged;
    private string? _directory;
    private bool _working;
    private bool _swapping;

    public void Initialize(EGlobal global, CancellationToken lifetime)
    {
        _global = global; _lifetime = lifetime;
        Title = "ePlugin — Update plugins"; DialogHideOnOk = false;
        var box = new VBoxContainer { CustomMinimumSize = new(800, 430) }; AddChild(box);
        var top = new HBoxContainer(); box.AddChild(top);
        _status = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; top.AddChild(_status);
        _check = new Button { Text = "Check now" }; top.AddChild(_check); _check.Pressed += CheckNow;
        _tree = new Tree { Columns = 3, HideRoot = true, ColumnTitlesVisible = true, CustomMinimumSize = new(790, 280), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _tree.SetColumnTitle(0, "Plugin"); _tree.SetColumnTitle(1, "Installed → New"); _tree.SetColumnTitle(2, "Source");
        box.AddChild(_tree); _tree.ItemEdited += SelectionChanged; _tree.ItemActivated += OpenRelease;
        box.AddChild(new Label { Text = "Selected addons will be replaced. Open scenes are saved first.\nFiles and project references are backed up and restored if installation fails." });
        _trust = new CheckBox { Text = "I trust the changed update source", Visible = false }; box.AddChild(_trust);
        _trust.Toggled += value => { _model.TrustChangedSource = value; Buttons(); };
        _progress = new ProgressBar { Visible = false, MaxValue = 100 }; box.AddChild(_progress);
        Confirmed += Confirm;
        Canceled += Cancel;
        CloseRequested += Cancel;
    }
    public void Open()
    {
        _global.RefreshPlainPlugins(); Refresh(); PopupCentered(new(850, 550)); GetCancelButton().GrabFocus();
    }
    public void Refresh()
    {
        if (_working) return;
        ClearStaging();
        var targets = _global.CollectUpdateTargets();
        _model = new(_global.PendingUpdates, targets, _global.UpdateCache, candidate =>
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
        foreach (var row in _model.Rows)
        {
            var item = _tree.CreateItem(root);
            item.SetCellMode(0, TreeItem.TreeCellMode.Check); item.SetEditable(0, !row.HasError && !_working && _staged is null);
            item.SetChecked(0, row.Selected); item.SetText(0, row.Candidate.PluginName); item.SetMetadata(0, row.Candidate.Slug);
            item.SetText(1, row.Candidate.InstalledVersion + " → " + row.Candidate.NewVersion);
            item.SetText(2, row.Candidate.SourceUrl);
            foreach (var finding in row.Findings)
            {
                var child = _tree.CreateItem(item); child.SetText(0, finding.Severity + ": " + finding.Message); child.SetTooltipText(0, finding.Message);
            }
            if (row.Failed is { } failed) _tree.CreateItem(item).SetText(0, $"Previously failed {failed.Utc:u}: {failed.Reason}");
        }
        _status.Text = _model.Rows.Count == 0 ? "All plugins are up to date (no known updates)." : "Last checked: " + (_global.LastUpdateCheck?.ToString("u") ?? "never");
        Buttons();
    }
    private void Buttons()
    {
        GetOkButton().Text = _staged is null ? _model.OkText : "Install reviewed updates";
        GetOkButton().Disabled = _working || !_model.CanApply;
        GetCancelButton().Disabled = _swapping;
        GetCancelButton().Text = _model.Rows.Count == 0 ? "Close" : "Cancel";
        _trust.Visible = _model.RequiresTrust;
        _check.Disabled = _working;
    }
    private void SelectionChanged()
    {
        if (_working || _staged is not null) return;
        var item = _tree.GetEdited();
        if (item?.GetMetadata(0).VariantType != Variant.Type.String) return;
        var row = _model.Rows.FirstOrDefault(r => r.Candidate.Slug == item.GetMetadata(0).AsString());
        if (row is not null) row.Selected = item.IsChecked(0);
        Buttons();
    }
    private void OpenRelease()
    {
        var item = _tree.GetSelected();
        if (item?.GetMetadata(0).VariantType != Variant.Type.String) return;
        var url = _model.Rows.FirstOrDefault(r => r.Candidate.Slug == item.GetMetadata(0).AsString())?.Candidate.ReleaseUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0) OS.ShellOpen(url);
    }
    private async void CheckNow()
    {
        if (_working) return;
        ClearStaging(); _working = true; Buttons(); _status.Text = "Checking for updates...";
        string? failure = null;
        try { await _global.CheckForUpdatesAsync(true); }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            _working = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) { Refresh(); if (failure is not null) _status.Text = failure; }
        }
    }
    private async void Confirm()
    {
        if (_working || !_model.CanApply) return;
        _working = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Buttons(); Render();
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
                    var row = _model.Rows.First(r => r.Candidate.Slug == package.Candidate.Slug);
                    row.Findings.AddRange(package.Findings);
                }
                // Warnings discovered inside the package are reviewed before the first project side effect.
                if (_staged.Any(p => p.Findings.Any(f => f.Severity == FindingSeverity.Warning)))
                { _working = false; Render(); _status.Text = "Review package warnings, then confirm installation."; return; }
            }
            _cancel.Token.ThrowIfCancellationRequested();
            if (!_model.CanApply) return;
            _swapping = true; Buttons(); _status.Text = "Installing selected addons...";
            var selected = _staged.Where(p => _model.Selected.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
            var outcome = await _global.ApplyUpdatesAsync(selected, _directory!, _model.TrustChangedSource);
            _directory = null; _staged = null; Hide();
            if (outcome == UpdateOutcome.Completed) Refresh();
        }
        catch (OperationCanceledException) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Download canceled. Addon files were not changed."; ClearStaging(); }
        catch (Exception ex) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Update failed: " + ex.Message; if (!_swapping) ClearStaging(); }
        finally { _working = false; _swapping = false; if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Buttons(); }
    }
    private void Cancel() { if (_swapping) return; _cancel?.Cancel(); if (!_working) ClearStaging(); Hide(); }
    private void ClearStaging()
    {
        if (_directory is not null && !File.Exists(Path.Combine(_directory, "journal.json")) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        _directory = null; _staged = null;
    }
    public override void _ExitTree()
    {
        _cancel?.Cancel(); _cancel?.Dispose();
        if (!_swapping) ClearStaging();
        base._ExitTree();
    }
}
#endif
