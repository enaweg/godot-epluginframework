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
    private const int EnabledColumn = 0, NameColumn = 1, TypeColumn = 2, VersionColumn = 3, UpdateColumn = 4;

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
    private ConfirmationDialog _disableFrameworkConfirm = null!;
    private TextureRect _detailsIcon = null!;
    private Label _detailsName = null!;
    private RichTextLabel _detailsText = null!;
    private Control _versionSeparator = null!;
    private Control _versionRow = null!;
    private OptionButton _versionSelect = null!;
    private Button _installVersion = null!;
    private Label _versionHint = null!;
    private ConfirmationDialog _versionConfirm = null!;
    private readonly Dictionary<string, IReadOnlyList<UpdateCandidate>> _versions = [];
    private readonly Dictionary<string, string> _versionErrors = [];
    private readonly HashSet<string> _loadingVersions = [];
    private IReadOnlyList<VersionOption> _versionOptions = [];
    private UpdateCandidate? _pendingVersion;
    private bool _pendingDowngrade;
    private Texture2D? _ePluginIcon;
    private Texture2D? _updateIcon;
    private PluginManagerViewModel _model = null!;
    private IReadOnlyList<ValidatedPackage>? _staged;
    // What is being staged/installed: the checked updates, or one explicitly chosen version.
    private IReadOnlyList<UpdateCandidate> _batch = [];
    private bool _versionInstall;
    private bool _allowDowngrade;
    private string? _directory;
    private string? _selectedSlug;
    private bool _working;
    private bool _swapping;

    /// <summary>False once an assembly reload dropped the C# state of this still existing editor node.</summary>
    public bool IsInitialized => _global is not null;

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
        _disableFrameworkConfirm = GetNode<ConfirmationDialog>("%DisableFrameworkConfirm");
        _detailsIcon = GetNode<TextureRect>("%DetailsIcon"); _detailsName = GetNode<Label>("%DetailsName"); _detailsText = GetNode<RichTextLabel>("%DetailsText");
        _versionSeparator = GetNode<Control>("%VersionSeparator"); _versionRow = GetNode<Control>("%VersionRow");
        _versionSelect = GetNode<OptionButton>("%VersionSelect"); _installVersion = GetNode<Button>("%InstallVersionButton");
        _versionHint = GetNode<Label>("%VersionHint"); _versionConfirm = GetNode<ConfirmationDialog>("%VersionConfirm");
        _ePluginIcon = ResourceLoader.Exists(EPluginIconPath) ? GD.Load<Texture2D>(EPluginIconPath) : null;
        _updateIcon = ResourceLoader.Exists(UpdateIconPath) ? GD.Load<Texture2D>(UpdateIconPath) : null;

        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(EnabledColumn, "On"); _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(TypeColumn, "Type");
        _tree.SetColumnTitle(VersionColumn, "Version"); _tree.SetColumnTitle(UpdateColumn, "Update");
        _tree.SetColumnExpand(EnabledColumn, false); _tree.SetColumnExpand(TypeColumn, false);
        _tree.SetColumnExpand(VersionColumn, false); _tree.SetColumnExpand(UpdateColumn, false);
        var scale = EditorInterface.Singleton.GetEditorScale();
        Size = (Vector2I)((Vector2)Size * scale); MinSize = (Vector2I)((Vector2)MinSize * scale);
        _tree.SetColumnCustomMinimumWidth(EnabledColumn, (int)(40 * scale)); _tree.SetColumnCustomMinimumWidth(TypeColumn, (int)(130 * scale));
        _tree.SetColumnCustomMinimumWidth(VersionColumn, (int)(80 * scale)); _tree.SetColumnCustomMinimumWidth(UpdateColumn, (int)(120 * scale));

        _check.Pressed += CheckNow;
        _retry.Pressed += RetryFailed;
        _release.Pressed += OpenRelease;
        _tree.ItemEdited += ItemEdited;
        _disableFrameworkConfirm.Confirmed += DisableFramework;
        _versionSelect.ItemSelected += _ => VersionButtons();
        _installVersion.Pressed += ConfirmVersion;
        _versionConfirm.Confirmed += () => { if (_pendingVersion is { } version) Install([version], true, _pendingDowngrade); };
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
            item.SetCellMode(EnabledColumn, TreeItem.TreeCellMode.Check);
            item.SetChecked(EnabledColumn, plugin.Enabled);
            item.SetEditable(EnabledColumn, row.CanToggle && editable);
            item.SetTooltipText(EnabledColumn, !row.CanToggle ? "The plugin folder or its plugin.cfg is missing."
                : plugin.Enabled ? "Enabled. Uncheck to disable the plugin." : "Disabled. Check to enable the plugin.");
            item.SetText(NameColumn, plugin.Name);
            item.SetTooltipText(NameColumn, $"{plugin.Name} (res://addons/{plugin.Slug})");
            if (row.IsEPlugin && _ePluginIcon is not null) item.SetIcon(NameColumn, _ePluginIcon);
            if (!plugin.Enabled) item.SetCustomColor(NameColumn, DisabledColor());
            if (plugin.FailedAttempt is not null || plugin.State == EEditorPluginState.Error) item.SetCustomColor(NameColumn, ErrorColor());
            item.SetText(TypeColumn, PluginCatalog.KindName(plugin.Kind));
            item.SetText(VersionColumn, plugin.Version);
            item.SetTooltipText(VersionColumn, PluginManagerViewModel.StatusText(plugin));
            if (row.Update is { } update)
            {
                item.SetCellMode(UpdateColumn, TreeItem.TreeCellMode.Check);
                item.SetEditable(UpdateColumn, !update.HasError && editable);
                item.SetChecked(UpdateColumn, update.Selected);
            }
            if (row.IsUpdatable)
            {
                if (_updateIcon is not null) item.SetIcon(UpdateColumn, _updateIcon);
                item.SetIconModulate(UpdateColumn, row.HasUpdate ? (row.Update!.HasError ? ErrorColor() : UpdateColor()) : DisabledColor());
                item.SetText(UpdateColumn, row.Update?.Candidate.NewVersion ?? "");
                item.SetTooltipText(UpdateColumn, row.Update is { } available
                    ? $"Update available: {available.Candidate.InstalledVersion} → {available.Candidate.NewVersion}" +
                      (available.HasError ? "\nThis update cannot be installed, see details." : "\nCheck to include it in Update.")
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
        var reviewed = _versionInstall ? _staged?.FirstOrDefault(p => p.Candidate.Slug == slug)?.Findings : null;
        _detailsText.Text = row is null ? "Select a plugin to see its details." : PluginManagerViewModel.Describe(row, reviewed);
        _release.Visible = IsSafeUrl(row?.Update?.Candidate.ReleaseUrl);
        RenderVersions(row);
    }

    /// <summary>The version choice of an updatable plugin. Versions are listed on demand and kept for the session.</summary>
    private void RenderVersions(PluginRow? row)
    {
        var visible = row is { IsUpdatable: true, Plugin.Missing: false };
        _versionSeparator.Visible = _versionRow.Visible = visible;
        _versionSelect.Clear(); _versionOptions = [];
        if (!visible) { _versionHint.Visible = false; return; }
        var plugin = row!.Plugin;
        if (!plugin.Enabled) SetVersionPlaceholder(plugin.Version + " (installed)", "Enable the plugin to change its version.");
        else if (_loadingVersions.Contains(plugin.Slug)) SetVersionPlaceholder("Loading versions...", null);
        else if (_versionErrors.TryGetValue(plugin.Slug, out var error)) SetVersionPlaceholder(plugin.Version + " (installed)", "Versions are unavailable: " + error);
        else if (!_versions.TryGetValue(plugin.Slug, out var versions)) { SetVersionPlaceholder("Loading versions...", null); LoadVersions(plugin.Slug); }
        else
        {
            _versionOptions = PluginManagerViewModel.VersionOptions(plugin.Version, versions);
            var latest = _versionOptions.FirstOrDefault(o => !o.Installed && o.Candidate is not null && !o.IsDowngrade);
            for (var i = 0; i < _versionOptions.Count; i++)
            {
                var option = _versionOptions[i];
                _versionSelect.AddItem(option.Version + (option.Installed ? " (installed)" : option == latest ? " (latest)" : ""), i);
                if (option.Installed) _versionSelect.Select(i);
            }
            _versionSelect.Disabled = _versionOptions.Count < 2;
        }
        VersionButtons();
    }

    private void SetVersionPlaceholder(string text, string? hint)
    {
        _versionSelect.AddItem(text); _versionSelect.Select(0); _versionSelect.Disabled = true;
        _versionHint.Text = hint ?? ""; _versionHint.Visible = hint is not null;
    }

    private async void LoadVersions(string slug)
    {
        if (!_loadingVersions.Add(slug)) return;
        try { _versions[slug] = await _global.ListVersionsAsync(slug, _lifetime); }
        catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested) { _versionErrors[slug] = ex.Message; }
        catch (OperationCanceledException) { return; }
        finally { _loadingVersions.Remove(slug); }
        if (GodotObject.IsInstanceValid(this) && IsInsideTree() && _selectedSlug == slug) RenderVersions(_model.Find(slug));
    }

    private VersionOption? SelectedVersion()
    {
        var index = _versionSelect.Selected;
        return index >= 0 && _versionSelect.GetItemId(index) is var id && id >= 0 && id < _versionOptions.Count && _versionOptions.Count > 0
            ? _versionOptions[id] : null;
    }

    private void VersionButtons()
    {
        var row = _selectedSlug is null ? null : _model.Find(_selectedSlug);
        var option = _versionOptions.Count == 0 ? null : SelectedVersion();
        if (row is null || option is null) { _installVersion.Disabled = true; _installVersion.Text = "Update"; return; }
        var blocked = PluginManagerViewModel.VersionChangeBlocked(row, option);
        _installVersion.Text = option.IsDowngrade ? "Downgrade" : "Update";
        _installVersion.Disabled = blocked is not null || _working || _staged is not null;
        _installVersion.TooltipText = blocked ?? $"Install version {option.Version} of {row.Plugin.Name}.";
        var hint = option.Installed ? null : blocked;
        _versionHint.Text = hint ?? ""; _versionHint.Visible = hint is not null;
    }

    private void ConfirmVersion()
    {
        var row = _selectedSlug is null ? null : _model.Find(_selectedSlug);
        var option = SelectedVersion();
        if (row is null || option?.Candidate is null || PluginManagerViewModel.VersionChangeBlocked(row, option) is not null || _working) return;
        // The listed candidate was built when the list was loaded; the installed version may have changed since.
        _pendingVersion = option.Candidate with { InstalledVersion = row.Plugin.Version };
        _pendingDowngrade = option.IsDowngrade;
        _versionConfirm.OkButtonText = option.IsDowngrade ? "Downgrade" : "Update";
        _versionConfirm.DialogText = $"Replace {row.Plugin.Name} {row.Plugin.Version} with version {option.Version}?\n\n" +
            (option.IsDowngrade
                ? "Use this to undo an update that broke the project. Project code or saved data that already relies on the newer version may stop working. "
                : "") +
            "Open scenes are saved first; files and project references are backed up and restored if installation fails.";
        _versionConfirm.PopupCentered();
    }

    private void Buttons()
    {
        GetOkButton().Text = _staged is null ? _model.OkText : _versionInstall ? "Install reviewed version" : "Install reviewed updates";
        GetOkButton().Disabled = _working || !CanProceed;
        GetOkButton().Visible = _model.Updates.Count > 0 || _staged is not null;
        GetCancelButton().Disabled = _swapping;
        GetCancelButton().Text = _working && !_swapping ? "Cancel" : "Close";
        _trust.Visible = NeedsTrust;
        _check.Disabled = _working || _staged is not null;
        _retry.Disabled = _working || _staged is not null || !_model.CanRetry;
        VersionButtons();
    }

    private bool NeedsTrust => _staged is not null ? _staged.Any(p => p.Findings.Any(f => f.RequiresTrust)) : _model.RequiresTrust;
    private bool CanProceed => _staged is not null ? !NeedsTrust || _model.TrustChangedSource : _model.CanApply;

    private static string? SlugOf(TreeItem? item) =>
        item?.GetMetadata(NameColumn).VariantType == Variant.Type.String ? item.GetMetadata(NameColumn).AsString() : null;

    private void ItemEdited()
    {
        var item = _tree.GetEdited();
        var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
        if (item is null || row is null || _working || _staged is not null) return;
        if (_tree.GetEditedColumn() == UpdateColumn && row.Update is { } update)
        {
            update.Selected = item.IsChecked(UpdateColumn);
            Buttons();
        }
        else if (_tree.GetEditedColumn() == EnabledColumn)
        {
            var enable = item.IsChecked(EnabledColumn);
            if (!enable && row.Plugin.Kind == PluginKind.Framework)
            {
                item.SetChecked(EnabledColumn, true);
                _disableFrameworkConfirm.PopupCentered();
                return;
            }
            // Enabling or disabling runs the plugin's recipe and may rebuild; never do that, or rebuild the tree,
            // inside the Tree's own edit signal.
            _working = true; Buttons();
            _status.Text = (enable ? "Enabling " : "Disabling ") + row.Plugin.Name + "...";
            Callable.From(() => SetPluginEnabled(row.Plugin.Slug, enable)).CallDeferred();
        }
    }

    private void SetPluginEnabled(string slug, bool enable)
    {
        try { EditorInterface.Singleton.SetPluginEnabled(slug, enable); }
        catch (Exception ex) { GD.PushError($"Cannot {(enable ? "enable" : "disable")} {slug}: {ex.Message}"); }
        finally
        {
            _working = false;
            // Dependencies and dependants may have been toggled too, so reload the whole list.
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Refresh();
        }
    }

    private void DisableFramework()
    {
        // The framework removes this dialog while it is disabled.
        Hide();
        var slug = _model.Plugins.First(p => p.Plugin.Kind == PluginKind.Framework).Plugin.Slug;
        Callable.From(() => EditorInterface.Singleton.SetPluginEnabled(slug, false)).CallDeferred();
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
        ClearStaging(); _versions.Clear(); _versionErrors.Clear();
        _working = true; Buttons(); _status.Text = "Checking for updates...";
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

    private void Confirm()
    {
        if (_staged is not null) Install(_batch, _versionInstall, _allowDowngrade);
        else if (_model.CanApply) Install(_model.Selected, false, false);
    }

    /// <summary>
    /// Stages and validates the batch, stops for review when the packages carry warnings, then installs. A second call
    /// with the batch already staged continues with the installation.
    /// </summary>
    private async void Install(IReadOnlyList<UpdateCandidate> batch, bool versionInstall, bool allowDowngrade)
    {
        if (_working || batch.Count == 0) return;
        _batch = batch; _versionInstall = versionInstall; _allowDowngrade = allowDowngrade;
        _working = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        try
        {
            if (_staged is null)
            {
                _progress.Visible = true; _status.Text = versionInstall ? $"Downloading and validating {batch[0].PluginName} {batch[0].NewVersion}..." : "Downloading and validating selected addons...";
                var relay = new InlineProgress(value => Callable.From(() => { if (GodotObject.IsInstanceValid(this)) _progress.Value = value * 100; }).CallDeferred());
                var staged = await _global.StageUpdatesAsync(batch, relay, _cancel.Token, allowDowngrade);
                _staged = staged.Packages; _directory = staged.Directory;
                if (_lifetime.IsCancellationRequested || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) { ClearStaging(); return; }
                if (!versionInstall)
                    foreach (var package in _staged)
                        _model.Updates.First(r => r.Candidate.Slug == package.Candidate.Slug).Findings.AddRange(package.Findings);
                // Warnings discovered inside the package are reviewed before the first project side effect.
                if (_staged.Any(p => p.Findings.Any(f => f.Severity == FindingSeverity.Warning)) || NeedsTrust && !_model.TrustChangedSource)
                { _working = false; _progress.Visible = false; Render(); _status.Text = "Review package warnings in the details, then confirm installation."; return; }
            }
            _cancel.Token.ThrowIfCancellationRequested();
            if (!CanProceed) return;
            _swapping = true; Buttons(); _status.Text = versionInstall ? "Installing the selected version..." : "Installing selected addons...";
            var selected = _staged.Where(p => batch.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
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
        _directory = null; _staged = null; _batch = []; _versionInstall = false; _allowDowngrade = false;
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
