#if TOOLS
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>What staging a submodule update found: the commit it is at now, and why the update must not start.</summary>
internal sealed record StagedSubmodule(string Head, IReadOnlyList<Finding> Problems);

/// <summary>
/// The update source of a plugin whose folder is a git submodule: every version tag of the submodule's <c>origin</c>
/// remote is a version, like the tags of a <see cref="GitSource"/>, compared with the installed plugin.cfg. Updating
/// checks out the tag's commit in the submodule (<see cref="SubmoduleSwap"/>) rather than replacing its files, so its
/// repository stays, and the project's repository then shows the changed submodule commit for the user to commit.
/// Everything here runs git in the submodule; checks and staging run off the editor thread.
/// </summary>
internal sealed class SubmoduleSource(IGitRunner git, PluginRepository repository) : IUpdateSource, IVersionListSource
{
    /// <summary>The remote a submodule is fetched from, as <c>git submodule update</c> does.</summary>
    public const string Remote = "origin";

    private string Directory => repository.Directory;

    /// <summary>The newest version tag the install may move to (see <see cref="UpdateService.Offers(PluginUpdateTarget, SemVer)"/>).</summary>
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
        (await ListAsync(target, options, ct).ConfigureAwait(false))
        .FirstOrDefault(c => SemVer.TryParse(c.NewVersion, out var version) && UpdateService.Offers(target, version));

    /// <summary>One candidate per version tag of the remote, newest first; nothing is fetched.</summary>
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        Require();
        await GitSource.EnsureVersion(git, Directory, ct).ConfigureAwait(false);
        return GitSource.VersionTags(await RemoteTags(ct).ConfigureAwait(false))
            .Select(t => new UpdateCandidate(target.Slug, target.Name, target.InstalledVersion, t.Version.ToString(), repository.SubmoduleSite, null, t.Commit,
                new SubmodulePackageRef(t.Commit, t.Tag)))
            .ToArray();
    }

    /// <summary>
    /// Fetches the tag's commit into the submodule and writes its files to <paramref name="destination"/>, so the update
    /// is validated like a package; the submodule's working copy is not touched. The problems refuse the update: changes
    /// that are not committed, commits that are not on the remote (a checkout away from them could lose them), and
    /// submodules of its own, which the checkout would not update.
    /// </summary>
    public async Task<StagedSubmodule> StageAsync(SubmodulePackageRef package, string destination, CancellationToken ct)
    {
        Require();
        if (!GitSource.IsCommit(package.Commit) || package.Tag.Length == 0 || package.Tag.StartsWith('-') || package.Tag.Any(char.IsControl))
            throw new InvalidDataException("Invalid submodule version.");
        await GitSource.EnsureVersion(git, Directory, ct).ConfigureAwait(false);
        var problems = new List<Finding>();
        var head = (await Checked(["rev-parse", "--verify", "HEAD^{commit}"], ct).ConfigureAwait(false)).Output.Trim();
        var changes = Changes((await Checked(StatusArguments, ct).ConfigureAwait(false)).Output);
        if (changes.Count > 0) problems.Add(LocalChanges(Directory, changes));
        // The remote's branches as they are now, so a commit pushed since the last fetch counts as published. Tags are
        // left alone: a local tag that differs from the remote's would fail the fetch.
        await Checked(["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", Remote], ct).ConfigureAwait(false);
        await Checked(["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", Remote, "refs/tags/" + package.Tag], ct).ConfigureAwait(false);
        var fetched = (await Checked(["rev-parse", "--verify", "FETCH_HEAD^{commit}"], ct).ConfigureAwait(false)).Output.Trim();
        if (!fetched.Equals(package.Commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The tag {package.Tag} of the submodule's remote points to another commit than when updates were checked; check for updates again.");
        if (await Unpublished(head, ct).ConfigureAwait(false) is { } commit) problems.Add(UnpublishedCommits(Directory, commit));
        var archive = destination + ".tar";
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await Checked(["archive", "--format=tar", "-o", archive, package.Commit], ct).ConfigureAwait(false);
            if (SubmoduleFiles.Extract(archive, destination, ct))
                problems.Add(new("R30", FindingSeverity.Error, $"Version {package.Tag} of the submodule {Directory} has submodules of its own, which ePlugin " +
                                                              "does not update. Update it with git (git checkout and git submodule update --recursive)."));
        }
        finally { AddonFiles.DeleteFile(archive); }
        return new(head, problems);
    }

    /// <summary>
    /// The first commit HEAD reaches that is on no branch of the remote and no tag the remote has as well; null when there
    /// is none. Those are the commits a checkout of another commit could lose.
    /// </summary>
    private async Task<string?> Unpublished(string head, CancellationToken ct)
    {
        var remote = await RemoteTags(ct).ConfigureAwait(false);
        var local = (await Checked(["for-each-ref", "--format=%(refname)%09%(objectname)%09%(*objectname)", "refs/tags"], ct).ConfigureAwait(false)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t')).Where(p => p.Length >= 2);
        // A local tag counts only when the remote has it for the same commit, i.e. it was fetched rather than made here.
        var published = local.Select(p => (Name: p[0], Commit: p.Length > 2 && p[2].Length > 0 ? p[2] : p[1]))
            .Where(t => t.Name.StartsWith("refs/tags/", StringComparison.Ordinal) &&
                        GitSource.Tagged(remote, t.Name[10..]) is { } on && on.Equals(t.Commit, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Commit).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Every tag passed as an argument: a long list falls back to all local tags, which only a tag made here and never
        // pushed makes too lenient.
        string[] exclude = published.Length > 300 ? ["--tags"] : published;
        var output = (await Checked(["rev-list", "--max-count=1", head, "--not", "--remotes=" + Remote, .. exclude], ct).ConfigureAwait(false)).Output.Trim();
        return output.Length == 0 ? null : output;
    }

    private async Task<string[][]> RemoteTags(CancellationToken ct) =>
        GitSource.ParseRefs((await Checked(["ls-remote", "--tags", "--", Remote], ct).ConfigureAwait(false)).Output);

    private Task<GitOutcome> Checked(string[] arguments, CancellationToken ct) => GitSource.Checked(git, Directory, arguments, ct);

    private void Require()
    {
        if (repository.Kind != RepositoryKind.Submodule) throw new InvalidOperationException($"{Directory} is not a git submodule.");
    }

    /// <summary>Lists the tracked files that differ from HEAD; untracked ones, such as Godot's .uid and .import files, do not count.</summary>
    internal static readonly string[] StatusArguments = ["status", "--porcelain=v1", "--untracked-files=no"];

    /// <summary>The paths of <c>git status --porcelain</c> output.</summary>
    internal static IReadOnlyList<string> Changes(string status) =>
        status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3).Select(l => l[3..]).ToArray();

    internal static Finding LocalChanges(string directory, IReadOnlyList<string> changes) =>
        new("R30", FindingSeverity.Error, $"The submodule {directory} has changes that are not committed ({string.Join(", ", changes.Take(5))}" +
                                          $"{(changes.Count > 5 ? ", ..." : "")}). Commit and push them, or discard them, then update again.");

    internal static Finding UnpublishedCommits(string directory, string commit) =>
        new("R30", FindingSeverity.Error, $"The submodule {directory} has commits that are not on its remote (such as {Short(commit)}), which checking out " +
                                          "another version could lose. Push them, or check out a commit of the remote, then update again.");

    internal static string Short(string commit) => commit[..Math.Min(commit.Length, 10)];
}

/// <summary>The git work of a submodule update on the editor thread, where the update transaction runs: local commands only.</summary>
internal static class SubmoduleGit
{
    public static string Head(IGitRunner git, string directory) => Checked(git, directory, "rev-parse", "--verify", "HEAD^{commit}").Trim();

    public static IReadOnlyList<string> Changes(IGitRunner git, string directory) =>
        SubmoduleSource.Changes(Checked(git, directory, SubmoduleSource.StatusArguments));

    /// <summary>Checks out <paramref name="commit"/> as a detached HEAD, as submodules are; fails on conflicting local changes.</summary>
    public static void Checkout(IGitRunner git, string directory, string commit) =>
        Checked(git, directory, "checkout", "--quiet", "--no-recurse-submodules", "--detach", commit);

    /// <summary>Writes the files of <paramref name="commit"/> to <paramref name="destination"/> without touching the working copy.</summary>
    public static void Export(IGitRunner git, string directory, string commit, string destination)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var archive = destination + ".tar";
        try
        {
            Checked(git, directory, "archive", "--format=tar", "-o", archive, commit);
            SubmoduleFiles.Extract(archive, destination, CancellationToken.None);
        }
        finally { AddonFiles.DeleteFile(archive); }
    }

    public static string Checked(IGitRunner git, string directory, params string[] arguments)
    {
        // Off the calling thread, so no synchronization context waits for itself; git itself runs as a process anyway.
        var result = Task.Run(() => git.RunAsync(directory, arguments, CancellationToken.None)).GetAwaiter().GetResult();
        if (result.ExitCode != 0) throw new IOException($"git {GitRunner.Command(arguments)} failed in {directory}: {UpdateErrors.GitError(result.Error).TrimEnd('.')}.");
        return result.Output;
    }
}

/// <summary>Extracts the files of a <c>git archive</c> like a package: bounded, inside the destination, links left out.</summary>
internal static class SubmoduleFiles
{
    /// <summary>Extracts <paramref name="archive"/>; returns whether it has a <c>.gitmodules</c>, which is left out.</summary>
    public static bool Extract(string archive, string destination, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(destination);
        var submodules = false;
        long bytes = 0;
        var count = 0;
        using var stream = File.OpenRead(archive);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            // Folders are created for their files; links are left out like in packages, and git writes nothing else.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var path = PackageFiles.Normalize(entry.Name);
            if (path.Split('/')[^1].Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)) { submodules = true; continue; }
            if (PackageFiles.Skip(path)) continue;
            if (++count > PackageFiles.MaximumFiles || entry.Length > PackageFiles.MaximumFile || (bytes += entry.Length) > PackageFiles.MaximumTotal)
                throw new IOException("Package exceeds file or size limits.");
            var target = PackageFiles.Inside(destination, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
        return submodules;
    }
}
#endif
