#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// ePlugin's settings in tabs: the project settings, the update sources (update sites and local directories), Tools
/// (cleaning up a local directory) and About.
/// The layout lives in SettingsDialog.tscn; each tab has its own script, this one owns the window and the windows the
/// tabs open.
/// </summary>
[Tool]
internal sealed partial class SettingsDialog : AcceptDialog, ISerializationListener
{
    private readonly DialogLifetime _lifetime = new();
    private TabContainer _tabs = null!;
    private GeneralSettingsTab _general = null!;
    private UpdateSourcesTab _updateSources = null!;
    private ToolsTab _tools = null!;
    private AboutTab _about = null!;
    private PluginSnapshot _plugins = null!;

    /// <summary>
    /// Raised after a setting or an update source changed, so the plugin list can be read again: both decide where
    /// updates come from and when they are checked. True when where updates come from changed.
    /// </summary>
    public event Action<bool>? Changed;

    public void Initialize(EGlobal global)
    {
        _tabs = GetNode<TabContainer>("%SettingsTabs");
        _plugins = new PluginSnapshot(global.CollectPlugins);
        // The tab titles are the names of these nodes: TabContainer reads no other title from a scene in Godot 4.4.
        _general = GetNode<GeneralSettingsTab>("%SettingsTabs/General"); _general.Initialize();
        _updateSources = GetNode<UpdateSourcesTab>("%SettingsTabs/Update sources"); _updateSources.Initialize(global, _plugins);
        _tools = GetNode<ToolsTab>("%SettingsTabs/Tools"); _tools.Initialize(global, _plugins);
        _about = GetNode<AboutTab>("%SettingsTabs/About"); _about.Initialize();
        _lifetime.Prepare(this, MethodName.RefreshWindowIcons, GetNode<Window>("%AutoAcceptConfirm"), GetNode<Window>("%CleanupConfirm"),
            GetNode<Window>("%SiteEditor"), GetNode<Window>("%LocalSourceFolderDialog"));
    }

    /// <summary>
    /// Opens on the General tab with every tab read again; <paramref name="framework"/> is the manager's entry for the
    /// framework, which About shows. With <paramref name="cleanupDirectory"/>, opens on Tools with that local directory.
    /// </summary>
    public void Open(PluginInfo? framework, string? cleanupDirectory = null)
    {
        // Update sources reads the local directories again, which Tools then shows; both read the plugins once.
        _plugins.Invalidate();
        _general.Open(); _updateSources.Open(); _tools.Open(cleanupDirectory); _about.Open(framework);
        _tabs.CurrentTab = cleanupDirectory is null ? 0 : _tools.GetIndex();
        EditorWindows.PopupChild(this);
    }

    /// <summary>Renders the icons for the current editor theme; the manager calls it when its theme changes.</summary>
    public void RefreshTheme() => _updateSources.RefreshTheme();

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_general is null) return;
        _general.Changed += GeneralChanged;
        _updateSources.Changed += SourcesChanged;
    }

    private void GeneralChanged(string key)
    {
        // The Update sources tab shows whether the built-in list is turned off.
        if (GeneralSettingsTab.ChangesUpdateSources(key)) _updateSources.Render();
        Changed?.Invoke(GeneralSettingsTab.ChangesUpdateSources(key));
    }

    private void SourcesChanged() => Changed?.Invoke(true);

    private void RefreshWindowIcons() => _lifetime.RefreshIcons();

    private void ReleaseCallbacks()
    {
        _lifetime.ReleaseCallbacks();
        if (_general is not null) _general.Changed -= GeneralChanged;
        if (_updateSources is not null) _updateSources.Changed -= SourcesChanged;
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

/// <summary>
/// The project's plugins as the settings' tabs show them: read once when the settings open and shared by the tabs until
/// one of them asks for them again, e.g. when the local index changed. Reading them is slow and records plain plugins in
/// the shared state file.
/// </summary>
internal sealed class PluginSnapshot(Func<IReadOnlyList<PluginInfo>> collect)
{
    private IReadOnlyList<PluginInfo>? _plugins;

    public IReadOnlyList<PluginInfo> Plugins => _plugins ??= collect();

    /// <summary>Reads the plugins again when they are asked for next.</summary>
    public void Invalidate() => _plugins = null;
}
#endif
