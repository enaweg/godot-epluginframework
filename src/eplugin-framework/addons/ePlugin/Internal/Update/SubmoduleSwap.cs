#if TOOLS
using System;
using System.IO;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// Updates a plugin whose folder is a git submodule by checking out the new version's commit in it, as a detached HEAD
/// like <c>git submodule update</c> leaves it; its repository and untracked files stay. The backup is the old commit's
/// files, written before the checkout, for the interim bridge, the GDExtension comparison and a manual repair; restoring
/// checks the old commit out again. Both refuse a submodule with uncommitted changes to tracked files, and an install
/// refuses one that is no longer at the commit the journal recorded.
/// </summary>
internal sealed class SubmoduleSwap(IGitRunner git, UpdatePluginJournal plugin) : IAddonSwap
{
    private SubmoduleJournal Submodule => plugin.Submodule ?? throw new InvalidOperationException($"The update of {plugin.Slug} is not a submodule update.");

    public string Name => $"git checkout of {Submodule.Tag} in the submodule";

    public int Install(string installed, string staging, string backup, string discard)
    {
        var submodule = Submodule;
        AddonFiles.RefuseLink(installed);
        var head = SubmoduleGit.Head(git, installed);
        // Repeated after the checkout: nothing is left to do.
        if (Same(head, submodule.NewCommit) && Directory.Exists(backup)) return 0;
        if (!Same(head, submodule.OldCommit))
            throw new IOException($"The submodule {installed} is at {SubmoduleSource.Short(head)} rather than {SubmoduleSource.Short(submodule.OldCommit)}, " +
                                  "where the update found it; check for updates again.");
        RefuseChanges(installed);
        if (!Directory.Exists(backup))
        {
            var partial = backup + ".partial";
            AddonFiles.DeleteTree(partial);
            SubmoduleGit.Export(git, installed, submodule.OldCommit, partial);
            AddonFiles.MoveDirectory(partial, backup);
        }
        SubmoduleGit.Checkout(git, installed, submodule.NewCommit);
        return 0;
    }

    public int Restore(string installed, string backup, string discard)
    {
        var submodule = Submodule;
        AddonFiles.RefuseLink(installed);
        // Old files the interim bridge kept are untracked in the new version, and would keep the old one from being checked out.
        InterimBridge.Remove(installed, plugin.Preserved);
        if (!Same(SubmoduleGit.Head(git, installed), submodule.OldCommit))
        {
            RefuseChanges(installed);
            SubmoduleGit.Checkout(git, installed, submodule.OldCommit);
        }
        // Moved aside before it is deleted, so a repeated rollback finds no backup to restore again (see AddonFileSwap).
        if (!Directory.Exists(backup)) return 0;
        var restored = AddonFileSwap.Restored(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(restored)!);
        for (var n = 1; Directory.Exists(restored); n++) restored = AddonFileSwap.Restored(backup) + "." + n;
        AddonFiles.MoveDirectory(backup, restored);
        try { AddonFiles.DeleteTree(restored); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    private void RefuseChanges(string installed)
    {
        var changes = SubmoduleGit.Changes(git, installed);
        if (changes.Count > 0) throw new IOException(SubmoduleSource.LocalChanges(installed, changes).Message);
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
#endif
