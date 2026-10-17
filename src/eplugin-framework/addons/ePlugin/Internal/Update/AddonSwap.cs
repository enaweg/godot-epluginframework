#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>How an update replaces a plugin folder with its staged version and puts the backup back on rollback.</summary>
internal interface IAddonSwap
{
    /// <summary>Shown in the log, so it tells which strategy an update used.</summary>
    string Name { get; }

    /// <summary>
    /// Replaces <paramref name="installed"/> with <paramref name="staging"/>, leaving the old version complete in
    /// <paramref name="backup"/>. Files that can be moved but not deleted go to <paramref name="discard"/>.
    /// </summary>
    /// <returns>How many old files were still in use and had to be moved to <paramref name="discard"/>.</returns>
    int Install(string installed, string staging, string backup, string discard);

    /// <summary>Puts <paramref name="backup"/> back in place of <paramref name="installed"/> and removes the backup.</summary>
    /// <returns>How many files of the replaced version were still in use and had to be moved to <paramref name="discard"/>.</returns>
    int Restore(string installed, string backup, string discard);
}

/// <summary>Moves the whole plugin folder: the old one becomes the backup, the staging folder takes its place.</summary>
internal sealed class AddonFolderSwap : IAddonSwap
{
    public static readonly AddonFolderSwap Instance = new();
    public string Name => "folder swap";

    public int Install(string installed, string staging, string backup, string discard)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        AddonFiles.MoveDirectory(installed, backup);
        AddonFiles.MoveDirectory(staging, installed);
        return 0;
    }

    public int Restore(string installed, string backup, string discard)
    {
        if (Directory.Exists(installed)) Directory.Delete(installed, true);
        AddonFiles.MoveDirectory(backup, installed);
        return 0;
    }
}

/// <summary>
/// Replaces the files of a plugin folder one by one and keeps the folder itself. In the editor on Windows, Godot loads
/// every GDExtension library from a copy next to it whose name starts with '~' (<see cref="PackageFiles.IsRuntimeCopy"/>)
/// and keeps that copy open, so the folder cannot be moved; the original library can be deleted and replaced. On other
/// systems the original is loaded, and a file that is deleted (rather than overwritten) stays mapped for the old library.
/// The runtime copies are never backed up, deleted or installed: Godot removes them itself when it closes the library.
/// <para>
/// The backup is copied completely, and renamed into place, before any installed file changes, so an interrupted swap or
/// restore can always be repeated from it. Restoring copies rather than moves for the same reason.
/// </para>
/// </summary>
internal sealed class AddonFileSwap : IAddonSwap
{
    public static readonly AddonFileSwap Instance = new();
    public string Name => "file-by-file swap keeping Godot's runtime copies";

    public int Install(string installed, string staging, string backup, string discard)
    {
        AddonFiles.RefuseLink(installed);
        var partial = backup + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        if (Directory.Exists(backup)) throw new IOException("A backup of the plugin already exists: " + backup);
        AddonFiles.CopyTree(installed, partial);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        AddonFiles.MoveDirectory(partial, backup);
        var discarded = AddonFiles.RemoveContents(installed, discard);
        AddonFiles.MoveContents(staging, installed);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        return discarded;
    }

    public int Restore(string installed, string backup, string discard)
    {
        Directory.CreateDirectory(installed);
        AddonFiles.RefuseLink(installed);
        var discarded = AddonFiles.RemoveContents(installed, discard);
        AddonFiles.CopyTree(backup, installed);
        Directory.Delete(backup, true);
        return discarded;
    }
}

internal static class AddonFiles
{
    public static void MoveDirectory(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (attempt < 2) { Thread.Sleep(200); }
        }
    }

    public static void RefuseLink(string directory)
    {
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{directory} is a link; update the plugin where it points to.");
    }

    /// <summary>Every entry below <paramref name="root"/> except Godot's runtime copies; refuses links.</summary>
    private static IEnumerable<string> Entries(string root, bool directories)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links inside the plugin folder cannot be swapped file by file: " + entry);
            if (Directory.Exists(entry))
            {
                foreach (var child in Entries(entry, directories)) yield return child;
                if (directories) yield return entry;
            }
            else if (!PackageFiles.IsRuntimeCopy(Path.GetFileName(entry))) yield return entry;
        }
    }

    /// <summary>Copies every file except Godot's runtime copies, with its timestamps, into <paramref name="target"/>.</summary>
    public static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Entries(source, false).ToArray())
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(file));
        }
        // Empty folders belong to the plugin, too (e.g. a hidden managed directory).
        foreach (var directory in Entries(source, true).Where(Directory.Exists).ToArray())
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
    }

    /// <summary>
    /// Deletes every file below <paramref name="root"/> except Godot's runtime copies, then the folders left empty. A file
    /// that cannot be deleted, e.g. a native library Godot loaded directly, is moved to <paramref name="discard"/>, which
    /// renaming an open library allows; it is deleted with the journal once the editor no longer uses it.
    /// </summary>
    /// <returns>How many files had to be moved to <paramref name="discard"/>.</returns>
    public static int RemoveContents(string root, string discard)
    {
        var discarded = 0;
        foreach (var file in Entries(root, false).ToArray())
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var to = Path.Combine(discard, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                for (var n = 1; File.Exists(to); n++) to = Path.Combine(discard, Path.GetRelativePath(root, file) + "." + n);
                File.Move(file, to);
                discarded++;
            }
        }
        foreach (var directory in Entries(root, true).Where(Directory.Exists).ToArray())
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        return discarded;
    }

    /// <summary>Moves every file below <paramref name="source"/> to the same place below <paramref name="target"/>.</summary>
    public static void MoveContents(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Entries(source, false).ToArray())
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(file, to);
        }
        foreach (var directory in Entries(source, true).Where(Directory.Exists).ToArray())
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
    }
}
#endif
