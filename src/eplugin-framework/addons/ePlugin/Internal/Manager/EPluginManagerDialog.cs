#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Kinds;
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
    private const int EnabledColumn = 0, NameColumn = 1, VersionColumn = 2;
    private const int VersionMinimumWidth = 150;

    private readonly EditorSignalConnections _signals = new();
    private EGlobal _global = null!;
    private CancellationToken _lifetime;
    private CancellationTokenSource? _cancel;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _check = null!;
    private Button _attentionButton = null!;
    private Button _release = null!;
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
    private Button _installZip = null!;
    private FileDialog _zipDialog = null!;
    private ConfirmationDialog _installConfirm = null!;
    private RichTextLabel _installText = null!;
    // The selected plugin's update kept with build errors, read when its details are shown.
    private UpdateJournal? _keptUpdate;
    private SettingsDialog _settings = null!;
    private AttentionDialog _attention = null!;
    // The plugins that need attention, read with the list.
    private IReadOnlyList<AttentionItem> _attentionItems = [];
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
    // Only an installation can be cancelled; the dialog has no Close button otherwise.
    private bool _installing;
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
    // The notice of work that ended while the manager was closed: Escape and the window's close button hide it also
    // while its work goes on, so the next Open shows how it ended instead of dropping it.
    private bool _keepNotice;

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
        _updates = new UpdateFlow(global, ReviewUpdates, ReviewLicenses, text => ActivationProgress.Begin(text));
        _tree = GetNode<Tree>("%PluginTree"); _status = GetNode<Label>("%Status");
        _check = GetNode<Button>("%CheckButton"); _attentionButton = GetNode<Button>("%AttentionButton"); _release = GetNode<Button>("%ReleaseButton");
        _disableFrameworkConfirm = GetNode<ConfirmationDialog>("%DisableFrameworkConfirm");
        _detailsIcon = GetNode<TextureRect>("%DetailsIcon"); _detailsName = GetNode<Label>("%DetailsName"); _detailsText = GetNode<RichTextLabel>("%DetailsText");
        _versionSeparator = GetNode<Control>("%VersionSeparator"); _versionRow = GetNode<Control>("%VersionRow");
        _versionSelect = GetNode<OptionButton>("%VersionSelect"); _installVersion = GetNode<Button>("%InstallVersionButton");
        _versionHint = GetNode<Label>("%VersionHint"); _versionConfirm = GetNode<ConfirmationDialog>("%VersionConfirm");
        _installZip = GetNode<Button>("%InstallZipButton"); _zipDialog = GetNode<FileDialog>("%InstallZipDialog");
        _installConfirm = GetNode<ConfirmationDialog>("%InstallConfirm"); _installText = GetNode<RichTextLabel>("%InstallText");
        _settings = GetNode<SettingsDialog>("%SettingsDialog"); _settings.Initialize(global);
        _attention = GetNode<AttentionDialog>("%AttentionDialog"); _attention.Initialize();
        _locationRow = GetNode<Control>("%LocationRow"); _locationLink = GetNode<LinkButton>("%LocationLink"); _openFolder = GetNode<Button>("%OpenFolderButton");
        RefreshTheme();
        _signals.Connect(this, Control.SignalName.ThemeChanged, Callable.From(() => CallDeferred(nameof(RefreshTheme))));

        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(EnabledColumn, "On"); _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(VersionColumn, "Version");
        _tree.SetColumnExpand(EnabledColumn, false); _tree.SetColumnExpand(NameColumn, false); _tree.SetColumnExpand(VersionColumn, true);
        foreach (var window in Windows) EditorWindows.Prepare(window, _signals, this, nameof(QueueWindowIcons));
        var scale = EditorWindows.Scale;
        _tree.SetColumnCustomMinimumWidth(EnabledColumn, (int)(40 * scale));
        _tree.SetColumnCustomMinimumWidth(VersionColumn, (int)(VersionMinimumWidth * scale));
        // The scene's line spacing of the details, which keeps their links apart, is for 100% editor scale.
        _detailsText.AddThemeConstantOverride("line_separation", (int)(_detailsText.GetThemeConstant("line_separation") * scale));

        _signals.Connect(_check, BaseButton.SignalName.Pressed, Callable.From(CheckNow));
        _signals.Connect(GetNode<Button>("%SettingsButton"), BaseButton.SignalName.Pressed,
            Callable.From(() => _settings.Open(_model.Plugins.FirstOrDefault(r => r.Plugin.Kind == PluginKind.Framework)?.Plugin)));
        _signals.Connect(_attentionButton, BaseButton.SignalName.Pressed, Callable.From(() => OpenAttention(null)));
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
        _signals.Connect(_installZip, BaseButton.SignalName.Pressed, Callable.From(ChooseZip));
        _signals.Connect(_zipDialog, FileDialog.SignalName.FileSelected, Callable.From<string>(ZipChosen));
        _signals.Connect(_installConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(ConfirmAddon));
        _signals.Connect(_installConfirm, AcceptDialog.SignalName.Canceled, Callable.From(CancelAddon));
        _signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(() => ShowDetails(SlugOf(_tree.GetSelected()))));
        _signals.Connect(_tree, Tree.SignalName.ItemActivated, Callable.From(OpenRelease));
        _signals.Connect(_tree, Tree.SignalName.ButtonClicked, Callable.From<TreeItem, long, long, long>((item, _, _, _) => item.Select(NameColumn)));
        _signals.Connect(_tree, Tree.SignalName.Resized, Callable.From(() => { if (_model is not null) FitColumns(); }));
        _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(Confirm));
        _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(Cancel));
        _signals.Connect(this, Window.SignalName.CloseRequested, Callable.From(Cancel));
    }

    private Window[] Windows => [this, _versionConfirm, _disableFrameworkConfirm, _zipDialog, _installConfirm];

    private void QueueWindowIcons() => CallDeferred(nameof(RefreshWindowIcons));
    private void RefreshWindowIcons()
    {
        foreach (var window in Windows) EditorWindows.ApplyIcon(window);
    }

    private void RefreshTheme()
    {
        if (!GodotObject.IsInstanceValid(this) || _global is null) return;
        var editorTheme = EditorWindows.Theme;
        if (editorTheme is not null && editorTheme.HasIcon("Folder", "EditorIcons"))
        { _openFolder.Icon = editorTheme.GetIcon("Folder", "EditorIcons"); _openFolder.Text = ""; }
        // The editor's labels indent their text; the details text starts where theirs does.
        var inset = editorTheme is not null && editorTheme.HasStylebox("normal", "Label") ? editorTheme.GetStylebox("normal", "Label").GetMargin(Side.Left) : 0;
        _detailsText.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = inset });
        _ePluginIcon = EditorIcons.EPlugin; _logo = EditorIcons.Logo; _updateIcon = EditorIcons.UpdateIndicator;
        _check.Icon = EditorIcons.Rescan;
        _installZip.Icon = EditorIcons.Install;
        _attentionButton.Icon = EditorIcons.Retry;
        GetNode<Button>("%SettingsButton").Icon = EditorIcons.Settings;
        _settings.RefreshTheme();
        TextColors.Use(EditorWindows.Theme);
        // Render the existing model rather than refreshing it: a theme change must preserve a staged update.
        if (_model is not null) Render();
    }

    public void Open()
    {
        if (!_keepNotice) _operationNotice = null;
        _keepNotice = false;
        Refresh();
        PopupCenteredClamped(Size, 0.9f);
        _tree.GrabFocus();
        // Problems shown already in this editor session wait for the button; new ones open the list on top.
        if (_attentionItems.Any(i => !_global.ShownAttention.Contains(i.Key))) CallDeferred(MethodName.OpenAttention, "");
    }

    /// <summary>Opens the Needs attention dialog at <paramref name="slug"/>, or at its first plugin when null or empty.</summary>
    private void OpenAttention(string? slug)
    {
        if (!IsInitialized || !Visible || _attentionItems.Count == 0) return;
        foreach (var item in _attentionItems) _global.ShownAttention.Add(item.Key);
        _attention.Open(_attentionItems, string.IsNullOrEmpty(slug) ? null : slug);
    }

    public void Refresh()
    {
        if (_working) return;
        // A new add-on staged by Install from ZIP waits for its confirmation, which clears it; e.g. the local index
        // finishing in the meantime must not drop it. Installing checks its folder again.
        if (_updates.StagedAddon is null) _updates.Clear();
        _licenses.Clear(); _welcomes.Clear();
        var plugins = _global.CollectPlugins();
        _targets = _global.CollectUpdateTargets();
        var previous = _model;
        _model = new(plugins, _global.PendingUpdates, _targets, _global.UpdateCache, candidate => _global.UpdatePreflightFindings([candidate]));
        _model.KeepUnselected(previous);
        _attentionItems = PluginAttention.Collect(_model, _global.FailedUpdateJournal, _global.KeptUpdate,
            slug => _global.RetryScope([slug], dependencies: true).Select(a => a.Slug).Where(s => s != slug).ToArray());
        // Update sites, local directories and retries change what the toolbar button shows, too.
        _global.InvalidateManagerStatus();
        if (_attention.Visible) _attention.Update(_attentionItems);
        Render();
    }

    private void Render()
    {
        _tree.Clear(); var root = _tree.CreateItem();
        TreeItem? selected = null;
        var editable = !Busy;
        foreach (var row in _model.Plugins)
        {
            var plugin = row.Plugin;
            var item = _tree.CreateItem(root);
            item.SetMetadata(NameColumn, plugin.Slug);
            item.SetCellMode(EnabledColumn, TreeItem.TreeCellMode.Check);
            item.SetChecked(EnabledColumn, plugin.Enabled);
            item.SetEditable(EnabledColumn, row.CanToggle && editable);
            item.SetTooltipText(EnabledColumn, plugin.Kind == PluginKind.GDExtensionAddon ? "Godot always loads a GDExtension without plugin.cfg; there is nothing to enable or disable."
                : !row.CanToggle ? "The plugin folder or its plugin.cfg is missing."
                : plugin.Enabled ? "Enabled. Uncheck to disable the plugin." : "Disabled. Check to enable the plugin.");
            item.SetText(NameColumn, PluginManagerViewModel.ListName(plugin));
            var updateState = PluginManagerViewModel.UpdateState(row);
            item.SetTooltipText(NameColumn, $"{PluginManagerViewModel.ListName(plugin)} ({PluginDirectory(plugin.Slug)})\n{PluginCatalog.KindName(plugin.Kind)}" +
                (updateState is null ? "" : "\n" + updateState));
            if (row.IsEPlugin && _ePluginIcon is not null) item.SetIcon(NameColumn, _ePluginIcon);
            if (updateState is not null && _updateIcon is not null)
            {
                item.AddButton(NameColumn, _updateIcon, 0, false, updateState);
                item.SetButtonColor(NameColumn, 0, row.Update is { } state ? (state.HasError ? EditorIcons.ErrorColor : EditorIcons.SuccessColor) : EditorIcons.DisabledColor);
            }
            if (!plugin.Enabled) item.SetCustomColor(NameColumn, EditorIcons.DisabledColor);
            if (row.NeedsAttention) item.SetCustomColor(NameColumn, EditorIcons.ErrorColor);
            if (row.Update is { } update)
            {
                // An available update is selected for the batch right where its version change is shown.
                item.SetCellMode(VersionColumn, TreeItem.TreeCellMode.Check);
                item.SetEditable(VersionColumn, !update.HasError && editable);
                item.SetChecked(VersionColumn, update.Selected);
                item.SetText(VersionColumn, $"{PluginManagerViewModel.VersionText(plugin)} → {update.Candidate.NewVersion}");
                if (update.HasError) item.SetCustomColor(VersionColumn, EditorIcons.ErrorColor);
                item.SetTooltipText(VersionColumn, update.HasError ? "This update cannot be installed, see details." : "Check to include this update in Update.");
            }
            else
            {
                item.SetText(VersionColumn, PluginManagerViewModel.VersionText(plugin));
                item.SetTooltipText(VersionColumn, PluginManagerViewModel.StatusText(plugin));
            }
            if (plugin.Slug == _selectedSlug) selected = item;
        }
        FitColumns();
        if (selected is not null) { selected.Select(NameColumn); _tree.ScrollToItem(selected); }
        else ShowDetails(null);
        var count = _model.Updates.Count;
        var next = _global.NextUpdateCheck;
        _status.Text = (_operationNotice ?? (count == 0 ? "" : $"{count} update{(count == 1 ? "" : "s")} available · ") +
                       PluginManagerViewModel.CheckSchedule(_global.LastUpdateCheck, next, DateTimeOffset.UtcNow, CultureInfo.CurrentCulture) +
                       (_global.IsIndexingLocalSources ? " · Indexing local directories..." : "")) +
                       ((_reloadPending = AssemblyReload.IsPending) ? " · " + ReloadNotice
                           : _pluginOperation.IsAwaitingReload ? " · " + ReloadFailedNotice : "");
        _status.TooltipText = _status.Text + "\n" + PluginManagerViewModel.CheckScheduleHint(next);
        Buttons();
    }

    private static string PluginDirectory(string slug) => "res://addons/" + slug;

    /// <summary>
    /// Tree draws cell buttons at the right edge of the cell. Sizing the Plugin column to its longest title keeps the
    /// update icons right after the titles; the Version column takes the remaining width.
    /// </summary>
    private void FitColumns()
    {
        var scale = EditorWindows.Scale;
        var font = _tree.GetThemeFont("font"); var fontSize = _tree.GetThemeFontSize("font_size");
        var separation = _tree.GetThemeConstant("h_separation");
        var margins = _tree.GetThemeConstant("inner_item_margin_left") + _tree.GetThemeConstant("inner_item_margin_right") + 2 * separation;
        float TextWidth(string text) => font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        var name = 0f;
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            var text = TextWidth(item.GetText(NameColumn));
            if (item.GetIcon(NameColumn) is { } icon) text += icon.GetWidth() + separation;
            name = Math.Max(name, text);
        }
        var button = _updateIcon is null ? 0 : _updateIcon.GetWidth() + 4 * separation;
        var desired = name + button + margins;
        // Never wider than the list leaves next to the other columns, or the tree scrolls horizontally.
        if (_tree.Size.X > 0)
        {
            // The Version column's minimum rather than its width, which expands into the rest of the list.
            var others = _tree.GetColumnWidth(EnabledColumn) + VersionMinimumWidth * scale +
                         _tree.GetThemeStylebox("panel").GetMinimumSize().X + 16 * scale;
            desired = Math.Max(Math.Min(desired, _tree.Size.X - others), 120 * scale);
        }
        _tree.SetColumnCustomMinimumWidth(NameColumn, (int)Math.Ceiling(desired));
    }

    private void ShowDetails(string? slug)
    {
        _selectedSlug = slug;
        var row = slug is null ? null : _model.Find(slug);
        _detailsIcon.Texture = row?.IsEPlugin == true ? _logo : null;
        // Without an icon its separation would indent the name further than the lines below.
        _detailsIcon.Visible = _detailsIcon.Texture is not null;
        _detailsName.Text = row is null ? "No plugin selected" : PluginManagerViewModel.DetailsTitle(row.Plugin);
        _keptUpdate = row is null ? null : _global.KeptUpdate(row.Plugin.Slug);
        _detailsText.Text = row is null ? "Select a plugin to see its details." : PluginManagerViewModel.Describe(row, LicenseOf(row), WelcomeOf(row), _keptUpdate);
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
        else if (link == PluginManagerViewModel.AttentionMeta) OpenAttention(_selectedSlug);
    }

    /// <summary>The dialog still exists in the editor; asynchronous work that finishes after it was closed shows nothing.</summary>
    private bool IsShown => GodotObject.IsInstanceValid(this) && IsInsideTree();

    /// <summary>Starts asynchronous work from a signal; an unexpected failure is logged instead of crashing the editor.</summary>
    private static void Run(Task task, string what) => ObservedTask.Run(task, ex => GD.PushError($"{what} failed: {ex.Message}"));

    private void Buttons()
    {
        var busy = Busy;
        // Set native input state before queuing the operation. It remains locked even while C# state is reloaded.
        GuiDisableInput = _pluginOperation.IsBusy || _global.IsChangingPlugins || _reloadPending;
        var editable = !busy;
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
            item.SetEditable(EnabledColumn, editable && row is { CanToggle: true });
            if (row?.Update is { } update) item.SetEditable(VersionColumn, editable && !update.HasError);
        }
        GetNode<Button>("%SettingsButton").Disabled = busy;
        _versionSelect.Disabled = busy || _versionOptions.Count < 2;
        GetOkButton().Text = _model.OkText;
        GetOkButton().Icon = EditorIcons.Upgrade;
        GetOkButton().Disabled = busy || !_model.CanApply;
        GetOkButton().Visible = _model.Updates.Count > 0;
        GetCancelButton().Disabled = _swapping || _pluginOperation.IsBusy || _global.IsChangingPlugins || _reloadPending;
        GetCancelButton().Visible = _installing && !_swapping;
        _check.Disabled = busy;
        _installZip.Disabled = busy;
        _attentionButton.Visible = _attentionItems.Count > 0;
        _attentionButton.Text = PluginAttention.ButtonText(_attentionItems.Count);
        _attentionButton.Disabled = busy;
        _attention.SetBusy(busy);
        VersionButtons();
    }

    private static string? SlugOf(TreeItem? item) =>
        item?.GetMetadata(NameColumn).VariantType == Variant.Type.String ? item.GetMetadata(NameColumn).AsString() : null;

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
        // Never interrupt work; it refreshes when it finishes.
        if (!IsShown || !Visible || _working) return;
        if (_global.IsIndexingLocalSources) Render();
        else Refresh();
    }

    /// <summary>
    /// A plugin's update site or a setting changed: versions loaded from a source no longer used are dropped, and the
    /// list is read again so updates found there are no longer offered and the status line shows the next scheduled check.
    /// </summary>
    private void SettingsChanged(bool sourcesChanged)
    {
        if (sourcesChanged) _updates.ClearVersions();
        if (!IsShown || _working) return;
        Refresh();
    }

    private static Texture2D? VersionIcon(bool downgrade, bool reinstall) =>
        reinstall ? EditorIcons.Reinstall : downgrade ? EditorIcons.Downgrade : EditorIcons.Upgrade;

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
        if (_working) Buttons();
        else Render();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_global is not null) _global.LocalIndexChanged += LocalIndexChanged;
        if (_settings is not null) _settings.Changed += SettingsChanged;
        if (_attention is not null) _attention.Requested += AttentionRequested;
    }

    private void ReleaseCallbacks()
    {
        _signals.Dispose();
        _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null;
        if (_global is not null) _global.LocalIndexChanged -= LocalIndexChanged;
        if (_settings is not null) _settings.Changed -= SettingsChanged;
        if (_attention is not null) _attention.Requested -= AttentionRequested;
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
