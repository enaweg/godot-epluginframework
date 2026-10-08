#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Dotnet;

namespace Enaweg.Plugin.Internal.Update;

internal enum UpdateOutcome { Completed, AwaitingReload, AwaitingDecision, KeptWithErrors, RolledBack }
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
    public static string Choose(bool finalBuild, bool selfUpdate, string policy, bool uiAvailable) =>
        !finalBuild || selfUpdate || !uiAvailable ? "rollback" : policy == "keep" ? "keep" : policy == "ask" && uiAvailable ? "ask" : "rollback";
}
internal sealed class UpdateApplier(string projectRoot, PluginStateStore store, IUpdateHost host, IUpdateStateStore cache)
{
    private string Installed(string slug) => PackageFiles.Inside(Path.Combine(projectRoot, "addons"), slug);
    public UpdateOutcome Apply(IReadOnlyList<ValidatedPackage> packages, string transactionDirectory, bool trustChangedSources = false)
    {
        if (packages.Count == 0 || packages.Any(p => !p.IsValid) || packages.Select(p => p.Candidate.Slug).Distinct().Count() != packages.Count)
            throw new InvalidOperationException("No valid distinct addon batch was selected.");
        if (!trustChangedSources && packages.Any(p => p.Findings.Any(f => f.RequiresTrust))) throw new InvalidOperationException("Confirm trust in the changed update source before installation.");
        if (store.IsReadOnly || packages.Any(p => store.IsBlocked(p.Candidate.Slug))) throw new InvalidOperationException("Resolve local plugin state before updating.");
        host.Preflight(packages);
        var journal = new UpdateJournal { Id = Path.GetFileName(transactionDirectory), Directory = transactionDirectory };
        foreach (var package in packages)
        {
            var slug = package.Candidate.Slug;
            if (!host.IsEnabled(slug)) throw new InvalidOperationException($"Plugin {slug} is no longer enabled.");
            var old = PluginIni.Parse(File.ReadAllText(Path.Combine(Installed(slug), "plugin.cfg"))).GetValueOrDefault("version");
            if (old != package.Candidate.InstalledVersion) throw new InvalidOperationException($"Plugin {slug} changed since the update check; check again.");
            var next = PluginIni.Parse(File.ReadAllText(Path.Combine(package.StagingDir, "plugin.cfg"))).GetValueOrDefault("version")!;
            journal.Plugins.Add(new() { Slug = slug, OldVersion = old!, NewVersion = next, IsEPlugin = host.IsManaged(slug),
                WasEnabled = true, ContainsCSharp = package.ContainsCSharp, SourceUrl = package.Candidate.SourceUrl, Revision = package.Candidate.ResolvedRevision,
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
                    var decision = UpdateFailureDecision.Choose(!managed, journal.Plugins.Any(p => p.Slug == "ePlugin"), host.BuildFailurePolicy, host.UiAvailable);
                    if (!managed)
                        foreach (var plugin in journal.Plugins.Where(p => !p.IsEPlugin)) host.SetPlainEnabled(plugin.Slug, true);
                    if (decision == "ask") { journal.Failure = "Plain C# addon build failed; the assembly cannot reload until the project compiles."; journal.Save(UpdatePhase.AwaitingDecision); return UpdateOutcome.AwaitingDecision; }
                    if (decision == "keep") return Keep(journal);
                    return Rollback(journal, "Build failed.");
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
            // A plain C# batch already passed its single build gate. Managed recipes need a final build.
            if (journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp) && Build(journal, "final").ExitCode != 0)
            {
                var decision = UpdateFailureDecision.Choose(true, journal.Plugins.Any(p => p.Slug == "ePlugin"), host.BuildFailurePolicy, host.UiAvailable);
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
        try
        {
            foreach (var plugin in journal.Plugins)
            {
                cache.State.Revisions[plugin.Slug] = new(plugin.SourceUrl, plugin.Revision, DateTimeOffset.UtcNow);
                cache.State.Results.RemoveAll(c => c.Slug == plugin.Slug);
                host.Log($"Updated {plugin.Slug} {plugin.OldVersion} -> {plugin.NewVersion}.");
            }
            cache.Save();
            journal.Save(UpdatePhase.Committed);
            System.IO.Directory.Delete(journal.Directory, true);
        }
        catch (Exception ex) { host.Log("Update was committed; cleanup will be retried at startup: " + ex.Message); }
        if (journal.Plugins.Any(p => p.IsEPlugin && p.ContainsCSharp))
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
            if (!journal.Plugins.All(host.Verify)) return Rollback(journal, "Retry verification failed.");
            journal.Save(UpdatePhase.Verified);
            return Commit(journal);
        }
        return Rollback(journal, "Manual update recovery.");
    }

    private BuildOutcome Build(UpdateJournal journal, string name)
    {
        var build = host.Build();
        var log = "build-" + name + ".log";
        File.WriteAllLines(Path.Combine(journal.Directory, log), build.Output);
        journal.Builds.Add(new(name, build.ExitCode, log));
        journal.Save();
        return build;
    }
    private void BackupProject(UpdateJournal journal)
    {
        var backup = Path.Combine(journal.Directory, "backup-project");
        System.IO.Directory.CreateDirectory(backup);
        foreach (var file in System.IO.Directory.GetFiles(projectRoot).Where(f => new[] { ".csproj", ".sln", ".slnx" }.Contains(Path.GetExtension(f)) || Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "project.godot"))
        {
            journal.ProjectFiles.Add(Path.GetFileName(file));
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)), false);
        }
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
