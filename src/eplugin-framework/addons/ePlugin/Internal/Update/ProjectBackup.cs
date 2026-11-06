#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// The copy of the project files (<c>.csproj</c>, <c>.sln</c>, <c>nuget.config</c>, <c>project.godot</c>) a journal restores
/// on rollback. Replacing it writes the new copy to <c>backup-project.new</c>, moves the old one to <c>.old</c> and the new
/// one into place, and only then saves the journal. Used by both the applier and early recovery, which stays independent
/// of <c>EGlobal</c> and recipe execution, and so does this class.
/// </summary>
internal static class ProjectBackup
{
    public const string Folder = "backup-project";

    /// <summary>Whether a file in the project root is one of the project files the backup holds.</summary>
    public static bool Includes(string fileName) =>
        Path.GetExtension(fileName) is ".csproj" or ".sln" or ".slnx" || fileName.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) || fileName == "project.godot";

    /// <summary>
    /// Copies the project files of <paramref name="projectRoot"/> into the journal's backup folder and lists them in
    /// <see cref="UpdateJournal.ProjectFiles"/>; the caller saves the journal.
    /// </summary>
    /// <param name="replace">Replaces an existing backup: the new copy is written next to the old one and swapped in, so
    /// it is never half written (see <see cref="Find"/>).</param>
    public static void Write(string projectRoot, UpdateJournal journal, bool replace = false)
    {
        var backup = Path.Combine(journal.Directory, Folder);
        var target = replace ? backup + ".new" : backup;
        if (replace) AddonFiles.DeleteTree(target);
        Directory.CreateDirectory(target);
        var files = new List<string>();
        foreach (var file in Directory.GetFiles(projectRoot).Where(f => Includes(Path.GetFileName(f))))
        {
            files.Add(Path.GetFileName(file));
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        }
        // Informational: restoring lists the backup folder itself (Find), as the caller saves the journal later.
        journal.ProjectFiles = files;
        if (replace)
        {
            var old = backup + ".old";
            AddonFiles.DeleteTree(old);
            if (Directory.Exists(backup)) AddonFiles.MoveDirectory(backup, old);
            AddonFiles.MoveDirectory(target, backup);
            AddonFiles.DeleteTree(old);
        }
    }

    /// <summary>
    /// The folder to restore from and its files. When a replacement stopped between its two moves, the new copy is
    /// complete (the old one is moved aside only after it) and is used; without the new copy, the old one is. The files
    /// are those the folder holds: each copy is complete before it is moved into place, while the journal listing them is
    /// saved only afterwards, so after an interruption it may still list the previous copy's (<paramref name="recorded"/>
    /// is used only when the folder is missing).
    /// </summary>
    public static (string Directory, IReadOnlyList<string> Files) Find(string journalDirectory, IReadOnlyList<string> recorded)
    {
        var backup = Path.Combine(journalDirectory, Folder);
        var directory = Directory.Exists(backup) || !Directory.Exists(backup + ".old") ? backup
            : Directory.Exists(backup + ".new") ? backup + ".new" : backup + ".old";
        if (!Directory.Exists(directory)) return (directory, recorded);
        return (directory, Directory.GetFiles(directory).Select(f => Path.GetFileName(f)).Where(Includes).ToArray());
    }

    /// <summary>
    /// Copies <paramref name="files"/> from <paramref name="backup"/> (see <see cref="Find"/>) over the project's, and
    /// deletes a <c>nuget.config</c> the backup does not hold: the update created it.
    /// </summary>
    /// <param name="projectSettings">Restores <c>project.godot</c> too. The live editor owns it, so the applier's rollback
    /// leaves it alone and restores autoloads through recipe operations; early recovery restores it, and reloads the
    /// settings before the restart.</param>
    public static void Restore(string projectRoot, string backup, IReadOnlyList<string> files, bool projectSettings)
    {
        foreach (var file in files.Where(f => projectSettings || f != "project.godot"))
            AddonFiles.Overwrite(PackageFiles.Inside(backup, file), PackageFiles.Inside(projectRoot, file));
        if (!files.Any(f => f.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))) AddonFiles.DeleteFile(Path.Combine(projectRoot, "nuget.config"));
    }
}
#endif
