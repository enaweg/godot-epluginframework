#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Shows or hides a managed directory by renaming it with or without a leading '.' and toggling the Windows hidden
/// attribute. A directory already in the wanted state is left as it is: that is what the caller wants, only already
/// done. A directory found in neither form is logged, not a failure; only both forms existing at once is, because
/// neither can be renamed onto the other without losing files.
/// <para>
/// Only a directory inside the plugin's directory is managed. One outside it (another plugin's, the project's, or the
/// plugin's own directory or one holding it) is refused when it is shown, and left as it is when it is hidden, so a
/// recipe that names one can still be uninstalled. A relative path is relative to the project, like <c>res://</c>.
/// </para>
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
        RefuseEnclosingDirectory(directory, shown, baseDirectory);
        if (!IsInside(shown, baseDirectory))
            throw new InvalidOperationException($"The managed directory {directory} is outside the plugin's directory. " +
                "Only directories inside the plugin can be added with AddDirectory.");
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
            Move(hidden, shown.FullName);
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
        if (!IsInside(shown, baseDirectory))
        {
            // showing refuses it, and renaming it would change what is not the plugin's; failing would keep the recipe
            // from being uninstalled
            plugin.Logger?.Warn($"Not hiding the managed directory {directory}: it is outside the plugin's directory.");
            return;
        }
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
            Move(shown, hidden.FullName);
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

    /// <summary>
    /// Refuses the plugin's own directory and the ones that hold it (the add-ons folder, the project): renaming one of
    /// them would hide the plugin itself, or everything else with it.
    /// </summary>
    private static void RefuseEnclosingDirectory(string directory, DirectoryInfo shown, string? baseDirectory)
    {
        if (baseDirectory is null) return;
        var plugin = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        var managed = Path.TrimEndingDirectorySeparator(shown.FullName) + Path.DirectorySeparatorChar;
        if (plugin.StartsWith(managed, PackageFiles.PathComparison))
            throw new InvalidOperationException($"The managed directory {directory} is the plugin's own directory or holds it. " +
                "Only directories inside the plugin can be added with AddDirectory.");
    }

    /// <summary>Whether a managed directory is below the plugin's directory; any directory when that is unknown.</summary>
    private static bool IsInside(DirectoryInfo shown, string? baseDirectory)
    {
        if (baseDirectory is null) return true;
        var plugin = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        var managed = Path.TrimEndingDirectorySeparator(shown.FullName) + Path.DirectorySeparatorChar;
        return managed.Length > plugin.Length && managed.StartsWith(plugin, PackageFiles.PathComparison);
    }

    /// <summary>Whether two recipe entries name the same managed directory, in either form.</summary>
    internal static bool SameDirectory(string a, string b) =>
        string.Equals(Forms(a).Shown.FullName, Forms(b).Shown.FullName, PackageFiles.PathComparison);

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
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Globalize(directory)));
        var parent = Path.GetDirectoryName(path) ?? path;
        var name = Path.GetFileName(path).TrimStart('.');
        return (new DirectoryInfo(Path.Combine(parent, name)), new DirectoryInfo(Path.Combine(parent, "." + name)));
    }

    /// <summary>
    /// The file system path a recipe names. A relative path is relative to the project, like a project path in a recipe,
    /// and not to the editor's working directory.
    /// </summary>
    private static string Globalize(string directory) =>
        directory.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(directory)
            ? ProjectSettings.GlobalizePath(directory)
            : ProjectSettings.GlobalizePath("res://" + directory.Replace('\\', '/'));

    private static InvalidOperationException Conflict(DirectoryInfo shown, DirectoryInfo hidden, string? baseDirectory) =>
        new($"Both <plugin>{Relative(shown, baseDirectory)} and <plugin>{Relative(hidden, baseDirectory)} exist; remove the outdated one.");

    private static string? BaseDirectory(PluginContext plugin) =>
        string.IsNullOrEmpty(plugin.Directory) ? null : Path.GetFullPath(ProjectSettings.GlobalizePath(plugin.Directory));

    private static string Relative(FileSystemInfo directory, string? baseDirectory) =>
        baseDirectory is null ? directory.FullName : directory.FullName.Replace(baseDirectory, "");

    /// <summary>Renames a managed directory, telling what usually keeps it from being renamed when that fails.</summary>
    private static void Move(DirectoryInfo directory, string destination)
    {
        try
        {
            directory.MoveTo(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Renaming {directory.FullName} to {Path.GetFileName(destination)} failed: {ex.Message} " +
                                  "Close programs that use files in it (a file explorer, an IDE, a running game, a virus scanner) " +
                                  "and check that it is writable, then retry.", ex);
        }
    }
}
#endif
