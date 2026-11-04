#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Plugins whose folder is a git submodule: checked against the version tags of a local bare remote, updated by checking
/// out a tag in the submodule of a local project repository, rolled back and recovered by checking out the old commit.
/// </summary>
[TestSuite]
public class SubmoduleUpdateTests
{
    private static readonly GitRunner Git = new(allowFileProtocol: true);

    [TestCase]
    public static async Task ChecksOfferTheNewestVersionTagOfTheSubmodulesRemote()
    {
        using var git = GitFixture.Create();
        // plugin.cfg's update_url, a project site and the built-in list play no part: the remote is the only site
        var target = git.Target() with { UpdateUrl = "https://example.org/plugin/releases", OverrideUrl = "https://example.org/site/releases", KnownUrl = "https://example.org/known/releases" };
        Assertions.AssertArray(target.UpdateUrls.ToArray()).IsEqual(new[] { git.Remote });
        Assertions.AssertBool(target.PackagesAllowed).IsFalse();
        var service = new UpdateService(new UpdateSourceFactory(git: Git), new SystemClock(), new MemoryCache());

        var result = await service.CheckAsync([target], new(), CancellationToken.None);

        Assertions.AssertInt(result.Failures.Count).IsEqual(0);
        var update = result.Updates.Single();
        Assertions.AssertBool(SemVer.Same(update.NewVersion, "2.0.0")).IsTrue();
        Assertions.AssertString(update.SourceUrl).IsEqual(git.Remote);
        Assertions.AssertObject(update.Package).IsEqual(new SubmodulePackageRef(git.Commits["2.0.0"], "v2.0.0"));
        // every tag is a version to choose from in the details
        var versions = await service.ListVersionsAsync(target, new(), CancellationToken.None);
        Assertions.AssertInt(versions.Count).IsEqual(3);
        Assertions.AssertString(((SubmodulePackageRef)versions[^1].Package).Tag).IsEqual("v1.0.0");
        // the submodule itself was not changed by checking
        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(git.Commits["1.0.0"]);
    }

    [TestCase]
    public static async Task AnUpdateChecksOutTheTagInTheSubmoduleAndKeepsItsRepository()
    {
        using var git = GitFixture.Create();
        // Godot's own files in the plugin folder are untracked and stay
        File.WriteAllText(Path.Combine(git.Submodule, "plugin.gd.uid"), "uid://test");
        var (store, applier, host) = Transaction(git);
        var (package, directory) = await Stage(git, "2.0.0");

        Assertions.AssertBool(package.IsValid).IsTrue();
        Assertions.AssertString(package.Findings.Single(f => f.Code == "R29").Message).Contains("Commit the changed submodule");
        Assertions.AssertString(package.SubmoduleHead).IsEqual(git.Commits["1.0.0"]);
        // staging leaves the working copy as it is
        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(git.Commits["1.0.0"]);

        Assertions.AssertObject(applier.Apply([package], directory)).IsEqual(UpdateOutcome.Completed);

        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(git.Commits["2.0.0"]);
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, ".git"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, "plugin.gd.uid"))).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(git.Submodule, "plugin.cfg")).Contains("2.0.0")).IsTrue();
        Assertions.AssertObject(PluginRepository.Detect(git.Submodule, git.Project).Kind).IsEqual(RepositoryKind.Submodule);
        Assertions.AssertString(store.GetShared(git.Slug)!.Version).IsEqual("2.0.0");
        Assertions.AssertInt(store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertBool(Directory.Exists(directory)).IsFalse();
        // the project's repository shows the changed submodule commit for the user to commit
        Assertions.AssertString(GitFixture.Git(git.Project, "diff", "--ignore-submodules=dirty", "--name-only")).IsEqual("addons/" + git.Slug);
        Assertions.AssertBool(host.Logs.Any(l => l.Contains("commit the changed submodule", StringComparison.Ordinal))).IsTrue();
        var summary = PluginManagerViewModel.UpdateSummary([new([package], directory, UpdateOutcome.Completed, null)], false);
        Assertions.AssertString(summary).Contains("Commit the changed submodule of Plugin in your project's repository.");
    }

    [TestCase]
    public static async Task ARollbackChecksOutTheOldCommitAgain()
    {
        using var git = GitFixture.Create();
        var (store, applier, host) = Transaction(git);
        host.Verified = false;
        var (package, directory) = await Stage(git, "2.0.0");

        Assertions.AssertObject(applier.Apply([package], directory)).IsEqual(UpdateOutcome.RolledBack);

        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(git.Commits["1.0.0"]);
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, ".git"))).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(git.Submodule, "plugin.cfg")).Contains("1.0.0")).IsTrue();
        Assertions.AssertString(GitFixture.Git(git.Submodule, "status", "--porcelain", "--ignored")).IsEmpty();
        Assertions.AssertString(GitFixture.Git(git.Project, "diff", "--name-only")).IsEmpty();
        Assertions.AssertInt(store.LocalAttempts.Count).IsEqual(0);
        var journal = UpdateJournal.Load(directory);
        Assertions.AssertObject(journal.State).IsEqual(UpdatePhase.RolledBack);
        Assertions.AssertString(journal.Plugins[0].Submodule!.OldCommit).IsEqual(git.Commits["1.0.0"]);
        // the backup was consumed, so a repeated rollback does not check anything out again
        Assertions.AssertBool(Directory.Exists(journal.Backup(git.Slug))).IsFalse();
    }

    [TestCase]
    public static async Task ASubmoduleWithUncommittedChangesOrUnpushedCommitsIsNotUpdated()
    {
        using var git = GitFixture.Create();
        File.AppendAllText(Path.Combine(git.Submodule, "plugin.gd"), "# local change\n");
        Assertions.AssertString(await Refusal(git)).Contains("has changes that are not committed (plugin.gd)");

        // committed on the detached HEAD, but never pushed: checking out another commit would leave it behind
        GitFixture.Git(git.Submodule, "commit", "-q", "-am", "local fix");
        Assertions.AssertString(await Refusal(git)).Contains("has commits that are not on its remote");
        // a tag made here does not publish it either
        GitFixture.Git(git.Submodule, "tag", "v1.0.1");
        Assertions.AssertString(await Refusal(git)).Contains("has commits that are not on its remote");
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, ".git"))).IsTrue();
    }

    [TestCase]
    public static async Task ASubmoduleAtAPublishedTagOnNoBranchIsUpdated()
    {
        using var git = GitFixture.Create();
        // a release tagged on a commit that no branch of the remote holds
        GitFixture.Git(git.Author, "checkout", "-q", "--detach");
        var hotfix = git.Release("1.0.5", "hotfix.txt");
        GitFixture.Git(git.Author, "push", "-q", "origin", "v1.0.5");
        GitFixture.Git(git.Submodule, "fetch", "-q", "origin", "tag", "v1.0.5");
        GitFixture.Git(git.Submodule, "checkout", "-q", "--detach", "v1.0.5");
        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(hotfix);

        var (package, _) = await Stage(git, "2.0.0", installed: "1.0.5");
        Assertions.AssertBool(package.IsValid).IsTrue();
    }

    [TestCase]
    public static async Task AnUpdateDoesNotStartFromAnotherCommitThanStagingFound()
    {
        using var git = GitFixture.Create();
        var (store, applier, _) = Transaction(git);
        var (package, directory) = await Stage(git, "2.0.0");
        GitFixture.Git(git.Submodule, "checkout", "-q", "--detach", "v1.1.0");
        File.WriteAllText(Path.Combine(git.Submodule, "plugin.cfg"), GitFixture.Config("1.0.0"));
        GitFixture.Git(git.Submodule, "commit", "-q", "-am", "same version");

        Assertions.AssertThrown(() => applier.Apply([package], directory)).IsInstanceOf<InvalidOperationException>();
        Assertions.AssertInt(store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertBool(File.Exists(Path.Combine(directory, "journal.json"))).IsFalse();
    }

    [TestCase]
    public static async Task APackageOfAnotherSourceIsNotInstalledOverASubmodule()
    {
        using var git = GitFixture.Create();
        var stage = Path.Combine(git.Root, "staging", git.Slug);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), GitFixture.Config("2.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.gd"), "");
        var zip = new UpdateCandidate(git.Slug, "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", git.Slug));
        var validated = new AddonPackageValidator().Validate(new(git.Target(), zip, stage));
        Assertions.AssertBool(validated.IsValid).IsFalse();
        Assertions.AssertString(validated.Findings.Single(f => f.Code == "R29").Message).Contains("is a git submodule");
        // the manager lists such an update, e.g. one cached before, with the reason
        var model = new PluginManagerViewModel([], [zip], [git.Target()], null, _ => []);
        Assertions.AssertBool(model.Updates.Single().HasError).IsTrue();
        await Task.CompletedTask;
    }

    [TestCase]
    public static void EarlyRecoveryChecksOutTheOldCommitOfASelfUpdate()
    {
        using var git = GitFixture.Create("ePlugin");
        var folder = Path.Combine(git.Project, ".godot/eplugin/updates/test");
        var backup = Path.Combine(folder, "backup/ePlugin");
        SubmoduleGit.Export(Git, git.Submodule, git.Commits["1.0.0"], backup);
        GitFixture.Git(git.Submodule, "checkout", "-q", "--detach", "v2.0.0");
        // left by the interim bridge, untracked in the new version
        File.WriteAllText(Path.Combine(git.Submodule, "kept.gd"), "");
        var store = new PluginStateStore(Path.Combine(git.Project, "addons/eplugin-state.json"), new NullLogger());
        store.Load();
        store.TryCreateBaseline([new("ePlugin", "1.0.0", PersistedPluginState.Activated)]);
        store.TryBeginAttempt("ePlugin", "1.0.0", PersistedPluginState.Activated, out var id);
        new UpdateJournal
        {
            Id = "test", Directory = folder, AttemptId = id, State = UpdatePhase.InterimBuilt, StartCount = 1,
            Plugins = [new() { Slug = "ePlugin", OldVersion = "1.0.0", NewVersion = "2.0.0", SwapStarted = true, Preserved = ["kept.gd"],
                Submodule = new() { OldCommit = git.Commits["1.0.0"], NewCommit = git.Commits["2.0.0"], Tag = "v2.0.0" } }]
        }.Save();

        Assertions.AssertObject(UpdateRecovery.RunIfNeeded(git.Project, new NullLogger(), () => true, () => { })).IsEqual(UpdateRecovery.Outcome.Restart);

        Assertions.AssertString(GitFixture.Head(git.Submodule)).IsEqual(git.Commits["1.0.0"]);
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, ".git"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, "kept.gd"))).IsFalse();
        store.Load();
        Assertions.AssertInt(store.LocalAttempts.Count).IsEqual(0);
    }

    [TestCase]
    public static void JournalsKeepTheSubmoduleCommitsAndRefuseInvalidOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "submodule-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var commit = new string('a', 40);
            var journal = new UpdateJournal
            {
                Id = Path.GetFileName(directory), Directory = directory, AttemptId = Guid.NewGuid(),
                Plugins = [new() { Slug = "plugin", OldVersion = "1.0.0", NewVersion = "2.0.0", Submodule = new() { OldCommit = commit, NewCommit = new string('b', 64), Tag = "v2.0.0" } }]
            };
            journal.Save();
            var loaded = UpdateJournal.Load(directory).Plugins[0].Submodule!;
            Assertions.AssertString(loaded.OldCommit).IsEqual(commit);
            Assertions.AssertString(loaded.Tag).IsEqual("v2.0.0");

            journal.Plugins[0].Submodule!.OldCommit = "HEAD~1";
            journal.Save();
            Assertions.AssertThrown(() => UpdateJournal.Load(directory)).IsInstanceOf<InvalidDataException>();
        }
        finally { AddonFiles.DeleteTree(directory); }
    }

    [TestCase]
    public static void TheDetailsNameTheSubmodulesRemote()
    {
        var submodule = new PluginRepository(RepositoryKind.Submodule, "/project/addons/plugin", "/project", "https://example.org/plugin.git");
        Assertions.AssertString(PluginManagerViewModel.RepositoryLine(submodule)).Contains("version tags of its remote https://example.org/plugin.git");
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/plugin.git", null, new string('c', 40),
            new SubmodulePackageRef(new string('c', 40), "v2.0.0"));
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", null, "/project/addons/plugin", Repository: submodule);
        var model = new PluginManagerViewModel([new PluginInfo("plugin", "Plugin", Internal.Kinds.PluginKind.GDScript, true) { Repository = submodule }], [candidate], [target], null, _ => []);
        Assertions.AssertBool(model.Updates.Single().HasError).IsFalse();
        Assertions.AssertBool(model.Plugins.Single().IsUpdatable).IsTrue();
        Assertions.AssertString(PluginManagerViewModel.VersionSource(new VersionOption("2.0.0", candidate, false, false))).Contains("tag v2.0.0, commit cccccccccc");
    }

    private static async Task<string> Refusal(GitFixture git)
    {
        try
        {
            await Stage(git, "2.0.0");
            return "";
        }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    private static async Task<(ValidatedPackage Package, string Directory)> Stage(GitFixture git, string version, string installed = "1.0.0")
    {
        var target = git.Target(installed);
        var candidate = (await new SubmoduleSource(Git, target.Repository!).ListAsync(target, new(), CancellationToken.None))
            .Single(c => SemVer.Same(c.NewVersion, version));
        var directory = Path.Combine(git.Project, ".godot/eplugin/updates", "test-" + Guid.NewGuid().ToString("N")[..8]);
        var staged = await new PackageFetcher(new(), new GitRunner(), Git).FetchAsync([candidate], [target], directory, null, CancellationToken.None);
        return (staged.Packages.Single(), directory);
    }

    private static (PluginStateStore Store, UpdateApplier Applier, PluginRepositoryTests.SubmoduleHost Host) Transaction(GitFixture git)
    {
        var store = new PluginStateStore(Path.Combine(git.Project, "addons/eplugin-state.json"), new NullLogger());
        store.Load();
        store.TryCreateBaseline([new(git.Slug, "1.0.0", PersistedPluginState.Activated)]);
        var host = new PluginRepositoryTests.SubmoduleHost(store);
        return (store, new UpdateApplier(git.Project, store, host, new MemoryCache(), Git), host);
    }

    private sealed class MemoryCache : IUpdateStateStore { public UpdateCache State { get; } = new(); public void Save() { } }
}
#endif
