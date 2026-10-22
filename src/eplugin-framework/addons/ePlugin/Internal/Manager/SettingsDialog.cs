#if TOOLS
using System;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// ePlugin's settings in tabs: the project settings, the update sources (update sites and local directories) and About.
/// The layout lives in SettingsDialog.tscn; each tab has its own script, this one owns the window and the windows the
/// tabs open.
/// </summary>
[Tool]
internal sealed partial class SettingsDialog : AcceptDialog, ISerializationListener
{
    private readonly EditorSignalConnections _signals = new();
    private TabContainer _tabs = null!;
    private GeneralSettingsTab _general = null!;
    private UpdateSourcesTab _updateSources = null!;
    private AboutTab _about = null!;
    private Window[] _windows = [];

    /// <summary>
    /// Raised after a setting or an update site changed, so the plugin list can be read again: both decide where updates
    /// come from and when they are checked.
    /// </summary>
    public event Action? Changed;

    public void Initialize(EGlobal global)
    {
        _tabs = GetNode<TabContainer>("%SettingsTabs");
        // The tab titles are the names of these nodes: TabContainer reads no other title from a scene in Godot 4.4.
        _general = GetNode<GeneralSettingsTab>("%SettingsTabs/General"); _general.Initialize();
        _updateSources = GetNode<UpdateSourcesTab>("%SettingsTabs/Update sources"); _updateSources.Initialize(global);
        _about = GetNode<AboutTab>("%SettingsTabs/About"); _about.Initialize();
        _windows = [this, GetNode<Window>("%AutoAcceptConfirm"), GetNode<Window>("%SiteEditor"), GetNode<Window>("%LocalSourceFolderDialog")];
        foreach (var window in _windows) EditorWindows.Prepare(window, _signals, this, nameof(QueueWindowIcons));
    }

    /// <summary>
    /// Opens on the General tab with every tab read again; <paramref name="framework"/> is the manager's entry for the
    /// framework, which About shows.
    /// </summary>
    public void Open(PluginInfo? framework)
    {
        _general.Open(); _updateSources.Open(); _about.Open(framework);
        _tabs.CurrentTab = 0;
        PopupCentered();
    }

    /// <summary>Renders the icons for the current editor theme; the manager calls it when its theme changes.</summary>
    public void RefreshTheme() => _updateSources.RefreshTheme();

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_general is null) return;
        _general.Changed += RaiseChanged;
        _updateSources.Changed += RaiseChanged;
    }

    private void RaiseChanged()
    {
        // The Update sources tab shows whether the built-in list is turned off.
        _updateSources.Render();
        Changed?.Invoke();
    }

    private void QueueWindowIcons() => CallDeferred(nameof(RefreshWindowIcons));
    private void RefreshWindowIcons()
    {
        foreach (var window in _windows) EditorWindows.ApplyIcon(window);
    }

    private void ReleaseCallbacks()
    {
        _signals.Dispose();
        if (_general is not null) _general.Changed -= RaiseChanged;
        if (_updateSources is not null) _updateSources.Changed -= RaiseChanged;
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize() => ReleaseCallbacks();
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseCallbacks();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        ReleaseCallbacks();
        base._ExitTree();
    }
}
#endif
