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

    private readonly DialogLifetime _dialogLifetime = new();
    private EGlobal _global = null!;
    private CancellationToken _lifetime;
    private CancellationTokenSource? _cancel;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _check = null!;
    private Button _settingsButton = null!;
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
    private LinkButton _retryVersions = null!;
    private Control _errorBox = null!;
    private RichTextLabel _errorText = null!;
    private Button _openLog = null!;
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
    private readonly Dictionary<string, EEditorPluginRecipe?> _recipes = [];
    private UpdateCandidate? _pendingVersion;
    private bool _pendingDowngrade;
    private bool _pendingReinstall;
    private Texture2D? _ePluginIcon;
    private Texture2D? _logo;
    private Texture2D? _updateIcon;
    private PluginManagerViewModel _model = null!;
    private string? _selectedSlug;
    // Which work runs, whether it waits for a reload, and what is done once it ended.
    private readonly ManagerSession _session = new();
    private double _reloadPoll;
    private const double ReloadPollSeconds = 0.25;
    // Rendering runs after async operations and on theme changes; keep their result until the next update operation.
    private string? _operationNotice;
    // The notice reports a failure: it is shown in the error area, where it wraps and can be selected and copied.
    private bool _noticeIsError;
    // The folder or file with the logs of the failure the notice reports; null when there are none.
    private string? _noticeLog;

    /// <summary>False once an assembly reload dropped the C# state of this still existing editor node.</summary>
    public bool IsInitialized => _global is not null;

    /// <summary>
    /// An operation runs, or the editor is about to reload the assembly an earlier one built. Asked directly rather than
    /// from the last poll, so a click right after an operation finished cannot slip in before the reload.
    /// </summary>
    private bool Busy => !IsInitialized || _session.IsBusy(_global.IsChangingPlugins, AssemblyReload.IsPending);

    /// <summary>
    /// <see cref="Busy"/>, also remembering whether a reload is due, which the status line and the locked input show and
    /// <see cref="_Process"/> compares with to notice a change.
    /// </summary>
    private bool PollBusy() => !IsInitialized || _session.IsBusy(_global.IsChangingPlugins, PollReload());

    /// <summary>Asks whether a reload is due and remembers it, see <see cref="ManagerSession.ReloadPending"/>.</summary>
    private bool PollReload() => _session.ReloadPending = AssemblyReload.IsPending;

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
        _check = GetNode<Button>("%CheckButton"); _settingsButton = GetNode<Button>("%SettingsButton"); _attentionButton = GetNode<Button>("%AttentionButton"); _release = GetNode<Button>("%ReleaseButton");
        _disableFrameworkConfirm = GetNode<ConfirmationDialog>("%DisableFrameworkConfirm");
        _detailsIcon = GetNode<TextureRect>("%DetailsIcon"); _detailsName = GetNode<Label>("%DetailsName"); _detailsText = GetNode<RichTextLabel>("%DetailsText");
        _versionSeparator = GetNode<Control>("%VersionSeparator"); _versionRow = GetNode<Control>("%VersionRow");
        _versionSelect = GetNode<OptionButton>("%VersionSelect"); _installVersion = GetNode<Button>("%InstallVersionButton");
        _versionHint = GetNode<Label>("%VersionHint"); _versionConfirm = GetNode<ConfirmationDialog>("%VersionConfirm");
        _retryVersions = GetNode<LinkButton>("%RetryVersionsButton");
        _errorBox = GetNode<Control>("%ErrorBox"); _errorText = GetNode<RichTextLabel>("%ErrorText"); _openLog = GetNode<Button>("%OpenLogButton");
        _installZip = GetNode<Button>("%InstallZipButton"); _zipDialog = GetNode<FileDialog>("%InstallZipDialog");
        _installConfirm = GetNode<ConfirmationDialog>("%InstallConfirm"); _installText = GetNode<RichTextLabel>("%InstallText");
        _settings = GetNode<SettingsDialog>("%SettingsDialog"); _settings.Initialize(global);
        _attention = GetNode<AttentionDialog>("%AttentionDialog"); _attention.Initialize(global.Host.PluginDirectory);
        _locationRow = GetNode<Control>("%LocationRow"); _locationLink = GetNode<LinkButton>("%LocationLink"); _openFolder = GetNode<Button>("%OpenFolderButton");
        RefreshTheme();
        _dialogLifetime.Signals.Connect(this, Control.SignalName.ThemeChanged, Callable.From(() => CallDeferred(MethodName.RefreshTheme)));

        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(EnabledColumn, "On"); _tree.SetColumnTitle(NameColumn, "Plugin"); _tree.SetColumnTitle(VersionColumn, "Version");
        _tree.SetColumnExpand(EnabledColumn, false); _tree.SetColumnExpand(NameColumn, false); _tree.SetColumnExpand(VersionColumn, true);
        _dialogLifetime.Prepare(this, MethodName.RefreshWindowIcons, _versionConfirm, _disableFrameworkConfirm, _zipDialog, _installConfirm);
        var scale = EditorWindows.Scale;
        _tree.SetColumnCustomMinimumWidth(EnabledColumn, (int)(40 * scale));
        _tree.SetColumnCustomMinimumWidth(VersionColumn, (int)(VersionMinimumWidth * scale));

        _dialogLifetime.Signals.Connect(_check, BaseButton.SignalName.Pressed, Callable.From(CheckNow));
        _dialogLifetime.Signals.Connect(_settingsButton, BaseButton.SignalName.Pressed, Callable.From(() => OpenSettings(null)));
        _dialogLifetime.Signals.Connect(_attentionButton, BaseButton.SignalName.Pressed, Callable.From(() => OpenAttention(null)));
        _dialogLifetime.Signals.Connect(_release, BaseButton.SignalName.Pressed, Callable.From(OpenRelease));
        _dialogLifetime.Signals.Connect(_locationLink, BaseButton.SignalName.Pressed, Callable.From(() => { if (_selectedSlug is not null) EditorWindows.SelectFile(PluginDirectory(_selectedSlug)); }));
        _dialogLifetime.Signals.Connect(_openFolder, BaseButton.SignalName.Pressed, Callable.From(() => { if (_selectedSlug is not null) EditorWindows.ShowInFileManager(PluginDirectory(_selectedSlug)); }));
        _dialogLifetime.Signals.Connect(_detailsText, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(DetailsLinkClicked));
        _dialogLifetime.Signals.Connect(_tree, Tree.SignalName.ItemEdited, Callable.From(ItemEdited));
        _dialogLifetime.Signals.Connect(_disableFrameworkConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(DisableFramework));
        _dialogLifetime.Signals.Connect(_versionSelect, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => VersionButtons()));
        _dialogLifetime.Signals.Connect(_installVersion, BaseButton.SignalName.Pressed, Callable.From(ConfirmVersion));
        _dialogLifetime.Signals.Connect(_retryVersions, BaseButton.SignalName.Pressed, Callable.From(RetryVersions));
        _dialogLifetime.Signals.Connect(GetNode<Button>("%CopyErrorButton"), BaseButton.SignalName.Pressed, Callable.From(() => DisplayServer.ClipboardSet(ErrorDetails())));
        _dialogLifetime.Signals.Connect(_openLog, BaseButton.SignalName.Pressed, Callable.From(OpenLog));
        _dialogLifetime.Signals.Connect(_versionConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(() =>
        {
            if (_pendingVersion is { } version) Install(new([version], true, _pendingDowngrade, _pendingReinstall));
        }));
        _dialogLifetime.Signals.Connect(_installZip, BaseButton.SignalName.Pressed, Callable.From(ChooseZip));
        _dialogLifetime.Signals.Connect(_zipDialog, FileDialog.SignalName.FileSelected, Callable.From<string>(ZipChosen));
        _dialogLifetime.Signals.Connect(_installConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(ConfirmAddon));
        _dialogLifetime.Signals.Connect(_installConfirm, AcceptDialog.SignalName.Canceled, Callable.From(CancelAddon));
        _dialogLifetime.Signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(() => ShowDetails(SlugOf(_tree.GetSelected()))));
        _dialogLifetime.Signals.Connect(_tree, Tree.SignalName.ItemActivated, Callable.From(OpenRelease));
        _dialogLifetime.Signals.Connect(_tree, Tree.SignalName.ButtonClicked, Callable.From<TreeItem, long, long, long>((item, _, _, _) => item.Select(NameColumn)));
        _dialogLifetime.Signals.Connect(_tree, Tree.SignalName.Resized, Callable.From(() => { if (_model is not null) FitColumns(); }));
        _dialogLifetime.Signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(Confirm));
        _dialogLifetime.Signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(Cancel));
        _dialogLifetime.Signals.Connect(this, Window.SignalName.CloseRequested, Callable.From(Cancel));
    }

    private void RefreshWindowIcons() => _dialogLifetime.RefreshIcons();

    private void RefreshTheme()
    {
        if (!GodotObject.IsInstanceValid(this) || _global is null) return;
        var editorTheme = EditorWindows.Theme;
        if (EditorIcons.Builtin("Folder") is { } folder) { _openFolder.Icon = folder; _openFolder.Text = ""; }
        // The editor's labels indent their text; the details text starts where theirs does.
        var inset = editorTheme is not null && editorTheme.HasStylebox("normal", "Label") ? editorTheme.GetStylebox("normal", "Label").GetMargin(Side.Left) : 0;
        _detailsText.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = inset });
        _ePluginIcon = EditorIcons.EPlugin; _logo = EditorIcons.Logo; _updateIcon = EditorIcons.UpdateIndicator;
        _check.Icon = EditorIcons.Rescan;
        _installZip.Icon = EditorIcons.Install;
        _attentionButton.Icon = EditorIcons.Retry;
        _settingsButton.Icon = EditorIcons.Settings;
        _settings.RefreshTheme();
        TextColors.Use(EditorWindows.Theme);
        // Render the existing model rather than refreshing it: a theme change must preserve a staged update.
        if (_model is not null) Render();
    }

    public void Open()
    {
        if (!_session.KeepNotice) ClearNotice();
        _session.KeepNotice = false;
        // Read on every open: the project may have been renamed in Project Settings since.
        Title = PluginManagerViewModel.WindowTitle(ProjectSettings.GetSetting("application/config/name").AsString(),
            ProjectSettings.GlobalizePath("res://"));
        // Reading the plugins while they change would record plain plugins in the middle of the operation, e.g. when the
        // manager was closed during it; the list shown last is read again once the operation is done. A manager opened
        // for the first time then (e.g. reopened right after an assembly reload) shows an empty list until then.
        if (_global.IsChangingPlugins || AssemblyReload.IsPending)
        {
            _model ??= new([], [], [], null, _ => []);
            _session.RefreshWhenIdle = true;
            Render();
        }
        else Refresh();
        EditorWindows.PopupChild(this);
        _tree.GrabFocus();
        // Problems shown already in this editor session wait for the button; new ones open the list on top.
        if (_attentionItems.Any(i => !_global.ShownAttention.Contains(i.Key))) CallDeferred(MethodName.OpenAttention, "");
    }

    /// <summary>
    /// Opens the settings; with <paramref name="cleanupDirectory"/>, on the Tools tab cleaning up that local directory.
    /// Nothing opens while the manager is busy, as its Settings button is disabled then.
    /// </summary>
    public void OpenSettings(string? cleanupDirectory)
    {
        if (Busy) return;
        _settings.Open(_model.Plugins.FirstOrDefault(r => r.Plugin.Kind == PluginKind.Framework)?.Plugin, cleanupDirectory);
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
        if (_session.Working) return;
        _session.RefreshWhenIdle = false;
        // A new add-on staged by Install from ZIP waits for its confirmation, which clears it; e.g. the local index
        // finishing in the meantime must not drop it. Installing checks its folder again.
        if (_updates.StagedAddon is null) _updates.Clear();
        _licenses.Clear(); _welcomes.Clear(); _recipes.Clear();
        var plugins = _global.CollectPlugins();
        _targets = _global.CollectUpdateTargets();
        _model = new(plugins, _global.PendingUpdates, _targets, _global.UpdateCache, candidate => _global.UpdatePreflightFindings([candidate]));
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
        var editable = !PollBusy();
        foreach (var row in _model.Plugins)
        {
            var plugin = row.Plugin;
            var item = _tree.CreateItem(root);
            item.SetMetadata(NameColumn, plugin.Slug);
            item.SetCellMode(EnabledColumn, TreeItem.TreeCellMode.Check);
            item.SetChecked(EnabledColumn, plugin.Enabled);
            item.SetEditable(EnabledColumn, row.CanToggle && editable);
            item.SetTooltipText(EnabledColumn, PluginManagerViewModel.EnabledTooltip(row));
            item.SetText(NameColumn, PluginManagerViewModel.ListName(plugin));
            var updateState = PluginManagerViewModel.UpdateState(row);
            item.SetTooltipText(NameColumn, PluginManagerViewModel.NameTooltip(row, PluginDirectory(plugin.Slug)));
            if (row.IsEPlugin && _ePluginIcon is not null) item.SetIcon(NameColumn, _ePluginIcon);
            if (updateState is not null && _updateIcon is not null)
            {
                item.AddButton(NameColumn, _updateIcon, 0, false, updateState);
                item.SetButtonColor(NameColumn, 0, row.Update is { } state ? (state.HasError ? EditorIcons.ErrorColor : EditorIcons.SuccessColor) : EditorIcons.DisabledColor);
            }
            if (!plugin.Enabled) item.SetCustomColor(NameColumn, EditorIcons.DisabledColor);
            if (row.NeedsAttention) item.SetCustomColor(NameColumn, EditorIcons.ErrorColor);
            item.SetText(VersionColumn, PluginManagerViewModel.VersionColumnText(row));
            item.SetTooltipText(VersionColumn, PluginManagerViewModel.VersionColumnTooltip(row));
            if (row.Update is { HasError: true }) item.SetCustomColor(VersionColumn, EditorIcons.ErrorColor);
            if (plugin.Slug == _selectedSlug) selected = item;
        }
        FitColumns();
        if (selected is not null) { selected.Select(NameColumn); _tree.ScrollToItem(selected); }
        else ShowDetails(null);
        var next = _global.NextUpdateCheck;
        // An error is shown in full in the error area instead.
        _status.Text = PluginManagerViewModel.StatusLine(_noticeIsError ? null : _operationNotice, _model.Updates.Count,
            PluginManagerViewModel.CheckSchedule(_global.LastUpdateCheck, next, DateTimeOffset.UtcNow, CultureInfo.CurrentCulture),
            _global.IsIndexingLocalSources, _session.ReloadPending, _session.Operation.IsAwaitingReload);
        _status.TooltipText = _status.Text + "\n" + PluginManagerViewModel.CheckScheduleHint(next);
        RenderError();
        Buttons();
    }

    /// <summary>Shows an error notice in the error area, in the error color, where it wraps and can be selected.</summary>
    private void RenderError()
    {
        var error = _noticeIsError ? _operationNotice : null;
        _errorBox.Visible = error is not null;
        _errorText.Text = error is null ? "" : $"[color={TextColors.Error}]{PluginManagerViewModel.Escape(error)}[/color]";
        _openLog.Visible = _noticeLog is not null && (System.IO.Directory.Exists(_noticeLog) || System.IO.File.Exists(_noticeLog));
    }

    /// <summary>
    /// Sets the outcome of an operation, shown until the next one; an error is logged too. The notice of work that ended
    /// while the manager was closed is kept for the next <see cref="Open"/>.
    /// </summary>
    /// <param name="log">The folder or file with the failure's logs, which the error area offers to open.</param>
    private void SetNotice(string? notice, bool isError, string? log = null)
    {
        _operationNotice = notice;
        _noticeIsError = isError && notice is not null;
        _noticeLog = _noticeIsError ? log : null;
        _session.KeepNotice = !Visible;
        if (_noticeIsError) GD.PushError(notice);
    }

    private void ClearNotice()
    {
        _operationNotice = null;
        _noticeIsError = false;
        _noticeLog = null;
    }

    /// <summary>The error notice and where its logs are, for the clipboard.</summary>
    private string ErrorDetails()
    {
        var error = _noticeIsError ? _operationNotice ?? "" : "";
        return _noticeLog is not null && !error.Contains(_noticeLog, StringComparison.Ordinal) ? error + "\nLogs: " + _noticeLog : error;
    }

    // Its button is Open log folder: it shows where the failure's logs are, while the Open log buttons of the Needs
    // attention and update failure dialogs open the build log itself (docs/eplugin-manager.md).
    private void OpenLog()
    {
        if (_noticeLog is not null) EditorWindows.ShowInFileManager(_noticeLog);
    }

    private string PluginDirectory(string slug) => _global.Host.PluginDirectory(slug);

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
        // Choosing another plugin and back loads the versions again that could not be loaded, e.g. while offline.
        if (slug is not null && slug != _selectedSlug) _updates.ForgetVersionError(slug);
        _selectedSlug = slug;
        var row = slug is null ? null : _model.Find(slug);
        _detailsIcon.Texture = row?.IsEPlugin == true ? _logo : null;
        // Without an icon its separation would indent the name further than the lines below.
        _detailsIcon.Visible = _detailsIcon.Texture is not null;
        _detailsName.Text = row is null ? "No plugin selected" : PluginManagerViewModel.DetailsTitle(row.Plugin);
        _keptUpdate = row is null ? null : _global.KeptUpdate(row.Plugin.Slug);
        _detailsText.Text = row is null ? "Select a plugin to see its details." : PluginManagerViewModel.Describe(row, LicenseOf(row), WelcomeOf(row), _keptUpdate, RecipeOf(row));
        // the same links the details text offers
        _release.Visible = PluginManagerViewModel.IsWebUrl(row?.Update?.Candidate.ReleaseUrl);
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

    private EEditorPluginRecipe? RecipeOf(PluginRow row)
    {
        if (row.Plugin.Missing || row.Plugin.Kind != PluginKind.EPlugin) return null;
        if (_recipes.TryGetValue(row.Plugin.Slug, out var recipe)) return recipe;
        try { recipe = _global.DescribeRecipe(row.Plugin.Slug); }
        catch (Exception ex) { GD.PushError($"Cannot read the recipe of {row.Plugin.Slug}: {ex.Message}"); recipe = null; }
        return _recipes[row.Plugin.Slug] = recipe;
    }

    private void DetailsLinkClicked(Variant meta)
    {
        var link = meta.AsString();
        if (PluginManagerViewModel.IsWebUrl(link)) EditorWindows.OpenWebLink(link);
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

    /// <summary>
    /// Starts asynchronous work from a signal; an unexpected failure is shown as an error instead of crashing the editor.
    /// </summary>
    /// <param name="ownsWork">
    /// The work marks the manager as working; its failure ends that, so the manager is not left locked with its progress
    /// text. Work that runs beside an operation, such as loading versions, only reports its failure.
    /// </param>
    private void Run(Task task, string what, bool ownsWork = true) => ObservedTask.Run(task, ex => ReportFailure(what, ex, ownsWork));

    /// <summary>Hands an unexpected failure to the editor thread, on whichever thread the failed work ended.</summary>
    private void ReportFailure(string what, Exception ex, bool ownsWork)
    {
        try
        {
            // A method call rather than a delegate, which Godot resolves on the instance an assembly reload left.
            if (GodotObject.IsInstanceValid(this))
            {
                CallDeferred(MethodName.WorkFailed, what, ex.Message, ownsWork);
                return;
            }
        }
        catch (ObjectDisposedException) { }
        GD.PushError($"{what} failed: {ex.Message}");
    }

    internal void WorkFailed(string what, string message, bool ownsWork)
    {
        // An assembly reload dropped this dialog's state since, so the work went with the old one; or work beside an
        // operation failed, whose status must not be replaced. Either is only logged.
        if (!IsInitialized || (!ownsWork && _session.Working))
        {
            GD.PushError($"{what} failed: {message}");
            return;
        }
        // A plugin operation the work began is completed by _Process once nothing works any more.
        if (ownsWork) _session.EndWork();
        SetNotice($"{what} failed unexpectedly: {message}", true);
        if (_model is not null) Render();
    }

    private void Buttons()
    {
        var busy = PollBusy();
        // Set native input state before queuing the operation. It remains locked even while C# state is reloaded.
        GuiDisableInput = _session.LocksInput(_global.IsChangingPlugins, _session.ReloadPending);
        var editable = !busy;
        for (var item = _tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
            item.SetEditable(EnabledColumn, editable && row is { CanToggle: true });
        }
        _settingsButton.Disabled = busy;
        _versionSelect.Disabled = busy || !PluginManagerViewModel.CanChooseVersion(_versionOptions);
        GetOkButton().Text = _model.OkText;
        GetOkButton().Icon = EditorIcons.Upgrade;
        GetOkButton().Disabled = busy || !_model.CanApply;
        GetOkButton().Visible = _model.Updates.Count > 0;
        GetCancelButton().Disabled = !_session.CanClose(_global.IsChangingPlugins, _session.ReloadPending);
        GetCancelButton().Visible = _session.ShowsCancel;
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
        EditorWindows.OpenWebLink(_selectedSlug is null ? null : _model.Find(_selectedSlug)?.Update?.Candidate.ReleaseUrl);
    }

    /// <summary>A new local index can add or remove local versions and updates, so the lists are read again.</summary>
    private void LocalIndexChanged()
    {
        if (!IsShown || !Visible) return;
        // Never interrupt work or read the plugins while they change (reading them records plain plugins in the shared
        // state file); the list is read again once that ended.
        if (Busy) _session.RefreshWhenIdle = true;
        else if (_global.IsIndexingLocalSources) Render();
        else Refresh();
    }

    /// <summary>
    /// A plugin's update site or a setting changed: versions loaded from a source no longer used are dropped, and the
    /// list is read again so updates found there are no longer offered and the status line shows the next scheduled check.
    /// While the settings are open, that waits until they close: every click on a number's arrows changes a setting.
    /// </summary>
    private void SettingsChanged(bool sourcesChanged)
    {
        if (sourcesChanged) _updates.ClearVersions();
        if (!IsShown) return;
        if (Busy || _settings.Visible) _session.RefreshWhenIdle = true;
        else Refresh();
    }

    private static Texture2D? VersionIcon(bool downgrade, bool reinstall) =>
        reinstall ? EditorIcons.Reinstall : downgrade ? EditorIcons.Downgrade : EditorIcons.Upgrade;

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (!IsInitialized || !Visible || (_reloadPoll += delta) < ReloadPollSeconds) return;
        _reloadPoll = 0;
        if (_session.CanCompleteOperation(_global.IsChangingPlugins)) CompletePluginOperation(_session.RefreshOnCompletion);
        var pending = AssemblyReload.IsPending;
        var locked = _session.LocksInput(_global.IsChangingPlugins, pending);
        // not while the settings are open, which change what the list shows (see SettingsChanged)
        if (_session.ShouldRefresh(locked) && !_settings.Visible)
        {
            _session.RefreshWhenIdle = false;
            _session.ReloadPending = pending;
            Refresh();
            return;
        }
        if (pending == _session.ReloadPending && locked == GuiDisableInput) return;
        _session.ReloadPending = pending;
        // Not while staging or installing: those render their own progress and refresh when done.
        if (_session.Working) Buttons();
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
        _dialogLifetime.ReleaseCallbacks();
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
        if (!_session.Swapping) _updates?.Clear();
        base._ExitTree();
    }
}
#endif
