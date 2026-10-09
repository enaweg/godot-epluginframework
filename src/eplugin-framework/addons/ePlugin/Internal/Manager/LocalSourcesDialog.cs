#if TOOLS
using System;
using System.IO;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Adds and removes the user's local plugin directories. The layout lives in EPluginManagerDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class LocalSourcesDialog : AcceptDialog
{
    private EGlobal _global = null!;
    private ItemList _list = null!;
    private Label _status = null!;
    private Button _remove = null!;
    private FileDialog _folderDialog = null!;
    private string? _error;

    public void Initialize(EGlobal global)
    {
        _global = global;
        _list = GetNode<ItemList>("%LocalSourceList"); _status = GetNode<Label>("%LocalSourceStatus");
        _remove = GetNode<Button>("%RemoveLocalSourceButton"); _folderDialog = GetNode<FileDialog>("%LocalSourceFolderDialog");
        var scale = EditorInterface.Singleton.GetEditorScale();
        Size = (Vector2I)((Vector2)Size * scale); MinSize = (Vector2I)((Vector2)MinSize * scale);
        _folderDialog.Size = (Vector2I)((Vector2)_folderDialog.Size * scale);
        GetNode<Button>("%AddLocalSourceButton").Pressed += () => _folderDialog.PopupCentered();
        _remove.Pressed += RemoveSelected;
        _list.ItemSelected += _ => _remove.Disabled = false;
        _folderDialog.DirSelected += Add;
        _global.LocalIndexChanged += Render;
    }

    public void Open()
    {
        _error = null;
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
            var missing = !Directory.Exists(directory);
            var item = _list.AddItem(missing ? directory + "  (not found)" : directory);
            _list.SetItemMetadata(item, directory);
            _list.SetItemTooltip(item, missing
                ? "This directory does not exist right now. It stays in the list and is indexed again when it reappears."
                : $"{index.Packages.Count(p => IsBelow(p.ZipPath, directory))} plugin packages found here.");
            if (missing) _list.SetItemCustomFgColor(item, EditorInterface.Singleton.GetEditorTheme()?.GetColor("font_disabled_color", "Button") ?? new Color(0.5f, 0.5f, 0.5f));
            if (directory == selected) _list.Select(item);
        }
        _remove.Disabled = _list.GetSelectedItems().Length == 0;
        _status.TooltipText = string.Join("\n", index.Failures.Select(f => $"{f.Path}: {f.Message}"));
        _status.Text = _error ?? (_global.IsIndexingLocalSources ? "Indexing..."
            : _global.LocalDirectories.Count == 0 ? "Add a directory to offer the plugin ZIP files in it as updates."
            : $"{index.Packages.Count} plugin package{(index.Packages.Count == 1 ? "" : "s")} in {index.Archives} ZIP file{(index.Archives == 1 ? "" : "s")}" +
              (index.Failures.Count == 0 ? "" : $", {index.Failures.Count} unreadable (hover for details)"));
    }

    private static bool IsBelow(string file, string directory) =>
        file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Add(string directory)
    {
        _error = null;
        try { if (!_global.AddLocalDirectory(directory)) _error = "This directory is already listed."; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { _error = "Cannot add the directory: " + ex.Message; }
        Render();
    }

    private void RemoveSelected()
    {
        if (_list.GetSelectedItems() is not { Length: > 0 } items) return;
        _error = null;
        try { _global.RemoveLocalDirectory(_list.GetItemMetadata(items[0]).AsString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error = "Cannot remove the directory: " + ex.Message; }
        Render();
    }

    public override void _ExitTree()
    {
        if (_global is not null) _global.LocalIndexChanged -= Render;
        base._ExitTree();
    }
}
#endif
