#if TOOLS
using System;
using System.IO;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Shows or hides a managed directory by renaming it with or without a leading '.' and toggling the Windows hidden
/// attribute. A directory already in the wanted state is left as it is: that is what the caller wants, only already
/// done. A directory found in neither form is logged, not a failure; only both forms existing at once is, because
/// neither can be renamed onto the other without losing files.
/// </summary>
[Tool]
public static class ShowHideHelper
{
    internal static void ShowDirectory(PluginContext plugin, string directory)
    {
        var (shown, hidden) = Forms(directory);
        var baseDirectory = BaseDirectory(plugin);
        if (shown.Exists && hidden.Exists) throw Conflict(shown, hidden, baseDirectory);
        if (shown.Exists)
        {
            shown.Attributes = FileAttributes.Normal;
            plugin.Logger?.Log($"Already shown: <plugin>{Relative(shown, baseDirectory)}");
        }
        else if (hidden.Exists)
        {
            hidden.Attributes = FileAttributes.Normal;
            plugin.Logger?.Log($"Showing: <plugin>{Relative(hidden, baseDirectory)} (to <plugin>{Relative(shown, baseDirectory)})");
            hidden.MoveTo(shown.FullName);
        }
        else
        {
            plugin.Logger?.Warn($"Cannot show <plugin>{Relative(hidden, baseDirectory)}: the directory does not exist.");
        }
    }

    internal static void HideDirectory(PluginContext plugin, string directory)
    {
        var (shown, hidden) = Forms(directory);
        var baseDirectory = BaseDirectory(plugin);
        if (shown.Exists && hidden.Exists) throw Conflict(shown, hidden, baseDirectory);
        if (hidden.Exists)
        {
            hidden.Attributes = FileAttributes.Hidden;
            plugin.Logger?.Log($"Already hidden: <plugin>{Relative(hidden, baseDirectory)}");
        }
        else if (shown.Exists)
        {
            shown.Attributes = FileAttributes.Hidden;
            plugin.Logger?.Log($"Hiding: <plugin>{Relative(shown, baseDirectory)} (to <plugin>{Relative(hidden, baseDirectory)})");
            shown.MoveTo(hidden.FullName);
        }
        else
        {
            plugin.Logger?.Warn($"Cannot hide <plugin>{Relative(shown, baseDirectory)}: the directory does not exist.");
        }
    }

    /// <summary>The visible and the hidden form of a directory, whichever of the two the recipe names.</summary>
    private static (DirectoryInfo Shown, DirectoryInfo Hidden) Forms(string directory)
    {
        var path = ProjectSettings.GlobalizePath(directory).TrimEnd('/', '\\');
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path).TrimStart('.');
        return (new DirectoryInfo(Path.Combine(parent, name)), new DirectoryInfo(Path.Combine(parent, "." + name)));
    }

    private static InvalidOperationException Conflict(DirectoryInfo shown, DirectoryInfo hidden, string? baseDirectory) =>
        new($"Both <plugin>{Relative(shown, baseDirectory)} and <plugin>{Relative(hidden, baseDirectory)} exist; remove the outdated one.");

    private static string? BaseDirectory(PluginContext plugin) =>
        string.IsNullOrEmpty(plugin.Directory) ? null : Path.GetFullPath(ProjectSettings.GlobalizePath(plugin.Directory));

    private static string Relative(DirectoryInfo directory, string? baseDirectory) =>
        baseDirectory is null ? directory.FullName : directory.FullName.Replace(baseDirectory, "");
}
#endif
