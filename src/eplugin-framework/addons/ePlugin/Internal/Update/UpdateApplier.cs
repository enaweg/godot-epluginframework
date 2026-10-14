#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Dotnet;

namespace Enaweg.Plugin.Internal.Update;

internal enum UpdateOutcome { Completed, AwaitingReload, AwaitingDecision, KeptWithErrors, RolledBack }
/// <summary>
/// What became of one group of a batch. <paramref name="Outcome"/> is null when the group was refused before its
/// transaction began, so none of its addons changed; <paramref name="Failure"/> then says why.
/// </summary>
internal sealed record UpdateGroupResult(IReadOnlyList<ValidatedPackage> Packages, string Directory, UpdateOutcome? Outcome, string? Failure);
internal interface IUpdateHost
{
    bool UiAvailable { get; }
    string BuildFailurePolicy { get; }
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
    void RequestReload(UpdateJournal journal);
    bool Verify(UpdatePluginJournal plugin);
    void Log(string message);
}
internal static class UpdateFailureDecision
{
    public static string Choose(bool selfUpdate, string policy, bool uiAvailable) =>
        selfUpdate || !uiAvailable ? "rollback" : policy is "keep" or "ask" ? policy : "rollback";

    /// <summary>
    /// The project's <c>on_build_failure</c> setting for one transaction: <c>ask</c> asks only for an update started from
    /// a plugin's details, so installing all updates rolls back; a group of a larger batch always rolls back, because
    /// keeping a project that does not build would fail every later group too.
    /// </summary>
    public static string Policy(string setting, bool grouped, bool askOnBuildFailure) =>
        grouped ? "rollback" : setting == "keep" ? "keep" : setting == "ask" && askOnBuildFailure ? "ask" : "rollback";
}
internal sealed class UpdateApplier(string projectRoot, PluginStateStore store, IUpdateHost host, IUpdateStateStore cache)
{
    private string Installed(string slug) => PackageFiles.Inside(Path.Combine(projectRoot, "addons"), slug);

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
            catch (Exception ex) { result = new(packages, directory, null, ex.Message); }
            try
            {
                // A group refused before its marker leaves only its staging behind.
                if (!File.Exists(Path.Combine(directory, "journal.json")) && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
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
        if (outcome is UpdateOutcome.RolledBack or UpdateOutcome.KeptWithErrors)
        {
            try { failure = UpdateJournal.Load(directory).Failure; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
        }
        return new(packages, directory, outcome, failure);
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
                    Move(package.StagingDir, staging);
                    moved.Add(package with { StagingDir = staging });
                }
            }
        }
        catch
        {
            foreach (var (_, directory) in split.Skip(1))
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
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
            var old = PluginIni.Parse(File.ReadAllText(Path.Combine(Installed(slug), "plugin.cfg"))).GetValueOrDefault("version");
            if (old != package.Candidate.InstalledVersion) throw new InvalidOperationException($"Plugin {slug} changed since the update check; check again.");
            var next = PluginIni.Parse(File.ReadAllText(Path.Combine(package.StagingDir, "plugin.cfg"))).GetValueOrDefault("version")!;
            journal.Plugins.Add(new() { Slug = slug, OldVersion = old!, NewVersion = next, IsEPlugin = host.IsManaged(slug),
                WasEnabled = true, ContainsCSharp = package.ContainsCSharp, SourceUrl = package.Candidate.Origin, Revision = package.Candidate.ResolvedRevision,
                Uids = UidMap.Collect(Installed(slug)) });
        }
        host.PrepareJournal(journal);
        BackupProject(journal);
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
        File.WriteAllText(Path.Combine(transactionDirectory, "README.txt"), "Manual restore: close the editor, move backup/<slug> to addons/<slug>, restore backup-project files if necessary, delete .godot/mono/temp and rebuild. Do not remove the local plugin-state marker until recovery succeeds.\n");
        try
        {
            host.CloseScenes();
            host.BeforeSwap(journal);
            journal.Save(UpdatePhase.Swapped);
            foreach (var plugin in journal.Plugins)
            {
                plugin.SwapStarted = true;
                journal.Save();
                if (!plugin.IsEPlugin) host.SetPlainEnabled(plugin.Slug, false);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(journal.Backup(plugin.Slug))!);
                Move(Installed(plugin.Slug), journal.Backup(plugin.Slug));
                Move(journal.Staging(plugin.Slug), Installed(plugin.Slug));
                UidMap.Restore(Installed(plugin.Slug), plugin.Uids);
                host.Bridge(journal, plugin);
            }
            host.Scan();
            if (journal.Plugins.Any(p => p.ContainsCSharp) || host.RestartAlways)
            {
                var managed = journal.Plugins.Any(p => p.IsEPlugin);
                if (Build(journal, managed ? "interim" : "final").ExitCode != 0)
                {
                    var decision = UpdateFailureDecision.Choose(journal.Plugins.Any(p => p.Slug == "ePlugin"), Policy(journal), host.UiAvailable);
                    if (decision == "rollback") return Rollback(journal, "Build failed.");
                    foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin)) host.SetPlainEnabled(plugin.Slug, true);
                    // The editor cannot load the new code, so the recipes are reconciled once the project builds again.
                    journal.BeforeReconcile = true;
                    journal.Failure = (managed ? "Interim build failed" : "Plain C# addon build failed") + "; the assembly cannot reload until the project compiles.";
                    if (decision == "ask") { journal.Save(UpdatePhase.AwaitingDecision); return UpdateOutcome.AwaitingDecision; }
                    return Keep(journal);
                }
                journal.Save(UpdatePhase.InterimBuilt);
                foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin)) host.SetPlainEnabled(plugin.Slug, true);
                host.RequestReload(journal);
                return UpdateOutcome.AwaitingReload;
            }
            foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin)) host.SetPlainEnabled(plugin.Slug, true);
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
            foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin))
                if (!host.IsEnabled(plugin.Slug)) host.SetPlainEnabled(plugin.Slug, true);
            host.Scan();
            // A plain C# batch already passed its single build gate. Managed recipes need a final build, unless reconciling
            // left the build inputs of the interim build the editor runs unchanged.
            var managedCSharp = journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp);
            var finalBuild = managedCSharp && (host.RestartAlways || !RunsInterimBuild(journal) || ChangedSinceInterimBuild(journal));
            if (managedCSharp && !finalBuild) host.Log("The updated recipes do not change the build; the editor keeps the interim build without another restart.");
            if (finalBuild && Build(journal, "final").ExitCode != 0)
            {
                var decision = UpdateFailureDecision.Choose(journal.Plugins.Any(p => p.Slug == "ePlugin"), Policy(journal), host.UiAvailable);
                if (decision == "ask") { journal.Failure = "Final build failed."; journal.Save(UpdatePhase.AwaitingDecision); return UpdateOutcome.AwaitingDecision; }
                if (decision == "keep") return Keep(journal);
                return Rollback(journal, "Final build failed.");
            }
            if (journal.Plugins.Any(p => !host.Verify(p))) return Rollback(journal, "Updated plugin verification failed.");
            if (journal.Plugins.Any(p => p.Slug == "ePlugin")) File.WriteAllText(Path.Combine(journal.Directory, "healthy.marker"), "verified");
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
            store.TryFail(journal.AttemptId, PersistedPluginState.Failed, "update_commit_failed");
            journal.Failure = "Healthy update could not be acknowledged; merge the shared state file and use Retry failed in the ePlugin Manager.";
            journal.Save(UpdatePhase.Verified);
            host.Log(journal.Failure);
            return UpdateOutcome.KeptWithErrors;
        }
        RebasePending(journal, true);
        // Updates never show a welcome page, also not one the plugin did not have or show before.
        var unseen = journal.Plugins.Where(p => !store.IsWelcomeShown(p.Slug)).Select(p => new ShownWelcome(p.Slug, p.NewVersion, DateTimeOffset.UtcNow)).ToArray();
        if (unseen.Length > 0 && !store.TryRecordWelcomes(unseen)) host.Log("The updated plugins' welcome pages could not be marked as shown; they may be shown after the editor restarts.");
        try
        {
            foreach (var plugin in journal.Plugins)
            {
                cache.State.Revisions[plugin.Slug] = new(plugin.SourceUrl, plugin.Revision, DateTimeOffset.UtcNow, plugin.NewVersion);
                cache.State.Results.RemoveAll(c => c.Slug == plugin.Slug);
                host.Log(plugin.OldVersion == plugin.NewVersion ? $"Reinstalled {plugin.Slug} {plugin.NewVersion}."
                    : $"Updated {plugin.Slug} {plugin.OldVersion} -> {plugin.NewVersion}.");
            }
            cache.Save();
            journal.Save(UpdatePhase.Committed);
            System.IO.Directory.Delete(journal.Directory, true);
        }
        catch (Exception ex) { host.Log("Update was committed; cleanup will be retried at startup: " + ex.Message); }
        if (journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp) && !RunsInterimBuild(journal))
        {
            // Committed journals must never be written again after their folder was deleted.
            journal.State = UpdatePhase.Committed;
            try { host.RequestReload(journal); }
            catch (Exception ex) { host.Log("Update committed. Reopen the editor to load the final assembly: " + ex.Message); }
        }
        return UpdateOutcome.Completed;
    }

    public UpdateOutcome Keep(UpdateJournal journal)
    {
        store.TryFail(journal.AttemptId, PersistedPluginState.Failed, "update_kept_build_failed");
        journal.Save(UpdatePhase.CommittedWithErrors);
        host.Log($"New versions kept with build errors. Fix the project and use Retry failed in the ePlugin Manager. Backup: {journal.Directory}");
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
        try
        {
            journal.Save(UpdatePhase.RollingBack);
            foreach (var plugin in journal.Plugins.AsEnumerable().Reverse())
            {
                var installed = Installed(plugin.Slug);
                if (System.IO.Directory.Exists(journal.Backup(plugin.Slug)))
                {
                    if (!plugin.IsEPlugin && host.IsEnabled(plugin.Slug)) host.SetPlainEnabled(plugin.Slug, false);
                    if (System.IO.Directory.Exists(installed)) System.IO.Directory.Delete(installed, true);
                    Move(journal.Backup(plugin.Slug), installed);
                }
                else if (plugin.SwapStarted && (!File.Exists(Path.Combine(installed, "plugin.cfg")) ||
                    PluginIni.Parse(File.ReadAllText(Path.Combine(installed, "plugin.cfg"))).GetValueOrDefault("version") != plugin.OldVersion))
                    throw new IOException($"Backup for {plugin.Slug} is missing; restore manually.");
            }
            try { host.Reconcile(journal, true); }
            finally { RestoreProject(journal); }
            foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin)) host.SetPlainEnabled(plugin.Slug, plugin.WasEnabled);
            host.Scan();
            if (journal.Plugins.Any(p => p.ContainsCSharp) && Build(journal, "rollback").ExitCode != 0) throw new IOException("Restored project failed to build.");
            if (store.LocalAttempts.Any(a => a.AttemptId == journal.AttemptId) && !store.TryAbandonAttempt(journal.AttemptId)) throw new IOException("Cannot clear the rollback marker.");
            journal.Save(UpdatePhase.RolledBack);
            RebasePending(journal, false);
            foreach (var plugin in journal.Plugins)
            {
                if (!cache.State.FailedUpdates.TryGetValue(plugin.Slug, out var failures)) cache.State.FailedUpdates[plugin.Slug] = failures = [];
                failures.Add(new(plugin.NewVersion, reason, DateTimeOffset.UtcNow));
            }
            cache.Save();
            host.Log($"Update rolled back: {reason}. Logs: {journal.Directory}");
            if (journal.Plugins.Any(p => p.ContainsCSharp)) host.RequestReload(journal);
            return UpdateOutcome.RolledBack;
        }
        catch (Exception ex)
        {
            store.TryFail(journal.AttemptId, PersistedPluginState.Failed, "update_rollback_failed");
            journal.Failure = reason + "; rollback failed: " + ex.Message;
            journal.Save(UpdatePhase.RollingBack);
            host.Log(journal.Failure + ". Use Retry failed in the ePlugin Manager after repair. Backup: " + journal.Directory);
            return UpdateOutcome.KeptWithErrors;
        }
    }

    public UpdateOutcome Retry(UpdateJournal journal)
    {
        host.CloseScenes();
        var reason = store.LocalAttempts.FirstOrDefault(a => a.AttemptId == journal.AttemptId)?.Reason;
        if (reason == "update_commit_failed") return journal.Plugins.All(host.Verify) ? Commit(journal) : Rollback(journal, "Retry verification failed.");
        if (reason == "update_kept_build_failed")
        {
            if (Build(journal, "retry").ExitCode != 0) return UpdateOutcome.KeptWithErrors;
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
        if (!store.TryFail(journal.AttemptId, PersistedPluginState.Failed, "attempt_in_progress"))
        {
            host.Log("Cannot clear the kept-update marker; the update stays kept. Backup: " + journal.Directory);
            return UpdateOutcome.KeptWithErrors;
        }
        try
        {
            host.RebaseRecipes(journal);
            BackupProject(journal, true);
            journal.BeforeReconcile = false;
            journal.Failure = null;
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

    private BuildOutcome Build(UpdateJournal journal, string name)
    {
        var build = host.Build();
        var log = "build-" + name + ".log";
        File.WriteAllLines(Path.Combine(journal.Directory, log), build.Output);
        journal.Builds.Add(new(name, build.ExitCode, log));
        journal.Save();
        return build;
    }
    private string Policy(UpdateJournal journal) => UpdateFailureDecision.Policy(host.BuildFailurePolicy, journal.Group is not null, journal.AskOnBuildFailure);

    /// <summary>
    /// The other groups of a batch that have not reconciled yet were backed up before this journal changed the project.
    /// They take over its result, so rolling one of them back later restores the project with this journal's changes
    /// rather than undoing them. Recipes are taken over only from a commit: a rollback restored what they already hold.
    /// </summary>
    private void RebasePending(UpdateJournal finished, bool committed)
    {
        try
        {
            foreach (var pending in new UpdateJournals(Path.Combine(projectRoot, ".godot/eplugin/updates")).Read()
                         .Where(j => j.Id != finished.Id && j.State is UpdatePhase.Staged or UpdatePhase.Swapped or UpdatePhase.InterimBuilt))
            {
                if (committed)
                    foreach (var (slug, record) in finished.Recipes)
                        if (pending.Plugins.All(p => p.Slug != slug) && pending.Recipes.TryGetValue(slug, out var own))
                        { own.Old = record.Applied.Clone(); own.Applied = record.Applied.Clone(); }
                BackupProject(pending, true);
                pending.Save();
            }
        }
        catch (Exception ex) { host.Log("Cannot refresh the backups of the remaining plugin updates; rolling one of them back may undo this update's project changes: " + ex.Message); }
    }

    private void BackupProject(UpdateJournal journal, bool replace = false)
    {
        var backup = Path.Combine(journal.Directory, "backup-project");
        // A replaced backup is written next to the old one and swapped in, so it is never half written.
        var target = replace ? backup + ".new" : backup;
        if (replace && System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
        System.IO.Directory.CreateDirectory(target);
        var files = new List<string>();
        foreach (var file in System.IO.Directory.GetFiles(projectRoot).Where(f => Path.GetExtension(f) is ".csproj" or ".sln" or ".slnx" || Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "project.godot"))
        {
            files.Add(Path.GetFileName(file));
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        }
        if (replace)
        {
            var old = backup + ".old";
            if (System.IO.Directory.Exists(old)) System.IO.Directory.Delete(old, true);
            if (System.IO.Directory.Exists(backup)) Move(backup, old);
            Move(target, backup);
            if (System.IO.Directory.Exists(old)) System.IO.Directory.Delete(old, true);
        }
        journal.ProjectFiles = files;
    }
    private void RestoreProject(UpdateJournal journal)
    {
        // The live editor owns project.godot; normal rollback restores autoloads via recipe operations.
        // Its backup is reserved for independent early recovery, which reloads settings before restart.
        foreach (var file in journal.ProjectFiles.Where(f => f != "project.godot")) File.Copy(PackageFiles.Inside(Path.Combine(journal.Directory, "backup-project"), file), PackageFiles.Inside(projectRoot, file), true);
        if (!journal.ProjectFiles.Any(p => p.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(projectRoot, "nuget.config"))) File.Delete(Path.Combine(projectRoot, "nuget.config"));
    }
    private static void Move(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { System.IO.Directory.Move(from, to); return; }
            catch (IOException) when (attempt < 2) { Thread.Sleep(200); }
        }
    }
}
#endif
