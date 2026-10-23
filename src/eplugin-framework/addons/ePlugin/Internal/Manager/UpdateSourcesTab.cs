#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The Update sources tab of the settings: the update sites the project sets for its plugins, the user's local
/// directories, whose ZIP files can update any plugin, and ePlugin's built-in list. The layout lives in
/// SettingsDialog.tscn, which also holds the site editor and the folder dialog.
/// </summary>
[Tool]
internal sealed partial class UpdateSourcesTab : VBoxContainer, ISerializationListener
{
    // A site row keeps its plugin's slug in the plugin column, a local directory row its path in the source column.
    private const int PluginColumn = 0, SourceColumn = 1;
    private const string DefaultHint = "GitHub, GitLab, Forgejo or Gitea (Codeberg) releases, a repository's tag page (.../tags), or a Git repository (.git, optionally with ?path=addons/<slug> and #branch).";
    private readonly EditorSignalConnections _signals = new();
    private EGlobal _global = null!;
    private Tree _tree = null!;
    private Label _status = null!;
    private Button _rescan = null!;
    private Button _addSite = null!;
    private Button _addDirectory = null!;
    private Button _edit = null!;
    private Button _remove = null!;
    private ConfirmationDialog _editor = null!;
    private OptionButton _slug = null!;
    private LineEdit _url = null!;
    private Label _hint = null!;
    private FileDialog _folderDialog = null!;
    private string? _error;
    // plugin.cfg update_url per slug, read when the editor opens rather than on every keystroke
    private Dictionary<string, string?> _configuredUrls = [];
    // Local directories whose packages the user folded away; rendering keeps them that way.
    private readonly HashSet<string> _collapsedDirectories = new(PackageFiles.PathComparer);

    /// <summary>Raised after a site was added, changed or removed, so the plugin list can be read again.</summary>
    public event Action? Changed;

    public void Initialize(EGlobal global)
    {
        _global = global;
        _tree = GetNode<Tree>("%SourceTree"); _status = GetNode<Label>("%SourceStatus");
        _rescan = GetNode<Button>("%RescanLocalSourcesButton"); _addSite = GetNode<Button>("%AddSiteButton");
        _addDirectory = GetNode<Button>("%AddLocalSourceButton"); _edit = GetNode<Button>("%EditSiteButton"); _remove = GetNode<Button>("%RemoveSourceButton");
        _editor = GetNode<ConfirmationDialog>("%SiteEditor"); _slug = GetNode<OptionButton>("%SiteSlug");
        _url = GetNode<LineEdit>("%SiteUrl"); _hint = GetNode<Label>("%SiteHint");
        _folderDialog = GetNode<FileDialog>("%LocalSourceFolderDialog");
        // Column titles and sizing are not scene properties of Tree.
        _tree.SetColumnTitle(PluginColumn, "Plugin"); _tree.SetColumnTitle(SourceColumn, "Update source");
        _tree.SetColumnExpand(PluginColumn, false); _tree.SetColumnCustomMinimumWidth(PluginColumn, (int)(220 * EditorWindows.Scale));
        _signals.Connect(_rescan, BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            _error = null;
            ObservedTask.Run(_global.RebuildLocalIndexAsync(), ex => GD.PushError($"Rescanning the local directories failed: {ex.Message}"));
        }));
        _signals.Connect(_addSite, BaseButton.SignalName.Pressed, Callable.From(() => OpenEditor(null)));
        _signals.Connect(_addDirectory, BaseButton.SignalName.Pressed, Callable.From(() => _folderDialog.PopupCentered()));
        _signals.Connect(_edit, BaseButton.SignalName.Pressed, Callable.From(() => OpenEditor(SelectedSlug())));
        _signals.Connect(_remove, BaseButton.SignalName.Pressed, Callable.From(RemoveSelected));
        _signals.Connect(_tree, Tree.SignalName.ItemSelected, Callable.From(Buttons));
        _signals.Connect(_tree, Tree.SignalName.ItemActivated, Callable.From(() => { if (SelectedSlug() is { } slug) OpenEditor(slug); }));
        _signals.Connect(_tree, Tree.SignalName.ItemCollapsed, Callable.From<TreeItem>(DirectoryCollapsed));
        _signals.Connect(_editor, AcceptDialog.SignalName.Confirmed, Callable.From(Save));
        _signals.Connect(_url, LineEdit.SignalName.TextChanged, Callable.From<string>(_ => ShowHint(null)));
        _signals.Connect(_url, LineEdit.SignalName.TextSubmitted, Callable.From<string>(_ => Save()));
        _signals.Connect(_slug, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => ShowHint(null)));
        _signals.Connect(_folderDialog, FileDialog.SignalName.DirSelected, Callable.From<string>(AddDirectory));
    }

    /// <summary>Renders the button icons for the current editor theme; the dialog calls it when its theme changes.</summary>
    public void RefreshTheme() => _rescan.Icon = EditorIcons.Rescan;

    public void Open()
    {
        _error = null;
        // A pull may have changed the sites, and another open editor the local directories, which all projects share.
        _global.ReloadUpdateSites();
        _global.ReloadLocalDirectories();
        Render();
    }

    public void Render()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        var selectedSlug = SelectedSlug();
        var selectedDirectory = SelectedDirectory();
        var plugins = _global.CollectPlugins().ToDictionary(p => p.Slug, StringComparer.Ordinal);
        _tree.Clear();
        var root = _tree.CreateItem();
        var project = Group(root, "Set by this project", _global.UpdateSites.Count == 0 ? "No update sites set yet. Use Add site... to set one." : null);
        foreach (var site in _global.UpdateSites)
        {
            var item = _tree.CreateItem(project);
            item.SetMetadata(PluginColumn, site.Slug);
            var plugin = plugins.GetValueOrDefault(site.Slug);
            item.SetText(PluginColumn, plugin?.Name ?? site.Slug);
            item.SetTooltipText(PluginColumn, plugin is null ? $"{site.Slug} is not in res://addons." : $"{plugin.Name} (res://addons/{site.Slug})");
            if (plugin is null) item.SetCustomColor(PluginColumn, EditorIcons.DisabledColor);
            item.SetText(SourceColumn, site.Url);
            var fallback = plugin?.UpdateUrl is { } url ? $"When it does not work, plugin.cfg's update_url is used: {url}" : "plugin.cfg sets no update_url.";
            var supported = UpdateSourceFactory.IsSupported(site.Url);
            item.SetTooltipText(SourceColumn, supported ? $"{site.Url}\n{fallback}" : $"{site.Url}\nThis is not a supported update site, so the plugin.cfg update_url is used.");
            if (!supported) item.SetCustomColor(SourceColumn, EditorIcons.ErrorColor);
            if (site.Slug == selectedSlug) item.Select(PluginColumn);
        }
        RenderLocalDirectories(root, plugins, selectedDirectory);
        RenderKnownPlugins(root, plugins);
        RenderStatus();
        Buttons();
    }

    /// <summary>
    /// The user's local directories, each with the plugin packages the index found in it. Only the index looks at the
    /// disk: an unreachable network share can block for a long time.
    /// </summary>
    private void RenderLocalDirectories(TreeItem root, Dictionary<string, PluginInfo> plugins, string? selected)
    {
        var group = Group(root, "Local directories", _global.LocalDirectories.Count == 0 ? "No local directory yet. Use Add directory... to add one." : null);
        group.SetTooltipText(PluginColumn, "ZIP files in these directories and their subdirectories are offered as updates and versions of any plugin. " +
                                           "The list belongs to your user account and is shared by all projects; it is not stored in the project.");
        var index = _global.LocalIndex;
        foreach (var directory in _global.LocalDirectories)
        {
            var item = _tree.CreateItem(group);
            item.SetMetadata(SourceColumn, directory);
            item.SetText(SourceColumn, directory);
            item.SetTooltipText(SourceColumn, directory);
            var state = index.StateOf(directory);
            item.SetText(PluginColumn, _global.IsIndexingLocalSources && state is null ? "Indexing..."
                : state is null ? "Not indexed yet"
                : !state.Exists ? "Not found"
                : $"{state.Packages} plugin package{(state.Packages == 1 ? "" : "s")}");
            item.SetTooltipText(PluginColumn, state is { Exists: false }
                ? "This directory was not found when it was indexed. It stays in the list and is indexed again when it reappears."
                : "Offers its packages as updates and versions of any plugin.");
            if (state is { Exists: false }) item.SetCustomColor(PluginColumn, EditorIcons.DisabledColor);
            foreach (var package in index.Packages.Where(p => IsInside(directory, p.ZipPath)).OrderBy(p => p.Slug, StringComparer.OrdinalIgnoreCase))
            {
                var child = _tree.CreateItem(item);
                var plugin = plugins.GetValueOrDefault(package.Slug);
                child.SetText(PluginColumn, plugin?.Name ?? package.Slug);
                child.SetTooltipText(PluginColumn, plugin is null ? $"{package.Slug} is not in res://addons." : $"{plugin.Name} (res://addons/{package.Slug})");
                child.SetText(SourceColumn, $"{package.Version} · {Path.GetRelativePath(directory, package.ZipPath)}");
                child.SetTooltipText(SourceColumn, package.ZipPath);
                for (var column = 0; column < _tree.Columns; column++)
                {
                    child.SetSelectable(column, false);
                    if (plugin is null) child.SetCustomColor(column, EditorIcons.DisabledColor);
                }
            }
            item.Collapsed = _collapsedDirectories.Contains(directory);
            if (PackageFiles.PathComparer.Equals(directory, selected)) item.Select(SourceColumn);
        }
    }

    private static bool IsInside(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != path && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void DirectoryCollapsed(TreeItem item)
    {
        if (item.GetMetadata(SourceColumn).VariantType != Variant.Type.String) return;
        var directory = item.GetMetadata(SourceColumn).AsString();
        if (item.Collapsed) _collapsedDirectories.Add(directory);
        else _collapsedDirectories.Remove(directory);
    }

    /// <summary>
    /// ePlugin's built-in list, read-only: the installed add-ons first, as only those use it. It stays visible while it is
    /// turned off, so it can be reviewed before turning it on.
    /// </summary>
    private void RenderKnownPlugins(TreeItem root, Dictionary<string, PluginInfo> plugins)
    {
        var enabled = EGlobal.UseKnownPlugins;
        var group = Group(root, "Built-in list", enabled ? $"{KnownPlugins.All.Count} add-ons, read-only"
            : $"{KnownPlugins.All.Count} add-ons, read-only, turned off on the General tab");
        group.SetTooltipText(PluginColumn, "Update sites ePlugin knows for popular add-ons. They are used after the project's update site " +
                                           "and the plugin.cfg update_url, for plugins that name no working update site.");
        group.Collapsed = true;
        foreach (var known in KnownPlugins.All.OrderBy(k => !plugins.ContainsKey(k.Slug)))
        {
            var item = _tree.CreateItem(group);
            var plugin = plugins.GetValueOrDefault(known.Slug);
            item.SetText(PluginColumn, $"{known.Name} ({known.Slug})");
            item.SetTooltipText(PluginColumn, plugin is null ? $"Not installed: no res://addons/{known.Slug}."
                : plugin.UpdateUrl is null ? $"Installed: {plugin.Name} (res://addons/{known.Slug}). plugin.cfg sets no update_url, so the built-in update site is used."
                : $"Installed: {plugin.Name} (res://addons/{known.Slug}). plugin.cfg's update_url is tried before the built-in update site: {plugin.UpdateUrl}");
            item.SetText(SourceColumn, known.UpdateUrl);
            item.SetTooltipText(SourceColumn, string.Join("\n", new[] { known.UpdateUrl, known.DocumentationUrl, known.WebsiteUrl, known.SourceUrl }
                .Where(u => u is not null).Distinct()));
            for (var column = 0; column < _tree.Columns; column++)
            {
                item.SetSelectable(column, false);
                if (!enabled || plugin is null) item.SetCustomColor(column, EditorIcons.DisabledColor);
            }
        }
    }

    /// <summary>A heading row of the tree, which cannot be selected; <paramref name="note"/> follows in the source column.</summary>
    private TreeItem Group(TreeItem root, string title, string? note)
    {
        var group = _tree.CreateItem(root);
        group.SetText(PluginColumn, title);
        if (note is not null) group.SetText(SourceColumn, note);
        for (var column = 0; column < _tree.Columns; column++) group.SetSelectable(column, false);
        return group;
    }

    private void RenderStatus()
    {
        var index = _global.LocalIndex;
        var problems = new[] { _global.UpdateSitesProblem, _global.LocalDirectoriesProblem }.OfType<string>().ToArray();
        _status.TooltipText = problems.Length > 0 ? string.Join("\n", problems) : string.Join("\n", index.Failures.Select(f => $"{f.Path}: {f.Message}"));
        if (_error is not null || problems.Length > 0)
        {
            _status.Text = _error ?? problems[0];
            return;
        }
        var sites = _global.UpdateSites.Count == 0 ? "No update sites set for this project"
            : $"{_global.UpdateSites.Count} update site{(_global.UpdateSites.Count == 1 ? "" : "s")} set for this project";
        var local = _global.IsIndexingLocalSources ? "indexing local directories..."
            : _global.LocalDirectories.Count == 0 ? null
            : $"{index.Packages.Count} plugin package{(index.Packages.Count == 1 ? "" : "s")} in {index.Archives} ZIP file{(index.Archives == 1 ? "" : "s")}" +
              (index.Failures.Count == 0 ? "" : $", {index.Failures.Count} unreadable (hover for details)");
        _status.Text = local is null ? sites + "." : $"{sites} · {local}";
    }

    private void Buttons()
    {
        var sitesProblem = _global.UpdateSitesProblem is not null;
        var directoriesProblem = _global.LocalDirectoriesProblem is not null;
        _rescan.Disabled = _global.IsIndexingLocalSources || _global.LocalDirectories.Count == 0;
        _addSite.Disabled = sitesProblem;
        _addDirectory.Disabled = directoriesProblem;
        _edit.Disabled = sitesProblem || SelectedSlug() is null;
        _remove.Disabled = !((SelectedSlug() is not null && !sitesProblem) || (SelectedDirectory() is not null && !directoriesProblem));
    }

    private string? SelectedSlug() => SelectedMetadata(PluginColumn);
    private string? SelectedDirectory() => SelectedMetadata(SourceColumn);
    private string? SelectedMetadata(int column) =>
        _tree.GetSelected() is { } item && item.GetMetadata(column).VariantType == Variant.Type.String ? item.GetMetadata(column).AsString() : null;

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
            _hint.AddThemeColorOverride("font_color", EditorIcons.ErrorColor);
            return;
        }
        _hint.RemoveThemeColorOverride("font_color");
        var slug = _slug.Selected >= 0 ? _slug.GetItemMetadata(_slug.Selected).AsString() : null;
        var configured = slug is null ? null : _configuredUrls.GetValueOrDefault(slug);
        _hint.Text = DefaultHint + (configured is null
            ? "\nThe plugin's plugin.cfg sets no update_url."
            : $"\nWhen this site does not work, plugin.cfg's update_url is used: {configured}");
    }

    private void AddDirectory(string directory)
    {
        _error = null;
        try { if (!_global.AddLocalDirectory(directory)) _error = "This directory is already listed."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot add the directory: " + ex.Message; }
        Render();
    }

    private void RemoveSelected()
    {
        _error = null;
        if (SelectedSlug() is { } slug)
        {
            try { if (_global.RemoveUpdateSite(slug)) Changed?.Invoke(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot remove the update site: " + ex.Message; }
        }
        else if (SelectedDirectory() is { } directory)
        {
            try { _global.RemoveLocalDirectory(directory); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot remove the directory: " + ex.Message; }
        }
        Render();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_global is not null) _global.LocalIndexChanged += Render;
    }

    private void ReleaseCallbacks()
    {
        _signals.Dispose();
        if (_global is not null) _global.LocalIndexChanged -= Render;
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
