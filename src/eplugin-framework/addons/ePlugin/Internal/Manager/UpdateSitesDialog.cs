#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Adds, edits and removes the update sites the project sets for its plugins. The layout lives in
/// UpdateSitesDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class UpdateSitesDialog : AcceptDialog, ISerializationListener
{
    private const int PluginColumn = 0, SiteColumn = 1, ConfigColumn = 2;
    private const string DefaultHint = "GitHub or GitLab releases, or a Git repository (.git, optionally with ?path=addons/<slug> and #branch).";
    private readonly EditorSignalConnections _signals = new();
    private EGlobal _global = null!;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _add = null!;
    private Button _edit = null!;
    private Button _remove = null!;
    private ConfirmationDialog _editor = null!;
    private OptionButton _slug = null!;
    private LineEdit _url = null!;
    private Label _hint = null!;
    private string? _error;
    // plugin.cfg update_url per slug, read when the editor opens rather than on every keystroke
    private Dictionary<string, string?> _configuredUrls = [];

    /// <summary>Raised after a site was added, changed or removed, so the plugin list can be read again.</summary>
    public event Action? Changed;

    public void Initialize(EGlobal global)
    {
        _global = global;
        _tree = GetNode<Tree>("%SiteTree"); _status = GetNode<Label>("%SiteStatus");
        _add = GetNode<Button>("%AddSiteButton"); _edit = GetNode<Button>("%EditSiteButton"); _remove = GetNode<Button>("%RemoveSiteButton");
        _editor = GetNode<ConfirmationDialog>("%SiteEditor"); _slug = GetNode<OptionButton>("%SiteSlug");
        _url = GetNode<LineEdit>("%SiteUrl"); _hint = GetNode<Label>("%SiteHint");
        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(PluginColumn, "Plugin"); _tree.SetColumnTitle(SiteColumn, "Update site"); _tree.SetColumnTitle(ConfigColumn, "plugin.cfg update_url");
        _tree.SetColumnExpand(PluginColumn, false); _tree.SetColumnCustomMinimumWidth(PluginColumn, (int)(160 * EditorInterface.Singleton.GetEditorScale()));
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcons));
        EditorWindows.Prepare(_editor, _signals, this, nameof(QueueWindowIcons));
        _signals.Connect(_add, BaseButton.SignalName.Pressed, Callable.From(() => OpenEditor(null)));
        _signals.Connect(_edit, BaseButton.SignalName.Pressed, Callable.From(() => OpenEditor(SelectedSlug())));
        _signals.Connect(_remove, BaseButton.SignalName.Pressed, Callable.From(RemoveSelected));
        _signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(Buttons));
        _signals.Connect(_tree, Tree.SignalName.ItemActivated, Callable.From(() => { if (SelectedSlug() is { } slug) OpenEditor(slug); }));
        _signals.Connect(_editor, AcceptDialog.SignalName.Confirmed, Callable.From(Save));
        _signals.Connect(_url, LineEdit.SignalName.TextChanged, Callable.From<string>(_ => ShowHint(null)));
        _signals.Connect(_url, LineEdit.SignalName.TextSubmitted, Callable.From<string>(_ => Save()));
        _signals.Connect(_slug, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => ShowHint(null)));
    }

    private void QueueWindowIcons() => CallDeferred(nameof(RefreshWindowIcons));
    private void RefreshWindowIcons()
    {
        EditorWindows.ApplyIcon(this);
        EditorWindows.ApplyIcon(_editor);
    }

    public void Open()
    {
        _error = null;
        // A pull may have changed the file since it was read.
        _global.ReloadUpdateSites();
        Render();
        PopupCentered();
    }

    private void Render()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        var selected = SelectedSlug();
        var plugins = _global.CollectPlugins().ToDictionary(p => p.Slug, StringComparer.Ordinal);
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var site in _global.UpdateSites)
        {
            var item = _tree.CreateItem(root);
            item.SetMetadata(PluginColumn, site.Slug);
            var plugin = plugins.GetValueOrDefault(site.Slug);
            item.SetText(PluginColumn, plugin?.Name ?? site.Slug);
            item.SetTooltipText(PluginColumn, plugin is null ? $"{site.Slug} is not in res://addons." : $"{plugin.Name} (res://addons/{site.Slug})");
            if (plugin is null) item.SetCustomColor(PluginColumn, DisabledColor());
            item.SetText(SiteColumn, site.Url);
            var supported = UpdateSourceFactory.IsSupported(site.Url);
            item.SetTooltipText(SiteColumn, supported ? site.Url : $"{site.Url}\nThis is not a supported update site, so the plugin.cfg update_url is used.");
            if (!supported) item.SetCustomColor(SiteColumn, ErrorColor());
            item.SetText(ConfigColumn, plugin?.UpdateUrl ?? "(none)");
            item.SetTooltipText(ConfigColumn, plugin?.UpdateUrl is { } url ? $"Used when the update site does not work: {url}" : "plugin.cfg sets no update_url.");
            if (plugin?.UpdateUrl is null) item.SetCustomColor(ConfigColumn, DisabledColor());
            if (site.Slug == selected) item.Select(PluginColumn);
        }
        var problem = _global.UpdateSitesProblem;
        _status.TooltipText = problem ?? "";
        _status.Text = _error ?? problem ?? (_global.UpdateSites.Count == 0
            ? "No update sites set. Plugins use their plugin.cfg update_url."
            : $"{_global.UpdateSites.Count} update site{(_global.UpdateSites.Count == 1 ? "" : "s")} set for this project.");
        Buttons();
    }

    private void Buttons()
    {
        var problem = _global.UpdateSitesProblem is not null;
        _add.Disabled = problem;
        _edit.Disabled = _remove.Disabled = problem || SelectedSlug() is null;
    }

    private string? SelectedSlug() =>
        _tree.GetSelected() is { } item && item.GetMetadata(PluginColumn).VariantType == Variant.Type.String
            ? item.GetMetadata(PluginColumn).AsString() : null;

    /// <summary>Opens the editor for a new site, or for the site of <paramref name="slug"/>, whose plugin cannot change.</summary>
    private void OpenEditor(string? slug)
    {
        if (_global.UpdateSitesProblem is not null) return;
        _slug.Clear();
        var taken = _global.UpdateSites.Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);
        var all = _global.CollectPlugins();
        _configuredUrls = all.ToDictionary(p => p.Slug, p => p.UpdateUrl, StringComparer.Ordinal);
        var plugins = all.Where(p => !p.Missing)
            .Where(p => slug is null ? !taken.Contains(p.Slug) : p.Slug == slug)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // A site of a plugin that was removed from res://addons can still be edited.
        if (slug is not null && plugins.Count == 0) _slug.AddItem(slug);
        foreach (var plugin in plugins) _slug.AddItem(plugin.Name == plugin.Slug ? plugin.Slug : $"{plugin.Name} ({plugin.Slug})");
        for (var i = 0; i < _slug.ItemCount; i++) _slug.SetItemMetadata(i, slug ?? plugins[i].Slug);
        if (_slug.ItemCount == 0)
        {
            _error = "Every plugin in res://addons has an update site already. Select one to edit it.";
            Render();
            return;
        }
        _slug.Select(0);
        _slug.Disabled = slug is not null;
        _url.Text = slug is null ? "" : _global.UpdateSiteOf(slug) ?? "";
        _editor.Title = slug is null ? "Add update site" : "Edit update site";
        ShowHint(null);
        _editor.PopupCentered();
        _url.GrabFocus();
        _url.CaretColumn = _url.Text.Length;
    }

    private void Save()
    {
        if (_slug.Selected < 0) return;
        var slug = _slug.GetItemMetadata(_slug.Selected).AsString();
        try
        {
            _global.SetUpdateSite(slug, _url.Text);
            _editor.Hide();
            _error = null;
            Changed?.Invoke();
            Render();
        }
        catch (ArgumentException ex) { ShowHint(ex.Message.Split(" (Parameter")[0]); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowHint("Cannot save the update site: " + ex.Message);
        }
    }

    /// <summary>Shows an error under the URL, or the plugin's plugin.cfg update_url that remains the fallback.</summary>
    private void ShowHint(string? error)
    {
        if (error is not null)
        {
            _hint.Text = error;
            _hint.AddThemeColorOverride("font_color", ErrorColor());
            return;
        }
        _hint.RemoveThemeColorOverride("font_color");
        var slug = _slug.Selected >= 0 ? _slug.GetItemMetadata(_slug.Selected).AsString() : null;
        var configured = slug is null ? null : _configuredUrls.GetValueOrDefault(slug);
        _hint.Text = DefaultHint + (configured is null
            ? "\nThe plugin's plugin.cfg sets no update_url."
            : $"\nWhen this site does not work, plugin.cfg's update_url is used: {configured}");
    }

    private void RemoveSelected()
    {
        if (SelectedSlug() is not { } slug) return;
        _error = null;
        try { if (_global.RemoveUpdateSite(slug)) Changed?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot remove the update site: " + ex.Message; }
        Render();
    }

    private static Color ThemeColor(string name, string type, Color fallback)
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        return theme is not null && theme.HasColor(name, type) ? theme.GetColor(name, type) : fallback;
    }
    private static Color ErrorColor() => ThemeColor("error_color", "Editor", new Color(1f, 0.47f, 0.42f));
    private static Color DisabledColor() => ThemeColor("font_disabled_color", "Button", new Color(0.5f, 0.5f, 0.5f));

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
