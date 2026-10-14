#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Checking, loading versions, staging and installing; this dialog shows what they report.
    private UpdateFlow _updates = null!;
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
    private string? _selectedSlug;
    private bool _working;
    private bool _swapping;
    private readonly PluginOperationGate _pluginOperation = new();
    // A build of this assembly waits for Godot's hot reload; see AssemblyReload. Plugin operations wait for it.
    private bool _reloadPending;
    private double _reloadPoll;
    private const double ReloadPollSeconds = 0.25;
    private const string ReloadNotice = "Waiting for the editor to load the rebuilt C# code...";
    private const string ReloadFailedNotice = "The editor has not finished loading the rebuilt C# code. Restart the editor to continue.";
    // Rendering runs after async operations and on theme changes; keep their result until the next update operation.
    private string? _operationNotice;

    /// <summary>False once an assembly reload dropped the C# state of this still existing editor node.</summary>
    public bool IsInitialized => _global is not null;

    /// <summary>
    /// An operation runs, or the editor is about to reload the assembly an earlier one built. Asked directly rather than
    /// from the last poll, so a click right after an operation finished cannot slip in before the reload.
    /// </summary>
    private bool Busy => !IsInitialized || _working || _pluginOperation.IsBusy || _global.IsChangingPlugins ||
        (_reloadPending = AssemblyReload.IsPending);

    public static EPluginManagerDialog Create(EGlobal global, CancellationToken lifetime)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<EPluginManagerDialog>();
        dialog.Initialize(global, lifetime);
        return dialog;
    }

    private void Initialize(EGlobal global, CancellationToken lifetime)
    {
        _global = global; _lifetime = lifetime;
        _updates = new UpdateFlow(global, ReviewLicenses, text => ActivationProgress.Begin(text));
        _tree = GetNode<Tree>("%PluginTree"); _status = GetNode<Label>("%Status");
        _check = GetNode<Button>("%CheckButton"); _retry = GetNode<Button>("%RetryButton"); _release = GetNode<Button>("%ReleaseButton");
        _trust = GetNode<CheckBox>("%TrustCheck");
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
        _signals.Connect(_versionConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(() =>
        {
            if (_pendingVersion is { } version) Install(new([version], true, _pendingDowngrade, _pendingReinstall));
        }));
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
        _operationNotice = null;
        Refresh();
        PopupCenteredClamped(Size, 0.9f);
        GetCancelButton().GrabFocus();
    }

    public void Refresh()
    {
        if (_working) return;
        _updates.Clear();
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
        var editable = !Busy && _updates.Staged is null;
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
        _status.Text = (_operationNotice ?? (count == 0 ? "" : $"{count} update{(count == 1 ? "" : "s")} available · ") +
                       "Last checked: " + (_global.LastUpdateCheck?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "never") +
                       (_global.IsIndexingLocalSources ? " · Indexing local directories..." : "")) +
                       ((_reloadPending = AssemblyReload.IsPending) ? " · " + ReloadNotice
                           : _pluginOperation.IsAwaitingReload ? " · " + ReloadFailedNotice : "");
        _status.TooltipText = _status.Text;
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
        var reviewed = _updates.Request?.VersionInstall == true ? _updates.Staged?.FirstOrDefault(p => p.Candidate.Slug == slug)?.Findings : null;
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
            var remote = _updates.RemoteVersions.GetValueOrDefault(target.Slug) ?? [];
            string? missing = null;
            if (target.UpdateUrls.Count > 0 && !_updates.RemoteVersions.ContainsKey(target.Slug))
            {
                if (_updates.RemoteVersionErrors.TryGetValue(target.Slug, out var error)) missing = "Versions from the update site are unavailable: " + error;
                else { missing = "Loading versions from the update site..."; Run(LoadVersionsAsync(target), "Loading versions"); }
            }
            if (local.Count == 0 && missing is not null)
            {
                var loading = _updates.IsLoadingVersions(target.Slug);
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
                _versionSelect.Disabled = Busy || _versionOptions.Count < 2;
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

    private async Task LoadVersionsAsync(PluginUpdateTarget target)
    {
        if (!await _updates.LoadVersionsAsync(target, _lifetime) || !IsShown) return;
        var slug = target.Slug;
        // A newer listed version is now a pending update; read the list again so the plugin shows it. Never discard a
        // staged batch awaiting review or interrupt work.
        var pending = _global.PendingUpdates.FirstOrDefault(c => c.Slug == slug);
        if (!_working && _updates.Staged is null && pending?.NewVersion != _model.Find(slug)?.Update?.Candidate.NewVersion) Refresh();
        else if (_selectedSlug == slug) RenderVersions(_model.Find(slug));
    }

    /// <summary>The dialog still exists in the editor; asynchronous work that finishes after it was closed shows nothing.</summary>
    private bool IsShown => GodotObject.IsInstanceValid(this) && IsInsideTree();

    /// <summary>Starts asynchronous work from a signal; an unexpected failure is logged instead of crashing the editor.</summary>
    private static void Run(Task task, string what) => ObservedTask.Run(task, ex => GD.PushError($"{what} failed: {ex.Message}"));

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
        _installVersion.Disabled = blocked is not null || Busy || _updates.Staged is not null;
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
        if (row is null || option?.Candidate is null || PluginManagerViewModel.VersionChangeBlocked(row, option) is not null || Busy) return;
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
            "Open scenes are saved first; files and project references are backed up" + _global.DetailsBuildFailurePolicy(row.Plugin.Slug) switch
            {
                "ask" => ". If the project does not build with it, you choose whether to keep it and fix the errors yourself or to restore the previous version.",
                "keep" => ". If the project does not build with it, it is kept so you can fix the errors (project setting eplugin/updates/on_build_failure).",
                _ => " and restored if installation fails.",
            };
        _versionConfirm.PopupCentered();
    }

    private void Buttons()
    {
        var busy = Busy;
        // Set native input state before queuing the operation. It remains locked even while C# state is reloaded.
        GuiDisableInput = _pluginOperation.IsBusy || _global.IsChangingPlugins || _reloadPending;
        var staged = _updates.Staged is not null;
        var editable = !busy && !staged;
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
            item.SetEditable(EnabledColumn, editable && row is { CanToggle: true });
            if (row?.Update is { } update) item.SetEditable(VersionColumn, editable && !update.HasError);
        }
        GetNode<Button>("%LocalSourcesButton").Disabled = busy;
        GetNode<Button>("%UpdateSitesButton").Disabled = busy;
        _trust.Disabled = busy;
        _versionSelect.Disabled = busy || _versionOptions.Count < 2;
        GetOkButton().Text = !staged ? _model.OkText : _updates.Request?.VersionInstall == true ? "Install reviewed version" : "Install reviewed updates";
        GetOkButton().Disabled = busy || !CanProceed;
        GetOkButton().Visible = _model.Updates.Count > 0 || staged;
        GetCancelButton().Disabled = _swapping || _pluginOperation.IsBusy || _global.IsChangingPlugins || _reloadPending;
        GetCancelButton().Text = _working && !_swapping ? "Cancel" : "Close";
        _trust.Visible = NeedsTrust;
        _check.Disabled = busy || staged;
        _retry.Disabled = busy || staged || !_model.CanRetry;
        VersionButtons();
    }

    private bool NeedsTrust => _updates.Staged is not null ? _updates.StagedNeedsTrust : _model.RequiresTrust;
    private bool CanProceed => _updates.Staged is not null ? !NeedsTrust || _model.TrustChangedSource : _model.CanApply;

    private static string? SlugOf(TreeItem? item) =>
        item?.GetMetadata(NameColumn).VariantType == Variant.Type.String ? item.GetMetadata(NameColumn).AsString() : null;

    private void ItemEdited()
    {
        if (!IsInitialized) return;
        var item = _tree.GetEdited();
        var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
        if (item is null || row is null) return;
        if (Busy || _updates.Staged is not null)
        {
            // Undo the click; the tree is rebuilt outside its own edit signal.
            if (_reloadPending) _status.Text = ReloadNotice;
            CallDeferred(MethodName.Render);
            return;
        }
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
            if (!BeginPluginOperation()) return;
            _status.Text = (enable ? "Enabling " : "Disabling ") + row.Plugin.Name + "...";
            // Method calls rather than delegates, which Godot cannot carry over an assembly reload.
            if (enable) CallDeferred(MethodName.EnableAfterLicenses, row.Plugin.Slug, row.Plugin.Name);
            else CallDeferred(MethodName.SetPluginEnabled, row.Plugin.Slug, row.Plugin.Name, false);
        }
    }

    private bool BeginPluginOperation()
    {
        if (Busy || !_pluginOperation.TryBegin()) return false;
        AssemblyReload.Capture();
        _working = true;
        Buttons();
        return true;
    }

    private void CompletePluginOperation()
    {
        // A dependency's deferred callback can keep the activation open after SetPluginEnabled returns.
        if (_global.IsChangingPlugins)
        {
            _working = false;
            Buttons();
            return;
        }
        _reloadPending = AssemblyReload.IsPending;
        _pluginOperation.Complete(_reloadPending);
        _working = false;
        // A pending reload keeps the old native UI locked. Its replacement is initialized after the reload.
        // Do not reopen it if the timestamp guard times out: another operation would still run on the old code.
        if (GodotObject.IsInstanceValid(this) && IsInsideTree()) Refresh();
    }

    /// <summary>
    /// Asks for the licenses of the plugin and of the dependencies enabled with it first; a declined license leaves the
    /// plugin disabled.
    /// </summary>
    private void EnableAfterLicenses(string slug, string name)
    {
        // An assembly reload replaced this dialog since the click; its successor shows the current state.
        if (!IsInitialized || !_pluginOperation.IsRunning) return;
        LicenseReview? review;
        try { review = _global.ReviewActivationLicenses(slug, name); }
        catch (Exception ex)
        {
            GD.PushError($"Cannot read the licenses of {slug}: {ex.Message}");
            CompletePluginOperation();
            return;
        }
        if (review is null) { SetPluginEnabled(slug, name, true); return; }
        _status.Text = $"Review the license to enable {name}.";
        LicenseDialog.Create(review, decided =>
        {
            _global.RecordLicenses(decided);
            if (decided.Approved.Any()) { CallDeferred(MethodName.SetPluginEnabled, slug, name, true); return; }
            _pluginOperation.Complete(false);
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

    private void SetPluginEnabled(string slug, string name, bool enable)
    {
        if (!IsInitialized || !_pluginOperation.IsRunning) return;
        // Also covers plain plugins; an ePlugin's own activation shares this window.
        using var progress = ActivationProgress.Begin((enable ? "Enabling " : "Disabling ") + name + "...");
        try
        {
            EditorInterface.Singleton.SetPluginEnabled(slug, enable);
            // a plain plugin's dependants and optional recipes, before the list below shows them
            _global.ObservePluginToggles();
        }
        catch (Exception ex) { GD.PushError($"Cannot {(enable ? "enable" : "disable")} {slug}: {ex.Message}"); }
        finally
        {
            // Dependencies and dependants may have been toggled too, so reload the whole list.
            CompletePluginOperation();
        }
    }

    private void DisableFramework()
    {
        if (!BeginPluginOperation()) return;
        // The framework removes this dialog while it is disabled.
        Hide();
        var slug = _model.Plugins.First(p => p.Plugin.Kind == PluginKind.Framework).Plugin.Slug;
        EditorInterface.Singleton.CallDeferred(EditorInterface.MethodName.SetPluginEnabled, slug, false);
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
        if (!IsShown || !Visible || _working || _updates.Staged is not null) return;
        if (_global.IsIndexingLocalSources) Render();
        else Refresh();
    }

    /// <summary>
    /// A plugin's update site changed: versions loaded from its old site are dropped, and the list is read again so
    /// updates found there are no longer offered.
    /// </summary>
    private void UpdateSitesChanged()
    {
        _updates.ClearVersions();
        if (!IsShown || _working || _updates.Staged is not null) return;
        Refresh();
    }

    private void RetryFailed()
    {
        if (_updates.Staged is not null || !BeginPluginOperation()) return;
        try
        {
            using (ActivationProgress.Begin("Retrying failed plugins...")) _global.RetryFailedPlugins();
        }
        finally { CompletePluginOperation(); }
    }

    private void CheckNow() => Run(CheckAsync(), "Checking for updates");

    private async Task CheckAsync()
    {
        if (Busy) return;
        _operationNotice = null;
        _working = true; Buttons(); _status.Text = "Checking for updates...";
        // Remote sites and local ZIPs can take a while; the scope ends after the list is read again.
        using var progress = ActivationProgress.Begin("Checking for updates...");
        CheckOutcome outcome;
        try { outcome = await _updates.CheckAsync(); }
        finally { _working = false; }
        if (!IsShown) return;
        _operationNotice = outcome.Notice;
        if (outcome.IsError) GD.PushError(outcome.Notice);
        Refresh();
    }

    private void Confirm()
    {
        if (_updates is { Staged: not null, Request: { } staged }) Install(staged);
        else if (_model.CanApply) Install(new InstallRequest(_model.Selected));
    }

    private void Install(InstallRequest request) => Run(InstallAsync(request), "Installing updates");

    /// <summary>
    /// Stages and validates the batch, stops for review when the packages carry warnings, then installs. A second call
    /// with the batch already staged continues with the installation.
    /// </summary>
    private async Task InstallAsync(InstallRequest request)
    {
        if (Busy || request.Batch.Count == 0) return;
        _operationNotice = null;
        _working = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        InstallOutcome outcome;
        try
        {
            outcome = await _updates.InstallAsync(request, _model.TrustChangedSource, new InstallView
            {
                Alive = () => !_lifetime.IsCancellationRequested && IsShown,
                Status = text => _status.Text = text,
                Staged = (packages, refused) =>
                {
                    if (request.VersionInstall) return; // the details show the reviewed package of a chosen version
                    foreach (var package in packages)
                        _model.Updates.FirstOrDefault(r => r.Candidate.Slug == package.Candidate.Slug)?.Findings.AddRange(package.Findings);
                    // A refused package stays out of this batch; its details say why.
                    foreach (var package in refused)
                        _model.Updates.FirstOrDefault(r => r.Candidate.Slug == package.Candidate.Slug)?.Findings.Add(new("refused", FindingSeverity.Error, package.Reason));
                },
                Swapping = () => { _swapping = true; Buttons(); }
            }, _cancel.Token);
        }
        finally { _working = false; _swapping = false; }
        if (!IsShown || outcome.Step == InstallStep.Abandoned) return;
        // The build-failure dialog takes over the decision. Otherwise the manager stays open: an update that needs no
        // restart is finished here, and a restart reopens it.
        if (outcome.Step == InstallStep.AwaitingDecision) Hide();
        else
        {
            _operationNotice = outcome.Notice;
            if (outcome.IsError) GD.PushError(outcome.Notice);
        }
        if (outcome.Refresh) Refresh(); else Render();
    }

    private void Cancel()
    {
        if (_swapping || _pluginOperation.IsBusy || _global.IsChangingPlugins || AssemblyReload.IsPending) return;
        _cancel?.Cancel();
        if (!_working) _updates.Clear();
        Hide();
    }

    private static Color ThemeColor(string name, string type, Color fallback)
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        return theme is not null && theme.HasColor(name, type) ? theme.GetColor(name, type) : fallback;
    }
    private static Color UpdateColor() => ThemeColor("success_color", "Editor", new Color(0.45f, 0.95f, 0.5f));
    private static Color ErrorColor() => ThemeColor("error_color", "Editor", new Color(1f, 0.47f, 0.42f));
    private static Color DisabledColor() => ThemeColor("font_disabled_color", "Button", new Color(0.5f, 0.5f, 0.5f));

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (!IsInitialized || !Visible || (_reloadPoll += delta) < ReloadPollSeconds) return;
        _reloadPoll = 0;
        if (_pluginOperation.IsRunning && !_working && !_global.IsChangingPlugins) CompletePluginOperation();
        var pending = AssemblyReload.IsPending;
        var locked = _pluginOperation.IsBusy || _global.IsChangingPlugins || pending;
        if (pending == _reloadPending && locked == GuiDisableInput) return;
        _reloadPending = pending;
        // Not while staging or installing: those render their own progress and refresh when done.
        if (_working || _updates.Staged is not null) Buttons();
        else Render();
    }

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
    public void OnBeforeSerialize()
    {
        GuiDisableInput = true;
        ReleaseCallbacks();
    }
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
        if (!_swapping) _updates?.Clear();
        base._ExitTree();
    }
}

/// <summary>A plugin operation ends only when it needs no reload; a reload creates a new gate with the new dialog.</summary>
internal sealed class PluginOperationGate
{
    public bool IsRunning { get; private set; }
    public bool IsAwaitingReload { get; private set; }
    public bool IsBusy => IsRunning || IsAwaitingReload;

    public bool TryBegin()
    {
        if (IsBusy) return false;
        IsRunning = true;
        return true;
    }

    public void Complete(bool reloadPending)
    {
        if (!IsRunning) return;
        IsRunning = false;
        IsAwaitingReload = reloadPending;
    }
}
#endif
