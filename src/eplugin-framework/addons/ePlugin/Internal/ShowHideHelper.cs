#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Shows or hides a managed directory by renaming it with or without a leading '.' and toggling the Windows hidden
/// attribute. A directory already in the wanted state is left as it is: that is what the caller wants, only already
/// done. A directory found in neither form is logged, not a failure; only both forms existing at once is, because
/// neither can be renamed onto the other without losing files.
/// <para>
/// A directory may hold a GDExtension only when every one of its extensions is <c>reloadable</c>: Godot does not load
/// extensions in hidden directories, so showing loads them and hiding unloads them first. That also closes the library
/// copies the editor on Windows keeps open, which would keep the directory from being renamed. One that is not
/// reloadable cannot be unloaded safely while the editor runs, so such a directory is refused when it is shown.
/// </para>
/// </summary>
[Tool]
internal static class ShowHideHelper
{
    /// <param name="host">The editor, which loads the GDExtensions of the directory; null skips that.</param>
    internal static void ShowDirectory(PluginContext plugin, string directory, IEditorHost? host = null)
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
            RefuseFixedExtensions(hidden, baseDirectory);
            hidden.Attributes = FileAttributes.Normal;
            plugin.Logger?.Log($"Showing: <plugin>{Relative(hidden, baseDirectory)} (to <plugin>{Relative(shown, baseDirectory)})");
            hidden.MoveTo(shown.FullName);
            if (host is null) return;
            // The editor's next file system scan would load them too; loading them now lets the plugin use them at once.
            foreach (var (file, _) in Extensions(shown).Where(e => !host.IsExtensionLoaded(e.File)))
            {
                if (host.LoadExtension(file)) plugin.Logger?.Log($"Loaded the GDExtension <plugin>{Relative(new FileInfo(file), baseDirectory)}");
                else plugin.Logger?.Warn($"Godot could not load the GDExtension <plugin>{Relative(new FileInfo(file), baseDirectory)}; it tries again when it scans the project.");
            }
        }
        else
        {
            plugin.Logger?.Warn($"Cannot show <plugin>{Relative(hidden, baseDirectory)}: the directory does not exist.");
        }
    }

    /// <param name="host">The editor, which unloads the GDExtensions of the directory; null skips that.</param>
    internal static void HideDirectory(PluginContext plugin, string directory, IEditorHost? host = null)
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
            if (host is not null)
            {
                var loaded = Extensions(shown).Where(e => host.IsExtensionLoaded(e.File)).ToArray();
                var fixedExtension = loaded.FirstOrDefault(e => e.Config is not { Reloadable: true }).File;
                if (fixedExtension is not null) throw FixedExtension(fixedExtension, baseDirectory);
                foreach (var (file, _) in loaded)
                {
                    if (!host.UnloadExtension(file))
                        throw new InvalidOperationException($"Godot could not unload the GDExtension <plugin>{Relative(new FileInfo(file), baseDirectory)}, so its directory cannot be hidden.");
                    plugin.Logger?.Log($"Unloaded the GDExtension <plugin>{Relative(new FileInfo(file), baseDirectory)}");
                }
            }
            shown.Attributes = FileAttributes.Hidden;
            plugin.Logger?.Log($"Hiding: <plugin>{Relative(shown, baseDirectory)} (to <plugin>{Relative(hidden, baseDirectory)})");
            shown.MoveTo(hidden.FullName);
        }
        else
        {
            plugin.Logger?.Warn($"Cannot hide <plugin>{Relative(shown, baseDirectory)}: the directory does not exist.");
        }
    }

    /// <summary>
    /// The <c>.gdextension</c> files below <paramref name="directory"/>, with what they say (null when unreadable).
    /// </summary>
    internal static IReadOnlyList<(string File, GDExtensionConfig? Config)> Extensions(DirectoryInfo directory) =>
        GDExtensionConfig.ReadAll(directory.FullName).Select(e => (Path.Combine(directory.FullName, e.Key), e.Value)).ToArray();

    /// <summary>Refuses to show a directory with a GDExtension that could not be unloaded to hide it again.</summary>
    private static void RefuseFixedExtensions(DirectoryInfo hidden, string? baseDirectory)
    {
        var fixedExtension = Extensions(hidden).FirstOrDefault(e => e.Config is not { Reloadable: true }).File;
        if (fixedExtension is not null) throw FixedExtension(fixedExtension, baseDirectory);
    }

    private static InvalidOperationException FixedExtension(string file, string? baseDirectory) =>
        new($"The managed directory holds the GDExtension <plugin>{Relative(new FileInfo(file), baseDirectory)}, which is not reloadable " +
            "(reloadable=true in its [configuration]). Godot cannot unload it while the editor runs, so the directory could not be hidden " +
            "again. Make the extension reloadable or keep it outside directories added with AddDirectory.");

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

    private static string Relative(FileSystemInfo directory, string? baseDirectory) =>
        baseDirectory is null ? directory.FullName : directory.FullName.Replace(baseDirectory, "");
}
#endif
