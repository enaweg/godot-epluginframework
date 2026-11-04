#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Plugin folders under git version control: how they are told apart, and that no package update replaces one, which
/// would delete the repository in it (packages never contain a .git).
/// </summary>
[TestSuite]
public class PluginRepositoryTests
{
    private string _root = null!;
    private string Project => Path.Combine(_root, "project");
    private string Plugin => Path.Combine(Project, "addons", "plugin");

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "plugin-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Plugin);
        File.WriteAllText(Path.Combine(Plugin, "plugin.cfg"), GitFixture.Config("1.0.0"));
    }

    [AfterTest] public void Cleanup() => AddonFiles.DeleteTree(_root);

    [TestCase]
    public static void ASubmoduleIsToldApartFromAClone()
    {
        using var git = GitFixture.Create();
        var submodule = PluginRepository.Detect(git.Submodule, git.Project);
        Assertions.AssertObject(submodule.Kind).IsEqual(RepositoryKind.Submodule);
        Assertions.AssertString(submodule.Repository).IsEqual(git.Project);
        // the submodule's origin, which is what git fetches from
        Assertions.AssertString(submodule.RemoteUrl).IsEqual(git.Remote);
        Assertions.AssertString(submodule.SubmoduleSite).IsEqual(git.Remote);
        Assertions.AssertBool(File.Exists(Path.Combine(git.Submodule, ".git"))).IsTrue();

        // a clone in the project and a worktree of another repository have a .git of their own, but are no submodules
        var clone = Path.Combine(git.Project, "addons", "clone");
        GitFixture.Git(git.Project, "clone", "-q", git.Remote, "addons/clone");
        Assertions.AssertObject(PluginRepository.Detect(clone, git.Project).Kind).IsEqual(RepositoryKind.Clone);
        var worktree = Path.Combine(git.Project, "addons", "worktree");
        GitFixture.Git(git.Author, "worktree", "add", "-q", "--detach", worktree, "v1.0.0");
        Assertions.AssertBool(File.Exists(Path.Combine(worktree, ".git"))).IsTrue();
        var detected = PluginRepository.Detect(worktree, git.Project);
        Assertions.AssertObject(detected.Kind).IsEqual(RepositoryKind.Clone);
        Assertions.AssertString(detected.PackageRefusal("res://addons/worktree")).Contains("Update it with your version control");
    }

    [TestCase]
    public void TheProjectsOwnRepositoryLeavesItsPluginsPlain()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Assertions.AssertObject(PluginRepository.Detect(Plugin, Project).Kind).IsEqual(RepositoryKind.None);
        Assertions.AssertString(PluginRepository.Detect(Plugin, Project).PackageRefusal("x")).IsNull();
        // also when the project is a folder of a larger repository
        AddonFiles.DeleteTree(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Assertions.AssertObject(PluginRepository.Detect(Plugin, Project).Kind).IsEqual(RepositoryKind.None);
    }

    [TestCase]
    public void AFolderInsideOrHoldingAnotherRepositoryIsRecognized()
    {
        // addons/ is a repository of its own, e.g. a submodule holding several plugins
        File.WriteAllText(Path.Combine(Project, "addons", ".git"), "gitdir: ../.git/modules/addons\n");
        var inside = PluginRepository.Detect(Plugin, Project);
        Assertions.AssertObject(inside.Kind).IsEqual(RepositoryKind.InsideRepository);
        Assertions.AssertString(inside.Repository).IsEqual(Path.Combine(Project, "addons"));
        // without the project root, nothing around the folder is looked at
        Assertions.AssertObject(PluginRepository.Detect(Plugin, null).Kind).IsEqual(RepositoryKind.None);

        File.Delete(Path.Combine(Project, "addons", ".git"));
        Directory.CreateDirectory(Path.Combine(Plugin, "thirdparty", "lib", ".git"));
        var holding = PluginRepository.Detect(Plugin, Project);
        Assertions.AssertObject(holding.Kind).IsEqual(RepositoryKind.ContainsRepository);
        Assertions.AssertString(holding.Repository).IsEqual(Path.Combine(Plugin, "thirdparty", "lib", ".git"));
        Assertions.AssertBool(PluginRepository.HoldsRepository(Plugin)).IsTrue();
    }

    [TestCase]
    public void ASubmoduleIsReadFromGitmodulesAlsoWithAGitFolder()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Path.Combine(Plugin, ".git"));
        File.WriteAllText(Path.Combine(Project, ".gitmodules"),
            "# registered by git submodule add\n[submodule \"addons/plugin\"]\n\tpath = addons/plugin\n\turl = \"https://example.org/plugin.git\" ; comment\n" +
            "[submodule \"other\"]\n\tpath = addons/other\n\turl = ../other.git\n");
        var detected = PluginRepository.Detect(Plugin, Project);
        Assertions.AssertObject(detected.Kind).IsEqual(RepositoryKind.Submodule);
        // without an origin in its own config, the URL .gitmodules records
        Assertions.AssertString(detected.RemoteUrl).IsEqual("https://example.org/plugin.git");
        Assertions.AssertInt(PluginRepository.Submodules(Project).Count).IsEqual(2);

        // a .gitmodules entry for another folder makes it a clone
        File.WriteAllText(Path.Combine(Project, ".gitmodules"), "[submodule \"x\"]\n\tpath = addons/x\n");
        Assertions.AssertObject(PluginRepository.Detect(Plugin, Project).Kind).IsEqual(RepositoryKind.Clone);
    }

    [TestCase]
    public void NoSwapReplacesAFolderThatHoldsARepository()
    {
        foreach (var swap in new IAddonSwap[] { AddonFolderSwap.Instance, AddonFileSwap.Instance })
        {
            foreach (var git in new[] { Path.Combine(Plugin, ".git"), Path.Combine(Plugin, "lib", ".git") })
            {
                Directory.CreateDirectory(git);
                File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
                var transaction = Path.Combine(_root, "transaction");
                var staging = Path.Combine(transaction, "staging", "plugin");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "plugin.cfg"), GitFixture.Config("2.0.0"));

                Assertions.AssertThrown(() => swap.Install(Plugin, staging, Path.Combine(transaction, "backup", "plugin"), Path.Combine(transaction, "discarded", "plugin")))
                    .IsInstanceOf<IOException>().HasMessage($"{Plugin} holds the git repository {git}, which replacing its files would delete; update it with git.");
                Assertions.AssertBool(File.Exists(Path.Combine(git, "HEAD"))).IsTrue();
                Assertions.AssertBool(File.ReadAllText(Path.Combine(Plugin, "plugin.cfg")).Contains("1.0.0")).IsTrue();
                Assertions.AssertBool(Directory.Exists(Path.Combine(transaction, "backup", "plugin"))).IsFalse();
                // a rollback would delete or empty the folder just the same
                Assertions.AssertThrown(() => swap.Restore(Plugin, staging, Path.Combine(transaction, "discarded", "plugin"))).IsInstanceOf<IOException>();
                Assertions.AssertBool(File.Exists(Path.Combine(git, "HEAD"))).IsTrue();

                AddonFiles.DeleteTree(git);
                AddonFiles.DeleteTree(transaction);
            }
        }
    }

    [TestCase]
    public void ValidationRefusesAPackageForAFolderUnderVersionControl()
    {
        Directory.CreateDirectory(Path.Combine(Plugin, ".git"));
        var stage = Path.Combine(_root, "staging", "plugin");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), GitFixture.Config("2.0.0"));
        File.WriteAllText(Path.Combine(stage, "plugin.gd"), "");
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));

        // without a detected repository, validation looks at the folder itself
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", Plugin);
        var validated = new AddonPackageValidator().Validate(new(target, candidate, stage));
        Assertions.AssertBool(validated.IsValid).IsFalse();
        Assertions.AssertString(validated.Findings.Single(f => f.Code == "R29").Message).Contains("is a git working copy with its own repository");

        AddonFiles.DeleteTree(Path.Combine(Plugin, ".git"));
        var inside = target with { Repository = new(RepositoryKind.InsideRepository, Plugin, Path.Combine(Project, "addons")) };
        Assertions.AssertBool(new AddonPackageValidator().Validate(new(inside, candidate, stage)).Findings.Any(f => f.Code == "R29" && f.Severity == FindingSeverity.Error)).IsTrue();
        Assertions.AssertBool(new AddonPackageValidator().Validate(new(target, candidate, stage)).Findings.Any(f => f.Code == "R29")).IsFalse();
    }

    [TestCase]
    public void TheApplierRefusesAPackageForARepositoryBeforeItsMarker()
    {
        File.WriteAllText(Path.Combine(Plugin, ".git"), "gitdir: /elsewhere\n");
        var store = new PluginStateStore(Path.Combine(Project, "addons/eplugin-state.json"), new NullLogger());
        store.Load();
        store.TryCreateBaseline([new("plugin", "1.0.0", PersistedPluginState.Activated)]);
        var directory = Path.Combine(Project, ".godot/eplugin/updates/test-id");
        var stage = Path.Combine(directory, "staging/plugin");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.cfg"), GitFixture.Config("2.0.0"));
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
        var applier = new UpdateApplier(Project, store, new SubmoduleHost(store), new MemoryCache());

        Assertions.AssertThrown(() => applier.Apply([new(candidate, stage, version, null, false, [])], directory))
            .IsInstanceOf<InvalidOperationException>();
        Assertions.AssertInt(store.LocalAttempts.Count).IsEqual(0);
        Assertions.AssertBool(File.Exists(Path.Combine(Plugin, ".git"))).IsTrue();
        Assertions.AssertBool(File.Exists(Path.Combine(directory, "journal.json"))).IsFalse();
    }

    [TestCase]
    public void EarlyRecoveryLeavesAFrameworkFolderWithARepositoryAlone()
    {
        var framework = Path.Combine(Project, "addons", "ePlugin");
        Directory.CreateDirectory(Path.Combine(framework, ".git"));
        File.WriteAllText(Path.Combine(framework, "plugin.cfg"), "[plugin]\nversion=\"2.0.0\"");
        var folder = Path.Combine(Project, ".godot/eplugin/updates/test");
        Directory.CreateDirectory(Path.Combine(folder, "backup/ePlugin"));
        File.WriteAllText(Path.Combine(folder, "backup/ePlugin/plugin.cfg"), "[plugin]\nversion=\"1.0.0\"");
        var store = new PluginStateStore(Path.Combine(Project, "addons/eplugin-state.json"), new NullLogger());
        store.Load();
        store.TryCreateBaseline([new("ePlugin", "1.0.0", PersistedPluginState.Activated)]);
        store.TryBeginAttempt("ePlugin", "1.0.0", PersistedPluginState.Activated, out var id);
        new UpdateJournal { Id = "test", Directory = folder, AttemptId = id, State = UpdatePhase.InterimBuilt, StartCount = 1,
            Plugins = [new() { Slug = "ePlugin", OldVersion = "1.0.0", NewVersion = "2.0.0", SwapStarted = true }] }.Save();

        Assertions.AssertObject(UpdateRecovery.RunIfNeeded(Project, new NullLogger(), () => true, () => { })).IsEqual(UpdateRecovery.Outcome.ManualRepair);
        Assertions.AssertBool(Directory.Exists(Path.Combine(framework, ".git"))).IsTrue();
        Assertions.AssertBool(File.ReadAllText(Path.Combine(framework, "plugin.cfg")).Contains("2.0.0")).IsTrue();
    }

    [TestCase]
    public static void TheManagerOffersNoPackageForAFolderUnderVersionControl()
    {
        var candidate = new UpdateCandidate("plugin", "Plugin", "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", "plugin"));
        var clone = new PluginRepository(RepositoryKind.Clone, "/project/addons/plugin", "/project/addons/plugin");
        var target = new PluginUpdateTarget("plugin", "Plugin", "1.0.0", "https://example.org/releases", "/project/addons/plugin", Repository: clone);
        var model = new PluginManagerViewModel([], [candidate], [target], null, _ => []);
        Assertions.AssertBool(model.Updates.Single().HasError).IsTrue();
        Assertions.AssertInt(model.Installable.Count).IsEqual(0);
        Assertions.AssertString(PluginManagerViewModel.RepositoryLine(clone)).Contains("git working copy");
        Assertions.AssertString(PluginManagerViewModel.RepositoryLine(new(RepositoryKind.None, "/x"))).IsEmpty();
        // local packages of it are not listed either
        var index = new LocalPackageIndex([new LocalPackage("/zips/plugin.zip", "plugin", "plugin", "2.0.0", DateTime.UtcNow)], [], 1, []);
        Assertions.AssertInt(LocalDirectorySource.Versions(index, target).Count).IsEqual(0);
        Assertions.AssertInt(LocalDirectorySource.Versions(index, target with { Repository = null }).Count).IsEqual(1);
    }

    private sealed class MemoryCache : IUpdateStateStore { public UpdateCache State { get; } = new(); public void Save() { } }

    /// <summary>An editor that accepts every step; builds pass unless queued otherwise.</summary>
    internal sealed class SubmoduleHost(PluginStateStore store) : IUpdateHost
    {
        public Queue<BuildOutcome> Builds { get; } = new();
        public List<string> Logs { get; } = [];
        public bool Managed { get; set; }
        public bool Verified { get; set; } = true;
        public int Reloads { get; private set; }
        public bool UiAvailable => false;
        public string BuildFailurePolicy => "rollback";
        public bool RestartAlways => false;
        public bool IsEnabled(string slug) => true;
        public bool IsManaged(string slug) => Managed;
        public void Preflight(IReadOnlyList<ValidatedPackage> packages) { }
        public void PrepareJournal(UpdateJournal journal) { }
        public void Bridge(UpdateJournal journal, UpdatePluginJournal plugin) { }
        public void Reconcile(UpdateJournal journal, bool rollback) { }
        public void SetPlainEnabled(string slug, bool enabled) => Assertions.AssertBool(store.IsBlocked(slug)).IsTrue();
        public void CloseScenes() { }
        public void Scan() { }
        public BuildOutcome Build() => Builds.Count > 0 ? Builds.Dequeue() : new(0, []);
        public void RequestReload(UpdateJournal journal) => Reloads++;
        public bool Verify(UpdatePluginJournal plugin) => Verified;
        public void Log(string message) => Logs.Add(message);
    }
}
#endif
