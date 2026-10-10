#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Welcomes;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Lists every addon with its details and installs updates. The layout lives in EPluginManagerDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class EPluginManagerDialog : ConfirmationDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/EPluginManagerDialog.tscn";
    private const int EnabledColumn = 0, NameColumn = 1, AuthorColumn = 2, TypeColumn = 3, VersionColumn = 4;
    private const int AuthorMinimumWidth = 100;

    private readonly EditorSignalConnections _signals = new();
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
    private Control _locationRow = null!;
    private LinkButton _locationLink = null!;
    private Button _openFolder = null!;
    private Control _versionSeparator = null!;
    private Control _versionRow = null!;
    private OptionButton _versionSelect = null!;
    private Button _installVersion = null!;
    private Label _versionHint = null!;
    private ConfirmationDialog _versionConfirm = null!;
    private LocalSourcesDialog _localSources = null!;
    private UpdateSitesDialog _updateSites = null!;
    // Versions published at each update_url, loaded on demand and kept until Check for updates. Local versions come from
    // the in-memory index on every render, so a new index never fetches these again.
    private readonly Dictionary<string, IReadOnlyList<UpdateCandidate>> _remoteVersions = [];
    private readonly Dictionary<string, string> _remoteVersionErrors = [];
    private readonly HashSet<string> _loadingVersions = [];
    private IReadOnlyList<PluginUpdateTarget> _targets = [];
    private IReadOnlyList<VersionOption> _versionOptions = [];
    /// <summary>A note under the version choice when nothing more specific is shown, e.g. why remote versions are missing.</summary>
    private string? _versionNote;
    // Read on selection rather than for every row: a disabled ePlugin's recipe needs a throw-away instance of its plugin.
    private readonly Dictionary<string, LicenseInfo?> _licenses = [];
    private readonly Dictionary<string, WelcomeEntry?> _welcomes = [];
    private UpdateCandidate? _pendingVersion;
    private bool _pendingDowngrade;
    private bool _pendingReinstall;
    private Texture2D? _ePluginIcon;
    private Texture2D? _logo;
    private Texture2D? _updateIcon;
    private PluginManagerViewModel _model = null!;
    private IReadOnlyList<ValidatedPackage>? _staged;
    // What is being staged/installed: the checked updates, or one explicitly chosen version.
    private IReadOnlyList<UpdateCandidate> _batch = [];
    private bool _versionInstall;
    private bool _allowDowngrade;
    private bool _allowReinstall;
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
        _localSources = GetNode<LocalSourcesDialog>("%LocalSourcesDialog"); _localSources.Initialize(global);
        _updateSites = GetNode<UpdateSitesDialog>("%UpdateSitesDialog"); _updateSites.Initialize(global);
        _locationRow = GetNode<Control>("%LocationRow"); _locationLink = GetNode<LinkButton>("%LocationLink"); _openFolder = GetNode<Button>("%OpenFolderButton");
        RefreshTheme();
        _signals.Connect(this, Control.SignalName.ThemeChanged, Callable.From(() => CallDeferred(nameof(RefreshTheme))));

        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(EnabledColumn, "On"); _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(AuthorColumn, "Author");
        _tree.SetColumnTitle(TypeColumn, "Type"); _tree.SetColumnTitle(VersionColumn, "Version");
        _tree.SetColumnExpand(EnabledColumn, false); _tree.SetColumnExpand(NameColumn, false); _tree.SetColumnExpand(AuthorColumn, true);
        _tree.SetColumnExpand(TypeColumn, false); _tree.SetColumnExpand(VersionColumn, false);
        foreach (var window in new Window[] { this, _versionConfirm, _disableFrameworkConfirm }) EditorWindows.Prepare(window, _signals, this, nameof(QueueWindowIcons));
        var scale = EditorInterface.Singleton.GetEditorScale();
        _tree.SetColumnCustomMinimumWidth(EnabledColumn, (int)(40 * scale)); _tree.SetColumnCustomMinimumWidth(AuthorColumn, (int)(AuthorMinimumWidth * scale));
        _tree.SetColumnCustomMinimumWidth(VersionColumn, (int)(150 * scale));

        _signals.Connect(_check, BaseButton.SignalName.Pressed, Callable.From(CheckNow));
        _signals.Connect(GetNode<Button>("%LocalSourcesButton"), BaseButton.SignalName.Pressed, Callable.From(_localSources.Open));
        _signals.Connect(GetNode<Button>("%UpdateSitesButton"), BaseButton.SignalName.Pressed, Callable.From(_updateSites.Open));
        _signals.Connect(_retry, BaseButton.SignalName.Pressed, Callable.From(RetryFailed));
        _signals.Connect(_release, BaseButton.SignalName.Pressed, Callable.From(OpenRelease));
        _signals.Connect(_locationLink, BaseButton.SignalName.Pressed, Callable.From(() => { if (_selectedSlug is not null) EditorInterface.Singleton.SelectFile(PluginDirectory(_selectedSlug)); }));
        _signals.Connect(_openFolder, BaseButton.SignalName.Pressed, Callable.From(() => { if (_selectedSlug is not null) OS.ShellShowInFileManager(ProjectSettings.GlobalizePath(PluginDirectory(_selectedSlug)), true); }));
        _signals.Connect(_detailsText, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(DetailsLinkClicked));
        _signals.Connect(_tree, Tree.SignalName.ItemEdited, Callable.From(ItemEdited));
        _signals.Connect(_disableFrameworkConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(DisableFramework));
        _signals.Connect(_versionSelect, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => VersionButtons()));
        _signals.Connect(_installVersion, BaseButton.SignalName.Pressed, Callable.From(ConfirmVersion));
        _signals.Connect(_versionConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(() => { if (_pendingVersion is { } version) Install([version], true, _pendingDowngrade, _pendingReinstall); }));
        _signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(() => ShowDetails(SlugOf(_tree.GetSelected()))));
        _signals.Connect(_tree, Tree.SignalName.ItemActivated, Callable.From(OpenRelease));
        _signals.Connect(_tree, Tree.SignalName.ButtonClicked, Callable.From<TreeItem, long, long, long>((item, _, _, _) => item.Select(NameColumn)));
        _signals.Connect(_tree, Tree.SignalName.Resized, Callable.From(() => { if (_model is not null) FitColumns(); }));
        _signals.Connect(_trust, BaseButton.SignalName.Toggled, Callable.From<bool>(value => { _model.TrustChangedSource = value; Buttons(); }));
        _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(Confirm));
        _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(Cancel));
        _signals.Connect(this, Window.SignalName.CloseRequested, Callable.From(Cancel));
    }

    private void QueueWindowIcons() => CallDeferred(nameof(RefreshWindowIcons));
    private void RefreshWindowIcons()
    {
        foreach (var window in new Window[] { this, _versionConfirm, _disableFrameworkConfirm })
            EditorWindows.ApplyIcon(window);
    }

    private void RefreshTheme()
    {
        if (!GodotObject.IsInstanceValid(this) || _global is null) return;
        if (EditorInterface.Singleton.GetEditorTheme() is { } editorTheme && editorTheme.HasIcon("Folder", "EditorIcons"))
        { _openFolder.Icon = editorTheme.GetIcon("Folder", "EditorIcons"); _openFolder.Text = ""; }
        _ePluginIcon = EditorIcons.EPlugin; _logo = EditorIcons.Logo; _updateIcon = EditorIcons.UpdateIndicator;
        _check.Icon = EditorIcons.Update;
        // Render the existing model rather than refreshing it: a theme change must preserve a staged update.
        if (_model is not null) Render();
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
        _licenses.Clear(); _welcomes.Clear();
        var plugins = _global.CollectPlugins();
        _targets = _global.CollectUpdateTargets();
        _model = new(plugins, _global.PendingUpdates, _targets, _global.UpdateCache, candidate => _global.UpdatePreflightFindings([candidate]));
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
            var updateState = PluginManagerViewModel.UpdateState(row);
            item.SetTooltipText(NameColumn, $"{plugin.Name} ({PluginDirectory(plugin.Slug)})" + (updateState is null ? "" : "\n" + updateState));
            if (row.IsEPlugin && _ePluginIcon is not null) item.SetIcon(NameColumn, _ePluginIcon);
            if (updateState is not null && _updateIcon is not null)
            {
                item.AddButton(NameColumn, _updateIcon, 0, false, updateState);
                item.SetButtonColor(NameColumn, 0, row.Update is { } state ? (state.HasError ? ErrorColor() : UpdateColor()) : DisabledColor());
            }
            if (!plugin.Enabled) item.SetCustomColor(NameColumn, DisabledColor());
            if (plugin.FailedAttempt is not null || plugin.State == EEditorPluginState.Error) item.SetCustomColor(NameColumn, ErrorColor());
            item.SetText(AuthorColumn, plugin.Author);
            if (!string.IsNullOrWhiteSpace(plugin.Author)) item.SetTooltipText(AuthorColumn, plugin.Author);
            item.SetText(TypeColumn, PluginCatalog.KindName(plugin.Kind));
            if (row.Update is { } update)
            {
                // An available update is selected for the batch right where its version change is shown.
                item.SetCellMode(VersionColumn, TreeItem.TreeCellMode.Check);
                item.SetEditable(VersionColumn, !update.HasError && editable);
                item.SetChecked(VersionColumn, update.Selected);
                item.SetText(VersionColumn, $"{plugin.Version} → {update.Candidate.NewVersion}");
                if (update.HasError) item.SetCustomColor(VersionColumn, ErrorColor());
                item.SetTooltipText(VersionColumn, update.HasError ? "This update cannot be installed, see details." : "Check to include this update in Update.");
            }
            else
            {
                item.SetText(VersionColumn, plugin.Version);
                item.SetTooltipText(VersionColumn, PluginManagerViewModel.StatusText(plugin));
            }
            if (plugin.Slug == _selectedSlug) selected = item;
        }
        FitColumns();
        if (selected is not null) { selected.Select(NameColumn); _tree.ScrollToItem(selected); }
        else ShowDetails(null);
        var count = _model.Updates.Count;
        _status.Text = (count == 0 ? "" : $"{count} update{(count == 1 ? "" : "s")} available · ") +
                       "Last checked: " + (_global.LastUpdateCheck?.ToLocalTime().ToString("g") ?? "never") +
                       (_global.IsIndexingLocalSources ? " · Indexing local directories..." : "");
        Buttons();
    }

    private static string PluginDirectory(string slug) => "res://addons/" + slug;

    /// <summary>
    /// Tree draws cell buttons at the right edge of the cell. Sizing the Plugin column to its longest title keeps the
    /// update icons right after the titles; the Type column fits its texts and the Author column takes the remaining width.
    /// </summary>
    private void FitColumns()
    {
        var scale = EditorInterface.Singleton.GetEditorScale();
        var font = _tree.GetThemeFont("font"); var fontSize = _tree.GetThemeFontSize("font_size");
        var separation = _tree.GetThemeConstant("h_separation");
        var margins = _tree.GetThemeConstant("inner_item_margin_left") + _tree.GetThemeConstant("inner_item_margin_right") + 2 * separation;
        float TextWidth(string text) => font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        float name = 0f, type = TextWidth(_tree.GetColumnTitle(TypeColumn));
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            var text = TextWidth(item.GetText(NameColumn));
            if (item.GetIcon(NameColumn) is { } icon) text += icon.GetWidth() + separation;
            name = Math.Max(name, text);
            type = Math.Max(type, TextWidth(item.GetText(TypeColumn)));
        }
        var typeWidth = type + margins;
        var button = _updateIcon is null ? 0 : _updateIcon.GetWidth() + 4 * separation;
        var desired = name + button + margins;
        // Never wider than the list leaves next to the other columns, or the tree scrolls horizontally.
        if (_tree.Size.X > 0)
        {
            var others = _tree.GetColumnWidth(EnabledColumn) + _tree.GetColumnWidth(VersionColumn) + typeWidth + AuthorMinimumWidth * scale +
                         _tree.GetThemeStylebox("panel").GetMinimumSize().X + 16 * scale;
            desired = Math.Max(Math.Min(desired, _tree.Size.X - others), 120 * scale);
        }
        _tree.SetColumnCustomMinimumWidth(NameColumn, (int)Math.Ceiling(desired));
        _tree.SetColumnCustomMinimumWidth(TypeColumn, (int)Math.Ceiling(typeWidth));
    }

    private void ShowDetails(string? slug)
    {
        _selectedSlug = slug;
        var row = slug is null ? null : _model.Find(slug);
        _detailsIcon.Texture = row?.IsEPlugin == true ? _logo : null;
        _detailsName.Text = row?.Plugin.Name ?? "No plugin selected";
        var reviewed = _versionInstall ? _staged?.FirstOrDefault(p => p.Candidate.Slug == slug)?.Findings : null;
        _detailsText.Text = row is null ? "Select a plugin to see its details." : PluginManagerViewModel.Describe(row, reviewed, LicenseOf(row), WelcomeOf(row));
        _release.Visible = IsSafeUrl(row?.Update?.Candidate.ReleaseUrl);
        _locationRow.Visible = row is not null;
        if (row is not null)
        {
            var directory = PluginDirectory(row.Plugin.Slug);
            _locationLink.Text = directory;
            _locationLink.Disabled = _openFolder.Disabled = row.Plugin.Missing || !DirAccess.DirExistsAbsolute(directory);
        }
        RenderVersions(row);
    }

    private LicenseInfo? LicenseOf(PluginRow row)
    {
        if (row.Plugin.Missing) return null;
        if (_licenses.TryGetValue(row.Plugin.Slug, out var license)) return license;
        try { license = _global.DescribeLicense(row.Plugin.Slug); }
        catch (Exception ex) { GD.PushError($"Cannot read the license of {row.Plugin.Slug}: {ex.Message}"); license = null; }
        return _licenses[row.Plugin.Slug] = license;
    }

    private WelcomeEntry? WelcomeOf(PluginRow row)
    {
        if (row.Plugin.Missing) return null;
        if (_welcomes.TryGetValue(row.Plugin.Slug, out var welcome)) return welcome;
        try { welcome = _global.DescribeWelcome(row.Plugin.Slug); }
        catch (Exception ex) { GD.PushError($"Cannot read the welcome page of {row.Plugin.Slug}: {ex.Message}"); welcome = null; }
        return _welcomes[row.Plugin.Slug] = welcome;
    }

    private void DetailsLinkClicked(Variant meta)
    {
        var link = meta.AsString();
        if (PluginManagerViewModel.IsWebUrl(link)) OS.ShellOpen(link);
        else if (link == PluginManagerViewModel.LicenseMeta && _selectedSlug is not null && _model.Find(_selectedSlug) is { } row &&
                 LicenseOf(row) is { Entry.Problem: null } license)
            LicenseDialog.CreateViewer(license).Open();
        else if (link == PluginManagerViewModel.WelcomeMeta && _selectedSlug is not null && _model.Find(_selectedSlug) is { } shown &&
                 WelcomeOf(shown) is { Problem: null } welcome)
            WelcomeDialog.Create([welcome]).Open();
    }

    /// <summary>
    /// The version choice of an updatable plugin: the versions in the local plugin directories, plus those of its
    /// update_url once they are loaded. Without the update site's versions the local ones are still offered.
    /// </summary>
    private void RenderVersions(PluginRow? row)
    {
        var visible = row is { IsUpdatable: true, Plugin.Missing: false };
        _versionSeparator.Visible = _versionRow.Visible = visible;
        _versionSelect.Clear(); _versionOptions = []; _versionNote = null;
        var target = visible ? _targets.FirstOrDefault(t => t.Slug == row!.Plugin.Slug) : null;
        if (!visible) { }
        else if (!row!.Plugin.Enabled || target is null) SetVersionPlaceholder(row.Plugin.Version + " (installed)", "Enable the plugin to change its version.");
        else
        {
            var local = _global.ListLocalVersions(target);
            var remote = _remoteVersions.GetValueOrDefault(target.Slug) ?? [];
            string? missing = null;
            if (target.UpdateUrls.Count > 0 && !_remoteVersions.ContainsKey(target.Slug))
            {
                if (_remoteVersionErrors.TryGetValue(target.Slug, out var error)) missing = "Versions from the update site are unavailable: " + error;
                else { missing = "Loading versions from the update site..."; LoadVersions(target); }
            }
            if (local.Count == 0 && missing is not null)
            {
                var loading = _loadingVersions.Contains(target.Slug);
                SetVersionPlaceholder(loading ? "Loading versions..." : row.Plugin.Version + " (installed)", loading ? null : missing);
            }
            else
            {
                _versionOptions = PluginManagerViewModel.VersionOptions(row.Plugin.Version, UpdateService.MergeVersions(local, remote));
                var latest = _versionOptions.FirstOrDefault(o => !o.Installed && o.Candidate is not null && !o.IsDowngrade);
                for (var i = 0; i < _versionOptions.Count; i++)
                {
                    var option = _versionOptions[i];
                    _versionSelect.AddItem(PluginManagerViewModel.VersionLabel(option, option == latest), i);
                    if (PluginManagerViewModel.VersionSource(option) is { } source) _versionSelect.SetItemTooltip(i, source);
                    if (option.Installed) _versionSelect.Select(i);
                }
                _versionSelect.Disabled = _versionOptions.Count < 2;
                _versionNote = missing;
            }
        }
        VersionButtons();
    }

    private void SetVersionPlaceholder(string text, string? note)
    {
        _versionSelect.AddItem(text); _versionSelect.Select(0); _versionSelect.Disabled = true;
        _versionNote = note;
    }

    private async void LoadVersions(PluginUpdateTarget target)
    {
        var slug = target.Slug;
        if (!_loadingVersions.Add(slug)) return;
        try { _remoteVersions[slug] = await _global.ListRemoteVersionsAsync(target, _lifetime); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { _remoteVersionErrors[slug] = ex.Message; }
        finally { _loadingVersions.Remove(slug); }
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
        // A newer listed version is now a pending update; read the list again so the plugin shows it. Never discard a
        // staged batch awaiting review or interrupt work.
        var pending = _global.PendingUpdates.FirstOrDefault(c => c.Slug == slug);
        if (!_working && _staged is null && pending?.NewVersion != _model.Find(slug)?.Update?.Candidate.NewVersion) Refresh();
        else if (_selectedSlug == slug) RenderVersions(_model.Find(slug));
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
        if (row is null || option is null) { _installVersion.Disabled = true; _installVersion.Text = "Update"; ShowVersionHint(_versionNote); return; }
        var blocked = PluginManagerViewModel.VersionChangeBlocked(row, option);
        _installVersion.Text = option.Action;
        _installVersion.Disabled = blocked is not null || _working || _staged is not null;
        _installVersion.TooltipText = blocked ?? (option.IsReinstall
            ? $"Install version {option.Version} of {row.Plugin.Name} again, replacing its files."
            : $"Install version {option.Version} of {row.Plugin.Name}.");
        ShowVersionHint((option.Installed ? null : blocked) ?? _versionNote);
    }

    private void ShowVersionHint(string? hint) { _versionHint.Text = hint ?? ""; _versionHint.Visible = hint is not null; }

    private void ConfirmVersion()
    {
        var row = _selectedSlug is null ? null : _model.Find(_selectedSlug);
        var option = SelectedVersion();
        if (row is null || option?.Candidate is null || PluginManagerViewModel.VersionChangeBlocked(row, option) is not null || _working) return;
        // The listed candidate was built when the list was loaded; the installed version may have changed since.
        _pendingVersion = option.Candidate with { InstalledVersion = row.Plugin.Version };
        _pendingDowngrade = option.IsDowngrade; _pendingReinstall = option.IsReinstall;
        _versionConfirm.OkButtonText = option.Action;
        _versionConfirm.DialogText = (option.IsReinstall
                ? $"Reinstall {row.Plugin.Name} {row.Plugin.Version}?\n\n{PluginManagerViewModel.VersionSource(option)}. Its files replace the installed ones: " +
                  "this brings in new commits of a git branch and undoes changes made to the plugin's folder in the project.\n\n"
                : $"Replace {row.Plugin.Name} {row.Plugin.Version} with version {option.Version}?\n\n") +
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
        if (_tree.GetEditedColumn() == VersionColumn && row.Update is { } update)
        {
            update.Selected = item.IsChecked(VersionColumn);
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
            if (enable) Callable.From(() => EnableAfterLicenses(row.Plugin.Slug, row.Plugin.Name)).CallDeferred();
            else Callable.From(() => SetPluginEnabled(row.Plugin.Slug, false)).CallDeferred();
        }
    }

    /// <summary>
    /// Asks for the licenses of the plugin and of the dependencies enabled with it first; a declined license leaves the
    /// plugin disabled.
    /// </summary>
    private void EnableAfterLicenses(string slug, string name)
    {
        LicenseReview? review;
        try { review = _global.ReviewActivationLicenses(slug, name); }
        catch (Exception ex)
        {
            GD.PushError($"Cannot read the licenses of {slug}: {ex.Message}");
            _working = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Refresh();
            return;
        }
        if (review is null) { SetPluginEnabled(slug, true); return; }
        _status.Text = $"Review the license to enable {name}.";
        LicenseDialog.Create(review, decided =>
        {
            _global.RecordLicenses(decided);
            if (decided.Approved.Any()) { Callable.From(() => SetPluginEnabled(slug, true)).CallDeferred(); return; }
            _working = false;
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
            Refresh();
            _status.Text = $"{name} was not enabled: a license it needs was declined.";
        }).Open();
    }

    private static Task<LicenseReview> ReviewLicenses(LicenseReview review)
    {
        // continue the installation after the dialog's signal, not inside it
        var decided = new TaskCompletionSource<LicenseReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        LicenseDialog.Create(review, decided.SetResult).Open();
        return decided.Task;
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

    /// <summary>A new local index can add or remove local versions and updates, so the lists are read again.</summary>
    private void LocalIndexChanged()
    {
        // Never discard a staged batch awaiting review or interrupt work; those refresh when they finish.
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || !Visible || _working || _staged is not null) return;
        if (_global.IsIndexingLocalSources) Render();
        else Refresh();
    }

    /// <summary>
    /// A plugin's update site changed: versions loaded from its old site are dropped, and the list is read again so
    /// updates found there are no longer offered.
    /// </summary>
    private void UpdateSitesChanged()
    {
        _remoteVersions.Clear(); _remoteVersionErrors.Clear();
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || _working || _staged is not null) return;
        Refresh();
    }

    private void RetryFailed()
    {
        if (_working || _staged is not null) return;
        _global.RetryFailedPlugins();
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Refresh();
    }

    private async void CheckNow()
    {
        if (_working) return;
        ClearStaging(); _remoteVersions.Clear(); _remoteVersionErrors.Clear();
        _working = true; Buttons(); _status.Text = "Checking for updates...";
        string? failure = null, notice = null;
        try
        {
            var result = await _global.CheckForUpdatesAsync(true);
            if (result.Failures.Count > 0) failure = string.Join("; ", result.Failures.Select(f => $"{f.Slug}: {f.Message}"));
            if (result.Fallbacks is { Count: > 0 } fallbacks)
                notice = "The update site of " + string.Join(", ", fallbacks.Select(f => f.Slug)) + " did not work; plugin.cfg's update_url was used (see the output log).";
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            _working = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                Refresh();
                if (failure is not null) _status.Text = "Update check failed: " + failure;
                else if (notice is not null) _status.Text = notice;
            }
        }
    }

    private void Confirm()
    {
        if (_staged is not null) Install(_batch, _versionInstall, _allowDowngrade, _allowReinstall);
        else if (_model.CanApply) Install(_model.Selected, false, false, false);
    }

    /// <summary>
    /// Stages and validates the batch, stops for review when the packages carry warnings, then installs. A second call
    /// with the batch already staged continues with the installation.
    /// </summary>
    private async void Install(IReadOnlyList<UpdateCandidate> batch, bool versionInstall, bool allowDowngrade, bool allowReinstall)
    {
        if (_working || batch.Count == 0) return;
        _batch = batch; _versionInstall = versionInstall; _allowDowngrade = allowDowngrade; _allowReinstall = allowReinstall;
        _working = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        var refresh = false;
        string? notice = null;
        try
        {
            if (_staged is null)
            {
                _progress.Visible = true; _status.Text = versionInstall ? $"Downloading and validating {batch[0].PluginName} {batch[0].NewVersion}..." : "Downloading and validating selected addons...";
                var relay = new InlineProgress(value => Callable.From(() => { if (GodotObject.IsInstanceValid(this)) _progress.Value = value * 100; }).CallDeferred());
                var staged = await _global.StageUpdatesAsync(batch, relay, _cancel.Token, allowDowngrade, allowReinstall);
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
            var selected = _staged.Where(p => batch.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
            // An update that keeps the accepted license file is not asked about again; a changed or new one is.
            if (_global.ReviewUpdateLicenses(selected) is { } licenses)
            {
                _status.Text = "Review the licenses of the updates.";
                var decided = await ReviewLicenses(licenses);
                _global.RecordLicenses(decided);
                var declined = decided.Canceled.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
                selected = selected.Where(p => !declined.Contains(p.Candidate.Slug)).ToArray();
                if (_lifetime.IsCancellationRequested || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) { ClearStaging(); return; }
                if (selected.Length == 0)
                {
                    notice = "Update canceled: its license was declined. Addon files were not changed.";
                    ClearStaging();
                    return;
                }
                _cancel.Token.ThrowIfCancellationRequested();
            }
            _swapping = true; Buttons(); _status.Text = versionInstall ? "Installing the selected version..." : "Installing selected addons...";
            var outcome = await _global.ApplyUpdatesAsync(selected, _directory!, _model.TrustChangedSource);
            _directory = null; _staged = null; Hide();
            // Installed versions changed, so the list is read again rather than only redrawn.
            refresh = outcome == UpdateOutcome.Completed;
        }
        catch (OperationCanceledException) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Download canceled. Addon files were not changed."; ClearStaging(); }
        catch (Exception ex) { if (GodotObject.IsInstanceValid(this) && IsInsideTree()) _status.Text = "Update failed: " + ex.Message; if (!_swapping) ClearStaging(); }
        finally
        {
            _working = false; _swapping = false;
            if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                _progress.Visible = false; if (refresh) Refresh(); else Render();
                if (notice is not null) _status.Text = notice;
            }
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
        _directory = null; _staged = null; _batch = []; _versionInstall = false; _allowDowngrade = false; _allowReinstall = false;
    }

    private static Color ThemeColor(string name, string type, Color fallback)
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        return theme is not null && theme.HasColor(name, type) ? theme.GetColor(name, type) : fallback;
    }
    private static Color UpdateColor() => ThemeColor("success_color", "Editor", new Color(0.45f, 0.95f, 0.5f));
    private static Color ErrorColor() => ThemeColor("error_color", "Editor", new Color(1f, 0.47f, 0.42f));
    private static Color DisabledColor() => ThemeColor("font_disabled_color", "Button", new Color(0.5f, 0.5f, 0.5f));

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_global is not null) _global.LocalIndexChanged += LocalIndexChanged;
        if (_updateSites is not null) _updateSites.Changed += UpdateSitesChanged;
    }

    private void ReleaseCallbacks()
    {
        _signals.Dispose();
        _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null;
        if (_global is not null) _global.LocalIndexChanged -= LocalIndexChanged;
        if (_updateSites is not null) _updateSites.Changed -= UpdateSitesChanged;
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize() => ReleaseCallbacks();
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        // Reload removes the C# script without exiting the native node from the tree.
        if (disposing) ReleaseCallbacks();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        ReleaseCallbacks();
        if (!_swapping) ClearStaging();
        base._ExitTree();
    }
}
#endif
