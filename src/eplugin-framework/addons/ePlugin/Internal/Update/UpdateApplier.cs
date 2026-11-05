#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;

namespace Enaweg.Plugin.Internal.Update;

internal enum UpdateOutcome { Completed, AwaitingReload, AwaitingDecision, KeptWithErrors, RolledBack }
/// <summary>
/// What became of one group of a batch. <paramref name="Outcome"/> is null when the group was refused before its
/// transaction began, so none of its addons changed; <paramref name="Failure"/> then says why.
/// <paramref name="KeptForProjectCode"/> is set when it was kept because only the project's own code failed to build.
/// </summary>
internal sealed record UpdateGroupResult(IReadOnlyList<ValidatedPackage> Packages, string Directory, UpdateOutcome? Outcome, string? Failure,
    bool KeptForProjectCode = false);
internal interface IUpdateHost
{
    bool UiAvailable { get; }
    BuildFailurePolicy BuildFailurePolicy { get; }
    bool RestartAlways { get; }
    bool IsEnabled(string slug);
    bool IsManaged(string slug);
    void Preflight(IReadOnlyList<ValidatedPackage> packages);
    void PrepareJournal(UpdateJournal journal);
    void BeforeSwap(UpdateJournal journal) { }
    void Bridge(UpdateJournal journal, UpdatePluginJournal plugin);
    void Reconcile(UpdateJournal journal, bool rollback);
    /// <summary>Takes the recipes of the plugins a kept update does not change from the project as it is now.</summary>
    void RebaseRecipes(UpdateJournal journal) { }
    void SetPlainEnabled(string slug, bool enabled);
    // Saves and closes every open scene so none keeps nodes whose scripts or resources disappear mid-update.
    void CloseScenes();
    void Scan();
    BuildOutcome Build();
    /// <summary>
    /// Whether an update whose build fails only in project code may be kept once its plugins compile on their own
    /// (<c>eplugin/updates/keep_on_project_errors</c>).
    /// </summary>
    bool KeepOnProjectErrors => false;
    /// <summary>Directories outside <c>addons/</c> whose code is plugin code: link targets, recipe projects.</summary>
    IReadOnlyList<string> PluginCodeRoots(UpdateJournal journal) => [];
    /// <summary>Builds the project with only plugin code, see <see cref="PluginsOnlyBuild"/>.</summary>
    BuildOutcome BuildPluginsOnly(PluginsOnlyBuild build) => new(1, ["A plugins-only build is not available."]);
    void RequestReload(UpdateJournal journal);
    /// <summary>
    /// Reloads the reloadable GDExtension at <paramref name="resourcePath"/> in place (loads it when it is not loaded);
    /// false when Godot could not, so the editor has to restart. Used by <see cref="NativeExtensionWorkflow"/>.
    /// </summary>
    bool ReloadExtension(string resourcePath) => false;
    bool Verify(UpdatePluginJournal plugin);
    void Log(string message);
}
/// <param name="git">Runs git in the plugin folders that are git submodules; only local commands.</param>
internal sealed class UpdateApplier(string projectRoot, PluginStateStore store, IUpdateHost host, IUpdateStateStore cache, IGitRunner? git = null)
{
    private readonly IGitRunner _git = git ?? new GitRunner(allowFileProtocol: true);
    private readonly UpdateBuildGate _builds = new(projectRoot, host);

    private string Installed(string slug) => PackageFiles.Inside(Path.Combine(projectRoot, "addons"), slug);

    /// <summary>
    /// Checks, before the marker, that the package may change the plugin folder: a package never replaces a folder under
    /// git version control (a swap would delete its repository, see <see cref="AddonFiles.RefuseRepository"/>), and a
    /// submodule update needs the folder to still be the submodule staging found, at the same commit and without
    /// uncommitted changes. Returns the commits a submodule update journals; null for a package.
    /// </summary>
    private SubmoduleJournal? Submodule(ValidatedPackage package)
    {
        var installed = Installed(package.Candidate.Slug);
        var repository = PluginRepository.Detect(installed, projectRoot);
        if (package.Candidate.Package is not SubmodulePackageRef submodule)
        {
            if (repository.PackageRefusal(installed) is { } refusal) throw new InvalidOperationException(refusal);
            return null;
        }
        if (repository.Kind != RepositoryKind.Submodule || package.SubmoduleHead is null)
            throw new InvalidOperationException($"{installed} is no longer a git submodule; check for updates again.");
        var head = SubmoduleGit.Head(_git, installed);
        if (!head.Equals(package.SubmoduleHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The submodule {installed} moved to another commit since the update was downloaded; update again.");
        if (SubmoduleGit.Changes(_git, installed) is { Count: > 0 } changes) throw new InvalidOperationException(SubmoduleSource.LocalChanges(installed, changes).Message);
        return new() { OldCommit = head, NewCommit = submodule.Commit, Tag = submodule.Tag };
    }

    /// <summary>How the plugin's files are replaced: a submodule checks out the new commit, anything else uses its kind's swap.</summary>
    private IAddonSwap Swap(PluginKindWorkflow workflow, UpdatePluginJournal plugin) =>
        plugin.Submodule is null ? workflow.Swap(plugin) : new SubmoduleSwap(_git, plugin);

    /// <summary>
    /// The installed version: the plugin.cfg's, or for a GDExtension add-on, which has none, the one the shared state
    /// recorded when ePlugin installed it ("" when it did not).
    /// </summary>
    private string? InstalledVersion(string slug, PluginKind kind) =>
        !PluginKinds.Workflow(kind).HasPluginConfig ? store.GetShared(slug)?.Version ?? ""
        : PluginIni.Parse(File.ReadAllText(Path.Combine(Installed(slug), "plugin.cfg"))).GetValueOrDefault("version");

    /// <summary>The version a package installs: its plugin.cfg's, or the announced one of an add-on without plugin.cfg.</summary>
    internal static string PackageVersion(ValidatedPackage package) =>
        File.Exists(Path.Combine(package.StagingDir, "plugin.cfg"))
            ? PluginIni.Parse(File.ReadAllText(Path.Combine(package.StagingDir, "plugin.cfg"))).GetValueOrDefault("version")!
            : package.NewVersion.ToString();

    /// <summary>
    /// Installs the groups of one staged batch one after another, each as its own transaction: a group that fails is
    /// rolled back alone and the next group is still installed on top of the ones that succeeded. A batch of one group
    /// is applied exactly like <see cref="Apply"/>, including its exceptions.
    /// </summary>
    /// <param name="askOnBuildFailure">Whether a batch of one group may ask to keep a version that does not build.</param>
    public IReadOnlyList<UpdateGroupResult> ApplyGroups(IReadOnlyList<IReadOnlyList<ValidatedPackage>> groups, string transactionDirectory,
        Func<string> newDirectory, bool trustChangedSources = false, Action<int, IReadOnlyList<ValidatedPackage>>? starting = null,
        Action<UpdateGroupResult>? finished = null, bool askOnBuildFailure = false)
    {
        if (groups.Count == 0) throw new InvalidOperationException("No valid distinct addon batch was selected.");
        if (groups.Count == 1)
        {
            starting?.Invoke(0, groups[0]);
            var single = Result(groups[0], transactionDirectory, Apply(groups[0], transactionDirectory, trustChangedSources, askOnBuildFailure: askOnBuildFailure));
            finished?.Invoke(single);
            return [single];
        }
        var results = new List<UpdateGroupResult>();
        var split = SplitStaging(groups, transactionDirectory, newDirectory);
        for (var i = 0; i < split.Count; i++)
        {
            var (packages, directory) = split[i];
            starting?.Invoke(i, packages);
            UpdateGroupResult result;
            try { result = Result(packages, directory, Apply(packages, directory, trustChangedSources, i)); }
            // Once its journal exists the transaction began and its marker blocks the plugins, so it needs recovery.
            catch (Exception ex) { result = new(packages, directory, File.Exists(Path.Combine(directory, "journal.json")) ? UpdateOutcome.KeptWithErrors : null, ex.Message); }
            try
            {
                // A group refused before its marker leaves only its staging behind.
                if (!File.Exists(Path.Combine(directory, "journal.json"))) AddonFiles.DeleteTree(directory);
            }
            catch (Exception ex) { host.Log($"Cannot remove the staging folder {directory}: {ex.Message}"); }
            results.Add(result);
            finished?.Invoke(result);
        }
        return results;
    }

    private static UpdateGroupResult Result(IReadOnlyList<ValidatedPackage> packages, string directory, UpdateOutcome outcome)
    {
        string? failure = null;
        var projectCode = false;
        if (outcome is UpdateOutcome.RolledBack or UpdateOutcome.KeptWithErrors)
        {
            try
            {
                var journal = UpdateJournal.Load(directory);
                failure = journal.Failure;
                projectCode = outcome == UpdateOutcome.KeptWithErrors && journal.KeptForProjectCode;
            }
            // The summary still names the outcome and the log folder; the reason says why it cannot tell more.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                failure = $"why is unknown, as its journal cannot be read ({ex.Message.TrimEnd('.')})";
            }
        }
        return new(packages, directory, outcome, failure, projectCode);
    }

    /// <summary>
    /// Gives every group after the first its own transaction folder, before the first group's commit deletes the batch
    /// folder with everything still staged in it.
    /// </summary>
    private static List<(IReadOnlyList<ValidatedPackage> Packages, string Directory)> SplitStaging(
        IReadOnlyList<IReadOnlyList<ValidatedPackage>> groups, string transactionDirectory, Func<string> newDirectory)
    {
        var split = new List<(IReadOnlyList<ValidatedPackage>, string)> { (groups[0], transactionDirectory) };
        try
        {
            foreach (var group in groups.Skip(1))
            {
                var directory = newDirectory();
                var moved = new List<ValidatedPackage>();
                split.Add((moved, directory));
                foreach (var package in group)
                {
                    var staging = PackageFiles.Inside(Path.Combine(directory, "staging"), package.Candidate.Slug);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                    AddonFiles.MoveDirectory(package.StagingDir, staging);
                    moved.Add(package with { StagingDir = staging });
                }
            }
        }
        catch
        {
            foreach (var (_, directory) in split.Skip(1))
                AddonFiles.DeleteTree(directory);
            throw;
        }
        return split;
    }

    public UpdateOutcome Apply(IReadOnlyList<ValidatedPackage> packages, string transactionDirectory, bool trustChangedSources = false, int? group = null,
        bool askOnBuildFailure = false)
    {
        if (packages.Count == 0 || packages.Any(p => !p.IsValid) || packages.Select(p => p.Candidate.Slug).Distinct().Count() != packages.Count)
            throw new InvalidOperationException("No valid distinct addon batch was selected.");
        if (!trustChangedSources && packages.Any(p => p.Findings.Any(f => f.RequiresTrust))) throw new InvalidOperationException("Confirm trust in the changed update source before installation.");
        if (store.IsReadOnly || packages.Any(p => store.IsBlocked(p.Candidate.Slug))) throw new InvalidOperationException("Resolve local plugin state before updating.");
        host.Preflight(packages);
        var journal = new UpdateJournal { Id = Path.GetFileName(transactionDirectory), Directory = transactionDirectory, Group = group, AskOnBuildFailure = askOnBuildFailure };
        foreach (var package in packages)
        {
            var slug = package.Candidate.Slug;
            if (!host.IsEnabled(slug)) throw new InvalidOperationException($"Plugin {slug} is no longer enabled.");
            var submodule = Submodule(package);
            var old = InstalledVersion(slug, package.Kind);
            // The check read the version through Godot, which reads an unquoted 1.2.0 as 1.2.
            if (!SemVer.Same(old, package.Candidate.InstalledVersion)) throw new InvalidOperationException($"Plugin {slug} changed since the update check; check again.");
            var next = PackageVersion(package);
            var managed = host.IsManaged(slug);
            journal.Plugins.Add(new() { Slug = slug, OldVersion = old!, NewVersion = next, IsEPlugin = managed,
                Kind = PluginKinds.ForJournal(slug, managed, package.Kind).ToString(), WasEnabled = true,
                NativeExtension = PluginKinds.HasNativeExtension(package.StagingDir) || PluginKinds.HasNativeExtension(Installed(slug)), ContainsCSharp = package.ContainsCSharp, SourceUrl = package.Candidate.Origin, Revision = package.Candidate.ResolvedRevision,
                Uids = UidMap.Collect(Installed(slug)), AcceptedLicense = package.AcceptedLicense, Submodule = submodule });
        }
        host.PrepareJournal(journal);
        ProjectBackup.Write(projectRoot, journal);
        if (!store.TryBeginAttempt(journal.Plugins[0].Slug, journal.Plugins[0].OldVersion, PersistedPluginState.Activated, out var attempt))
            throw new IOException("Cannot write the update marker; addon files were not touched.");
        journal.AttemptId = attempt;
        try
        {
            foreach (var plugin in journal.Plugins.Skip(1))
                if (!store.TryAddParticipant(attempt, plugin.Slug, plugin.OldVersion, PersistedPluginState.Activated)) throw new IOException("Cannot record all update participants.");
            journal.Save(UpdatePhase.Staged);
        }
        catch { store.TryAbandonAttempt(attempt); throw; }
        try
        {
            File.WriteAllText(Path.Combine(transactionDirectory, "README.txt"), "Manual restore: close the editor, move backup/<slug> to addons/<slug>, restore backup-project files if necessary, delete .godot/mono/temp and rebuild. Do not remove the local plugin-state marker until recovery succeeds.\n");
        }
        // Only notes for a manual repair; the transaction holds the marker already and continues without them.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.Log("Cannot write the manual restore notes: " + ex.Message); }
        try
        {
            host.CloseScenes();
            host.BeforeSwap(journal);
            journal.Save(UpdatePhase.Swapped);
            foreach (var plugin in journal.Plugins)
            {
                // Each kind replaces its files its own way; see the PluginKindWorkflow implementations.
                var workflow = PluginKinds.Workflow(plugin);
                workflow.BeforeSwap(plugin);
                plugin.SwapStarted = true;
                journal.Save();
                var swap = Swap(workflow, plugin);
                host.Log($"{workflow.Name} {plugin.Slug}: installing {plugin.NewVersion} ({swap.Name}).");
                if (workflow.DisabledDuringSwap) host.SetPlainEnabled(plugin.Slug, false);
                var discarded = swap.Install(Installed(plugin.Slug), journal.Staging(plugin.Slug), journal.Backup(plugin.Slug), journal.Discarded(plugin.Slug));
                UidMap.Restore(Installed(plugin.Slug), plugin.Uids);
                host.Bridge(journal, plugin);
                workflow.AfterSwap(new(host, journal, plugin, Installed(plugin.Slug), discarded));
                journal.Save();
            }
            host.Scan();
            if (journal.Plugins.Any(p => p.ContainsCSharp) || host.RestartAlways)
            {
                var managed = journal.Plugins.Any(p => p.IsEPlugin);
                if (!_builds.Passes(journal, managed ? "interim" : "final"))
                    return BuildFailed(journal, "Build failed.", beforeReconcile: true,
                        (managed ? "Interim build failed" : "Plain C# addon build failed") + "; the assembly cannot reload until the project compiles.");
                journal.Save(UpdatePhase.InterimBuilt);
                RestorePlainPlugins(journal);
                host.RequestReload(journal);
                return UpdateOutcome.AwaitingReload;
            }
            RestorePlainPlugins(journal);
            journal.Save(UpdatePhase.InterimBuilt);
            return Resume(journal);
        }
        catch (Exception ex) { return Rollback(journal, ex.Message); }
    }

    public UpdateOutcome Resume(UpdateJournal journal)
    {
        try
        {
            journal.Save(UpdatePhase.Reconciling);
            host.Reconcile(journal, false);
            journal.Save(UpdatePhase.Reconciled);
            RestorePlainPlugins(journal, onlyDisabled: true);
            host.Scan();
            // A plain C# batch already passed its single build gate. Managed recipes need a final build, unless reconciling
            // left the build inputs of the interim build the editor runs unchanged.
            var managedCSharp = journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp);
            var finalBuild = managedCSharp && (host.RestartAlways || !RunsInterimBuild(journal) || ChangedSinceInterimBuild(journal));
            if (managedCSharp && !finalBuild) host.Log("The updated recipes do not change the build; the editor keeps the interim build without another restart.");
            if (finalBuild && !_builds.Passes(journal, "final")) return BuildFailed(journal, "Final build failed.", beforeReconcile: false, "Final build failed.");
            if (journal.Plugins.Any(p => !host.Verify(p))) return Rollback(journal, "Updated plugin verification failed.");
            if (journal.Plugins.Any(p => p.Slug == Framework.Slug)) File.WriteAllText(Path.Combine(journal.Directory, "healthy.marker"), "verified");
            journal.Save(UpdatePhase.Verified);
            return Commit(journal);
        }
        catch (Exception ex) { return Rollback(journal, ex.Message); }
    }

    public UpdateOutcome Commit(UpdateJournal journal)
    {
        var versions = new Dictionary<string, string>(journal.AdditionalVersions, StringComparer.Ordinal);
        foreach (var plugin in journal.Plugins) versions[plugin.Slug] = plugin.NewVersion;
        if (!store.TryAcknowledgeVersions(journal.AttemptId, versions))
        {
            store.TryFail(journal.AttemptId, PersistedPluginState.Failed, AttemptReasons.UpdateCommitFailed);
            journal.Failure = "Healthy update could not be acknowledged; merge the shared state file and use Retry under Needs attention in the ePlugin Manager.";
            journal.Save(UpdatePhase.Verified);
            host.Log(journal.Failure);
            return UpdateOutcome.KeptWithErrors;
        }
        return FinishCommit(journal);
    }

    /// <summary>
    /// Whether the shared state already holds every version of <paramref name="journal"/>, i.e. its attempt was
    /// acknowledged although the journal was not saved as committed (e.g. the editor stopped right after it).
    /// </summary>
    public bool IsAcknowledged(UpdateJournal journal) =>
        journal.Plugins.All(p => store.GetShared(p.Slug)?.Version == p.NewVersion) &&
        journal.AdditionalVersions.All(v => store.GetShared(v.Key)?.Version == v.Value);

    /// <summary>The part of <see cref="Commit"/> after the acknowledgement; it never rolls the update back.</summary>
    public UpdateOutcome FinishCommit(UpdateJournal journal)
    {
        RebasePending(journal, true);
        // Updates never show a welcome page, also not one the plugin did not have or show before.
        var unseen = journal.Plugins.Where(p => !store.IsWelcomeShown(p.Slug)).Select(p => new ShownWelcome(p.Slug, p.NewVersion, DateTimeOffset.UtcNow)).ToArray();
        if (unseen.Length > 0 && !store.TryRecordWelcomes(unseen)) host.Log("The updated plugins' welcome pages could not be marked as shown; they may be shown after the editor restarts.");
        var licenses = journal.Plugins.Where(p => p.AcceptedLicense is not null)
            .Select(p => new AcceptedLicense(p.Slug, p.AcceptedLicense!, p.NewVersion, DateTimeOffset.UtcNow, false)).ToArray();
        if (licenses.Length > 0 && !store.TryRecordLicenses(licenses)) host.Log("The accepted licenses of the updated plugins could not be saved; they are asked for again after the editor restarts.");
        foreach (var plugin in journal.Plugins)
        {
            host.Log(plugin.OldVersion == plugin.NewVersion ? $"Reinstalled {plugin.Slug} {plugin.NewVersion}."
                : plugin.OldVersion.Length == 0 ? $"Installed {plugin.Slug} {plugin.NewVersion} over a version ePlugin did not know."
                : $"Updated {plugin.Slug} {plugin.OldVersion} -> {plugin.NewVersion}.");
            if (plugin.Submodule is { } submodule)
                host.Log($"The submodule {Installed(plugin.Slug)} is now at {submodule.Tag} ({SubmoduleSource.Short(submodule.NewCommit)}); " +
                         "commit the changed submodule in your project's repository.");
        }
        // The journal first: a verified journal whose marker is gone blocks every later update until it is committed.
        var committed = false;
        try
        {
            journal.Save(UpdatePhase.Committed);
            committed = true;
        }
        catch (Exception ex) { host.Log("Update was committed, but its journal could not be marked so; it is finished at startup: " + ex.Message); }
        try
        {
            foreach (var plugin in journal.Plugins)
            {
                cache.State.Revisions[plugin.Slug] = new(plugin.SourceUrl, plugin.Revision, DateTimeOffset.UtcNow, plugin.NewVersion);
                cache.State.Results.RemoveAll(c => c.Slug == plugin.Slug);
            }
            cache.Save();
        }
        catch (Exception ex) { host.Log("Update was committed, but the update cache could not be saved: " + ex.Message); }
        try
        {
            if (committed) AddonFiles.DeleteTree(journal.Directory);
        }
        catch (Exception ex) { host.Log("Update was committed; cleanup will be retried at startup: " + ex.Message); }
        if (journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp) && !RunsInterimBuild(journal))
        {
            // Committed journals must never be written again after their folder was deleted.
            journal.State = UpdatePhase.Committed;
            try { host.RequestReload(journal); }
            catch (Exception ex) { host.Log("Update committed. Reopen the editor to load the final assembly: " + ex.Message); }
        }
        else if (journal.Plugins.FirstOrDefault(p => PluginKinds.Workflow(p).NeedsRestart(p)) is { } native)
        {
            journal.State = UpdatePhase.Committed;
            try { host.RequestReload(journal); }
            catch (Exception ex) { host.Log($"Update committed. Reopen the editor to load the new native libraries of {native.Slug}: " + ex.Message); }
        }
        return UpdateOutcome.Completed;
    }

    public UpdateOutcome Keep(UpdateJournal journal)
    {
        // Retry and Roll back find a kept update by its marker; one that cannot be marked so is not kept.
        if (!store.TryFail(journal.AttemptId, PersistedPluginState.Failed, AttemptReasons.UpdateKeptBuildFailed))
            return Rollback(journal, "The new versions could not be kept: the local plugin state cannot record them.");
        journal.Save(UpdatePhase.CommittedWithErrors);
        host.Log($"New versions kept with build errors. Fix the project and use Retry under Needs attention in the ePlugin Manager. Backup: {journal.Directory}");
        return UpdateOutcome.KeptWithErrors;
    }

    public UpdateOutcome Rollback(UpdateJournal journal, string reason)
    {
        if (store.LastCompletedAttemptId == journal.AttemptId)
        {
            host.Log("Update was committed; cleanup will be retried at startup: " + reason);
            return UpdateOutcome.Completed;
        }
        journal.Failure = reason;
        // These reconciled their recipes, which Reconcile undoes below, and then waited for Retry: their project backup
        // predates the changes made since (other plugins' NuGets, the user's fixes), which restoring it would drop.
        if (journal.State is UpdatePhase.Verified || journal.State is UpdatePhase.CommittedWithErrors && !journal.BeforeReconcile)
            journal.KeepProjectFiles = true;
        try
        {
            journal.Save(UpdatePhase.RollingBack);
            foreach (var plugin in journal.Plugins.AsEnumerable().Reverse())
            {
                var installed = Installed(plugin.Slug);
                if (System.IO.Directory.Exists(journal.Backup(plugin.Slug)))
                {
                    var workflow = PluginKinds.Workflow(plugin);
                    var swap = Swap(workflow, plugin);
                    host.Log($"{workflow.Name} {plugin.Slug}: restoring {plugin.OldVersion} ({swap.Name}).");
                    if (workflow.DisabledDuringSwap && host.IsEnabled(plugin.Slug)) host.SetPlainEnabled(plugin.Slug, false);
                    var discarded = swap.Restore(installed, journal.Backup(plugin.Slug), journal.Discarded(plugin.Slug));
                    workflow.AfterRestore(new(host, journal, plugin, installed, discarded));
                    journal.Save();
                }
                // A GDExtension add-on has no plugin.cfg to tell; its file swap backs up everything before it changes a file.
                else if (plugin.SwapStarted && PluginKinds.Workflow(plugin).HasPluginConfig && (!File.Exists(Path.Combine(installed, "plugin.cfg")) ||
                    PluginIni.Parse(File.ReadAllText(Path.Combine(installed, "plugin.cfg"))).GetValueOrDefault("version") != plugin.OldVersion))
                    throw new IOException($"Backup for {plugin.Slug} is missing; restore manually.");
            }
            try { host.Reconcile(journal, true); }
            finally
            {
                if (!journal.KeepProjectFiles)
                {
                    var (backup, files) = ProjectBackup.Find(journal.Directory, journal.ProjectFiles);
                    ProjectBackup.Restore(projectRoot, backup, files, projectSettings: false);
                    // Once only: a retry after the build below failed must not write the backup over the repairs made since.
                    journal.KeepProjectFiles = true;
                    journal.Save();
                }
            }
            RestorePlainPlugins(journal, rollback: true);
            host.Scan();
            if (journal.Plugins.Any(p => p.ContainsCSharp) && _builds.Build(journal, "rollback").ExitCode != 0) throw new IOException("Restored project failed to build.");
            if (store.LocalAttempts.Any(a => a.AttemptId == journal.AttemptId) && !store.TryAbandonAttempt(journal.AttemptId)) throw new IOException("Cannot clear the rollback marker.");
            journal.Save(UpdatePhase.RolledBack);
        }
        catch (Exception ex)
        {
            store.TryFail(journal.AttemptId, PersistedPluginState.Failed, AttemptReasons.UpdateRollbackFailed);
            journal.Failure = reason + "; rollback failed: " + ex.Message;
            // The marker sends the update to Retry even when the journal cannot say why; throwing here would report it as
            // never begun.
            try { journal.Save(UpdatePhase.RollingBack); }
            catch (Exception saveEx) { host.Log("Cannot save the update journal: " + saveEx.Message); }
            host.Log(journal.Failure + ". Use Retry under Needs attention in the ePlugin Manager after repair. Backup: " + journal.Directory);
            return UpdateOutcome.KeptWithErrors;
        }
        // The rollback is complete. Nothing after this may mark it as rolling back again: repeating it would restore the
        // project files over changes made since.
        RebasePending(journal, false);
        try
        {
            foreach (var plugin in journal.Plugins)
            {
                if (!cache.State.FailedUpdates.TryGetValue(plugin.Slug, out var failures)) cache.State.FailedUpdates[plugin.Slug] = failures = [];
                failures.Add(new(plugin.NewVersion, reason, DateTimeOffset.UtcNow));
            }
            cache.Save();
        }
        catch (Exception ex) { host.Log("Cannot record the rolled-back versions as failed: " + ex.Message); }
        host.Log($"Update rolled back: {reason}. Logs: {journal.Directory}");
        if (journal.Plugins.Any(p => p.ContainsCSharp || PluginKinds.Workflow(p).NeedsRestart(p)))
        {
            try { host.RequestReload(journal); }
            catch (Exception ex) { host.Log("Update rolled back. Reopen the editor to load the restored plugins: " + ex.Message); }
        }
        return UpdateOutcome.RolledBack;
    }

    /// <summary>
    /// Rolls back an update kept with build errors, on the user's request. One kept before its recipes were reconciled
    /// first takes the project files and the other plugins' recipes as they are now, so changes made while fixing the
    /// project survive; the updated plugins' recipes were never applied, so there is nothing of theirs to undo. One kept
    /// after reconciling leaves the project files alone (<see cref="UpdateJournal.KeepProjectFiles"/>): Reconcile undoes
    /// its recipe changes, and anything else changed since stays.
    /// </summary>
    public UpdateOutcome RollbackKept(UpdateJournal journal)
    {
        if (journal.State != UpdatePhase.CommittedWithErrors) throw new InvalidOperationException("Only an update kept with build errors can be rolled back this way.");
        host.CloseScenes();
        if (journal.BeforeReconcile)
        {
            host.RebaseRecipes(journal);
            ProjectBackup.Write(projectRoot, journal, true);
            journal.Save();
        }
        return Rollback(journal, "Rolled back on request after it was kept with build errors.");
    }

    public UpdateOutcome Retry(UpdateJournal journal)
    {
        host.CloseScenes();
        var reason = store.LocalAttempts.FirstOrDefault(a => a.AttemptId == journal.AttemptId)?.Reason;
        if (reason == AttemptReasons.UpdateCommitFailed) return journal.Plugins.All(host.Verify) ? Commit(journal) : Rollback(journal, "Retry verification failed.");
        if (reason == AttemptReasons.UpdateKeptBuildFailed)
        {
            var retry = _builds.Build(journal, "retry");
            if (retry.ExitCode != 0)
            {
                _builds.RetryFailed(journal, retry);
                return UpdateOutcome.KeptWithErrors;
            }
            if (journal.BeforeReconcile) return ResumeAfterRestart(journal);
            if (!journal.Plugins.All(host.Verify)) return Rollback(journal, "Retry verification failed.");
            journal.Save(UpdatePhase.Verified);
            return Commit(journal);
        }
        return Rollback(journal, "Manual update recovery.");
    }

    /// <summary>
    /// Continues an update kept before its recipes were reconciled, now that the project builds: like after a working
    /// interim build, the editor restarts into the new code and the update resumes with reconciling. The project may have
    /// changed since it was kept, so a rollback from here on restores it, and the other plugins' recipes, as they are now.
    /// </summary>
    private UpdateOutcome ResumeAfterRestart(UpdateJournal journal)
    {
        if (!store.TryFail(journal.AttemptId, PersistedPluginState.Failed, AttemptReasons.InProgress))
        {
            host.Log("Cannot clear the kept-update marker; the update stays kept. Backup: " + journal.Directory);
            return UpdateOutcome.KeptWithErrors;
        }
        try
        {
            host.RebaseRecipes(journal);
            ProjectBackup.Write(projectRoot, journal, true);
            journal.BeforeReconcile = false;
            journal.Failure = null;
            journal.FailureCause = null; journal.PluginsBuildAlone = null; journal.ProjectErrors = [];
            journal.Save(UpdatePhase.InterimBuilt);
            host.Log("The kept update builds now; the editor restarts to finish it.");
            host.RequestReload(journal);
            return UpdateOutcome.AwaitingReload;
        }
        catch (Exception ex) { return Rollback(journal, ex.Message); }
    }

    /// <summary>
    /// The interim build is followed by a restart, so the editor runs it until a later build replaces it; a batch whose
    /// last build is the interim one needs no restart to load its code.
    /// </summary>
    private static bool RunsInterimBuild(UpdateJournal journal) => journal.Builds.LastOrDefault()?.Name == "interim";

    /// <summary>
    /// Whether reconciling changed what the interim build compiled: the interim build already contains the new plugin
    /// code, but also the old files the bridge kept, and none of the recipes' new NuGets, projects or directories.
    /// </summary>
    private static bool ChangedSinceInterimBuild(UpdateJournal journal) =>
        journal.Plugins.Any(p => p.Preserved.Count > 0) ||
        journal.Recipes.Values.Any(r => r.Target is null || RecipeReconciler.ChangesBuild(r.Old, r.Target));

    /// <summary>
    /// Enables the plugins whose kind is disabled while its files are swapped (<see cref="PluginKindWorkflow.DisabledDuringSwap"/>)
    /// again; a rollback restores whether each was enabled before the update instead.
    /// </summary>
    /// <param name="onlyDisabled">Leaves a plugin that is enabled already alone.</param>
    private void RestorePlainPlugins(UpdateJournal journal, bool rollback = false, bool onlyDisabled = false)
    {
        foreach (var plugin in journal.Plugins.Where(p => PluginKinds.Workflow(p).DisabledDuringSwap))
        {
            if (onlyDisabled && host.IsEnabled(plugin.Slug)) continue;
            host.SetPlainEnabled(plugin.Slug, !rollback || plugin.WasEnabled);
        }
    }

    /// <summary>
    /// What becomes of a transaction whose build failed, as the build-failure policy decides (<see cref="UpdateBuildGate.Decision"/>):
    /// rolled back for <paramref name="rollbackReason"/>, left for the user to decide, or kept with
    /// <paramref name="failure"/> (or the project's own errors, when only they failed) as the reason.
    /// </summary>
    /// <param name="beforeReconcile">The interim or plain C# build failed: the editor cannot load the new code, so the
    /// recipes are reconciled once the project builds again. The plain plugins are enabled again first, and the journal
    /// is saved before <see cref="Keep"/> marks it kept, so a Retry of a kept update the editor stopped between the two
    /// still reconciles its recipes rather than committing it without them.</param>
    private UpdateOutcome BuildFailed(UpdateJournal journal, string rollbackReason, bool beforeReconcile, string failure)
    {
        var decision = _builds.Decision(journal);
        if (decision == BuildFailurePolicy.Rollback) return Rollback(journal, rollbackReason);
        if (beforeReconcile)
        {
            RestorePlainPlugins(journal);
            journal.BeforeReconcile = true;
        }
        journal.Failure = journal.KeptForProjectCode ? UpdateBuildGate.ProjectCodeFailure(journal) : failure;
        if (decision == BuildFailurePolicy.Ask) { journal.Save(UpdatePhase.AwaitingDecision); return UpdateOutcome.AwaitingDecision; }
        if (beforeReconcile) journal.Save();
        return Keep(journal);
    }

    /// <summary>
    /// The other groups of a batch that have not reconciled yet were backed up before this journal changed the project.
    /// They take over its result, so rolling one of them back later restores the project with this journal's changes
    /// rather than undoing them. Recipes are taken over only from a commit: a rollback restored what they already hold.
    /// </summary>
    private void RebasePending(UpdateJournal finished, bool committed)
    {
        try
        {
            // a pending update that cannot be read keeps its old backup, which is what the warning below is about too
            var journals = new UpdateJournals(Path.Combine(projectRoot, ".godot/eplugin/updates"), message => host.Log(message +
                " (its backups are not refreshed: rolling it back may undo this update's project changes)"));
            foreach (var pending in journals.Read()
                         .Where(j => j.Id != finished.Id && j.State is UpdatePhase.Staged or UpdatePhase.Swapped or UpdatePhase.InterimBuilt))
            {
                if (committed)
                    foreach (var (slug, record) in finished.Recipes)
                        if (pending.Plugins.All(p => p.Slug != slug) && pending.Recipes.TryGetValue(slug, out var own))
                        { own.Old = record.Applied.Clone(); own.Applied = record.Applied.Clone(); }
                ProjectBackup.Write(projectRoot, pending, true);
                pending.Save();
            }
        }
        catch (Exception ex) { host.Log("Cannot refresh the backups of the remaining plugin updates; rolling one of them back may undo this update's project changes: " + ex.Message); }
    }
}
#endif
