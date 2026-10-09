#if TOOLS
using System;
using System.IO;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Adds, removes and rescans the user's local plugin directories. The layout lives in EPluginManagerDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class LocalSourcesDialog : AcceptDialog
{
    private EGlobal _global = null!;
    private ItemList _list = null!;
    private Label _status = null!;
    private Button _rescan = null!;
    private Button _add = null!;
    private Button _remove = null!;
    private FileDialog _folderDialog = null!;
    private string? _error;

    public void Initialize(EGlobal global)
    {
        _global = global;
        _list = GetNode<ItemList>("%LocalSourceList"); _status = GetNode<Label>("%LocalSourceStatus");
        _rescan = GetNode<Button>("%RescanLocalSourcesButton"); _add = GetNode<Button>("%AddLocalSourceButton"); _remove = GetNode<Button>("%RemoveLocalSourceButton"); _folderDialog = GetNode<FileDialog>("%LocalSourceFolderDialog");
        EditorWindows.Prepare(this); EditorWindows.Prepare(_folderDialog);
        _rescan.Pressed += () => { _error = null; _ = _global.RebuildLocalIndexAsync(); };
        _add.Pressed += () => _folderDialog.PopupCentered();
        _remove.Pressed += RemoveSelected;
        _list.ItemSelected += _ => _remove.Disabled = _global.LocalDirectoriesProblem is not null;
        _folderDialog.DirSelected += Add;
    }

    public void Open()
    {
        _error = null;
        // Another open editor may have changed the list, which is shared by all projects.
        _global.ReloadLocalDirectories();
        Render();
        PopupCentered();
    }

    private void Render()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        var selected = _list.GetSelectedItems() is { Length: > 0 } items ? _list.GetItemMetadata(items[0]).AsString() : null;
        _list.Clear();
        var index = _global.LocalIndex;
        foreach (var directory in _global.LocalDirectories)
        {
            // Only the index looks at the disk: an unreachable network share can block for a long time.
            var state = index.StateOf(directory);
            var missing = state is { Exists: false };
            var item = _list.AddItem(missing ? directory + "  (not found)" : directory);
            _list.SetItemMetadata(item, directory);
            _list.SetItemTooltip(item, state is null ? "Not indexed yet."
                : missing ? "This directory was not found when it was indexed. It stays in the list and is indexed again when it reappears."
                : $"{state.Packages} plugin package{(state.Packages == 1 ? "" : "s")} found here.");
            if (missing) _list.SetItemCustomFgColor(item, EditorInterface.Singleton.GetEditorTheme()?.GetColor("font_disabled_color", "Button") ?? new Color(0.5f, 0.5f, 0.5f));
            if (directory == selected) _list.Select(item);
        }
        var problem = _global.LocalDirectoriesProblem;
        _rescan.Disabled = _global.IsIndexingLocalSources || _global.LocalDirectories.Count == 0;
        _add.Disabled = problem is not null;
        _remove.Disabled = problem is not null || _list.GetSelectedItems().Length == 0;
        _status.TooltipText = problem ?? string.Join("\n", index.Failures.Select(f => $"{f.Path}: {f.Message}"));
        _status.Text = _error ?? problem ?? (_global.IsIndexingLocalSources ? "Indexing..."
            : _global.LocalDirectories.Count == 0 ? "Add a directory to offer the plugin ZIP files in it as updates."
            : $"{index.Packages.Count} plugin package{(index.Packages.Count == 1 ? "" : "s")} in {index.Archives} ZIP file{(index.Archives == 1 ? "" : "s")}" +
              (index.Failures.Count == 0 ? "" : $", {index.Failures.Count} unreadable (hover for details)"));
    }

    private void Add(string directory)
    {
        _error = null;
        try { if (!_global.AddLocalDirectory(directory)) _error = "This directory is already listed."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot add the directory: " + ex.Message; }
        Render();
    }

    private void RemoveSelected()
    {
        if (_list.GetSelectedItems() is not { Length: > 0 } items) return;
        _error = null;
        try { _global.RemoveLocalDirectory(_list.GetItemMetadata(items[0]).AsString()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { _error = "Cannot remove the directory: " + ex.Message; }
        Render();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_global is not null) _global.LocalIndexChanged += Render;
    }

    public override void _ExitTree()
    {
        if (_global is not null) _global.LocalIndexChanged -= Render;
        base._ExitTree();
    }
}
#endif
