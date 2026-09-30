#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>Temporarily hides Project Settings and reopens it through Godot's normal menu action.</summary>
internal sealed class ProjectSettingsDialog : IDisposable
{
    private readonly PopupMenu _menu;
    private readonly int _itemId;

    private ProjectSettingsDialog(PopupMenu menu, int itemId)
    {
        _menu = menu;
        _itemId = itemId;
    }

    public static IDisposable? TryHide()
    {
        if (!Engine.IsEditorHint() || DisplayServer.GetName() == "headless")
        {
            return null;
        }

        var editorRoot = EditorInterface.Singleton.GetBaseControl().GetParent();
        Window? dialog = null;
        PopupMenu? menu = null;
        var itemId = -1;
        foreach (var node in Descendants(editorRoot))
        {
            if (node is Window window && node.GetClass() == "ProjectSettingsEditor" && window.Visible)
            {
                dialog = window;
            }

            if (node is not PopupMenu popup)
            {
                continue;
            }

            for (var index = 0; index < popup.ItemCount; index++)
            {
                // Godot gives this shortcut an untranslated resource name, even when the menu is localized.
                if (popup.GetItemShortcut(index)?.ResourceName == "Project Settings...")
                {
                    menu = popup;
                    itemId = popup.GetItemId(index);
                    break;
                }
            }
        }

        // These are editor internals. Leave the dialog alone if a future Godot version changes either lookup.
        if (dialog is null || menu is null)
        {
            return null;
        }

        dialog.Hide();
        return new ProjectSettingsDialog(menu, itemId);
    }

    public void Dispose()
    {
        if (GodotObject.IsInstanceValid(_menu) && !_menu.IsQueuedForDeletion())
        {
            // Plain Show/Popup would leave the Plugins list stale. The menu action updates it, and deferring
            // lets the current plugin toggle finish before Godot replaces the TreeItems used by its callback.
            // Queue a native call so reopening also survives a C# assembly reload.
            _menu.CallDeferred(GodotObject.MethodName.EmitSignal, PopupMenu.SignalName.IdPressed, _itemId);
        }
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren(true))
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
#endif
