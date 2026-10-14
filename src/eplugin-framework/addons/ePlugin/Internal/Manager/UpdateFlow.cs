#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>What the ePlugin Manager's update workflows need from the framework; <see cref="EGlobal"/> provides it.</summary>
internal interface IUpdateWorkflowHost
{
    IReadOnlyList<UpdateCandidate> PendingUpdates { get; }
    bool IsRestartPending { get; }
    Task<UpdateCheckResult> CheckForUpdatesAsync(bool force);
    Task<IReadOnlyList<UpdateCandidate>> ListRemoteVersionsAsync(PluginUpdateTarget target, CancellationToken ct);

    Task<(IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused, string Directory)> StageUpdatesAsync(
        IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress, bool allowDowngrade, bool allowReinstall,
        CancellationToken ct);

    LicenseReview? ReviewUpdateLicenses(IEnumerable<ValidatedPackage> packages);
    void RecordLicenses(LicenseReview review);

    Task<IReadOnlyList<UpdateGroupResult>> ApplyUpdatesAsync(IReadOnlyList<ValidatedPackage> packages, string directory,
        bool trustChangedSources, bool askOnBuildFailure);
}

/// <summary>What to install: the checked updates, or one version chosen in a plugin's details.</summary>
internal sealed record InstallRequest(IReadOnlyList<UpdateCandidate> Batch, bool VersionInstall = false,
    bool AllowDowngrade = false, bool AllowReinstall = false);

internal enum InstallStep
{
    /// <summary>The batch is staged and waits for the user to review its warnings or trust a changed source.</summary>
    Reviewing,
    /// <summary>The updates were installed; their results are in the notice.</summary>
    Installed,
    /// <summary>A build failed and the update failure dialog takes over the decision.</summary>
    AwaitingDecision,
    /// <summary>The download was canceled or every license was declined; addon files were not changed.</summary>
    Canceled,
    /// <summary>Staging or installing failed before an outcome; addon files were not changed unless a journal says so.</summary>
    Failed,
    /// <summary>The manager went away meanwhile; nothing is shown.</summary>
    Abandoned
}

/// <param name="Notice">The status line to show; null for none.</param>
/// <param name="IsError">The notice reports a failure and is logged as an error too.</param>
/// <param name="Refresh">Installed versions changed, so the plugin list has to be read again.</param>
internal sealed record InstallOutcome(InstallStep Step, string? Notice = null, bool IsError = false, bool Refresh = false);

/// <summary>The result of checking for updates: the status line, and whether it reports a failure.</summary>
internal sealed record CheckOutcome(string? Notice, bool IsError);

/// <summary>What an installation tells the manager while it runs.</summary>
internal sealed class InstallView
{
    /// <summary>Whether the manager is still shown; once it is not, a running installation stops before changing files.</summary>
    public Func<bool> Alive { get; init; } = () => true;
    public Action<string> Status { get; init; } = _ => { };
    /// <summary>The packages were staged; the manager shows their findings and the refused ones.</summary>
    public Action<IReadOnlyList<ValidatedPackage>, IReadOnlyList<RefusedPackage>> Staged { get; init; } = (_, _) => { };
    /// <summary>Addon files are about to be replaced; the manager can no longer be closed.</summary>
    public Action Swapping { get; init; } = () => { };
}

/// <summary>
/// The update workflows of the ePlugin Manager without its UI: checking for updates, loading a plugin's published
/// versions, and staging, reviewing and installing a batch. The dialog shows what they report.
/// </summary>
/// <param name="reviewLicenses">Shows the license dialog and completes once every license was decided.</param>
/// <param name="beginProgress">Starts a progress scope in ePlugin's progress window.</param>
internal sealed class UpdateFlow(IUpdateWorkflowHost host, Func<LicenseReview, Task<LicenseReview>> reviewLicenses,
    Func<string, IEPluginProgressScope> beginProgress)
{
    private readonly Dictionary<string, IReadOnlyList<UpdateCandidate>> _remoteVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _remoteVersionErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingVersions = new(StringComparer.Ordinal);

    /// <summary>
    /// Versions published at each plugin's update site, loaded on demand and kept until the next check. Local versions
    /// come from the in-memory index, so a new index never fetches these again.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<UpdateCandidate>> RemoteVersions => _remoteVersions;

    /// <summary>Why a plugin's published versions could not be loaded.</summary>
    public IReadOnlyDictionary<string, string> RemoteVersionErrors => _remoteVersionErrors;

    public bool IsLoadingVersions(string slug) => _loadingVersions.Contains(slug);

    /// <summary>The batch being staged or awaiting review; null when nothing is staged.</summary>
    public InstallRequest? Request { get; private set; }

    /// <summary>The validated packages of the batch awaiting review; null when nothing is staged.</summary>
    public IReadOnlyList<ValidatedPackage>? Staged { get; private set; }

    /// <summary>Packages of the staged batch that were refused; the others are installed without them.</summary>
    public IReadOnlyList<RefusedPackage> Refused { get; private set; } = [];

    /// <summary>Whether a staged package moves to another update host, which the user has to trust explicitly.</summary>
    public bool StagedNeedsTrust => Staged?.Any(p => p.Findings.Any(f => f.RequiresTrust)) == true;

    private string? _directory;

    /// <summary>Checks every plugin for updates now, also local packages; returns the status line.</summary>
    public async Task<CheckOutcome> CheckAsync()
    {
        Clear();
        _remoteVersions.Clear();
        _remoteVersionErrors.Clear();
        try
        {
            var result = await host.CheckForUpdatesAsync(true);
            if (result.Failures.Count > 0)
            {
                return new("Update check failed: " + string.Join("; ", result.Failures.Select(f => $"{f.Slug}: {f.Message}")), true);
            }

            return new(result.Fallbacks is { Count: > 0 } fallbacks
                ? "The update site of " + string.Join(", ", fallbacks.Select(f => f.Slug)) +
                  " did not work; plugin.cfg's update_url was used (see the output log)."
                : null, false);
        }
        catch (Exception ex)
        {
            return new("Update check failed: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Loads the versions published at a plugin's update site. False when they were loading already or the manager
    /// closed meanwhile; true once they or the reason they are missing are known.
    /// </summary>
    public async Task<bool> LoadVersionsAsync(PluginUpdateTarget target, CancellationToken ct)
    {
        var slug = target.Slug;
        if (!_loadingVersions.Add(slug)) return false;
        try
        {
            _remoteVersions[slug] = await host.ListRemoteVersionsAsync(target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _remoteVersionErrors[slug] = ex.Message;
        }
        finally
        {
            _loadingVersions.Remove(slug);
        }

        return true;
    }

    /// <summary>Forgets loaded versions, e.g. after a plugin's update site changed.</summary>
    public void ClearVersions()
    {
        _remoteVersions.Clear();
        _remoteVersionErrors.Clear();
    }

    /// <summary>
    /// Stages and validates the batch, stops for review when its packages carry warnings or move to another host, asks
    /// for changed licenses, then installs. Called again for a staged batch, it continues with the installation.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(InstallRequest request, bool trustChangedSource, InstallView view,
        CancellationToken ct)
    {
        Request = request;
        try
        {
            if (Staged is null)
            {
                var first = request.Batch[0];
                var text = request.VersionInstall
                    ? $"Downloading and validating {first.PluginName} {first.NewVersion}..."
                    : "Downloading and validating selected addons...";
                view.Status(text);
                (IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused, string Directory) staged;
                using (var progress = beginProgress(text))
                {
                    // Reported from the download thread; the scope ignores a report that arrives after it ended.
                    var shown = -1;
                    var relay = new InlineProgress(value =>
                    {
                        var percent = (int)(value * 100);
                        if (Interlocked.Exchange(ref shown, percent) != percent) progress.Report($"{text} {percent}%");
                    });
                    staged = await host.StageUpdatesAsync(request.Batch, relay, request.AllowDowngrade, request.AllowReinstall, ct);
                }

                Staged = staged.Packages;
                Refused = staged.Refused;
                _directory = staged.Directory;
                if (!view.Alive())
                {
                    Clear();
                    return new(InstallStep.Abandoned);
                }

                view.Staged(Staged, Refused);
                // Warnings discovered inside the packages are reviewed before the first project side effect.
                if (Staged.Any(p => p.Findings.Any(f => f.Severity == FindingSeverity.Warning)) ||
                    StagedNeedsTrust && !trustChangedSource)
                {
                    return new(InstallStep.Reviewing, PluginManagerViewModel.RefusedSummary(Refused) +
                                                      "Review package warnings in the details, then confirm installation.");
                }
            }

            ct.ThrowIfCancellationRequested();
            if (StagedNeedsTrust && !trustChangedSource)
            {
                return new(InstallStep.Reviewing);
            }

            var selected = Staged!.Where(p => request.Batch.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
            // An update that keeps the accepted license file is not asked about again; a changed or new one is.
            if (host.ReviewUpdateLicenses(selected) is { } licenses)
            {
                view.Status("Review the licenses of the updates.");
                var decided = await reviewLicenses(licenses);
                host.RecordLicenses(decided);
                var declined = decided.Canceled.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
                selected = selected.Where(p => !declined.Contains(p.Candidate.Slug)).ToArray();
                if (!view.Alive())
                {
                    Clear();
                    return new(InstallStep.Abandoned);
                }

                if (selected.Length == 0)
                {
                    var notice = PluginManagerViewModel.RefusedSummary(Refused) +
                                 "Update canceled: its license was declined. Addon files were not changed.";
                    Clear();
                    return new(InstallStep.Canceled, notice);
                }

                ct.ThrowIfCancellationRequested();
            }

            view.Swapping();
            view.Status(request.VersionInstall ? "Installing the selected version..." : "Installing selected addons...");
            // Independent groups are installed one after another; a failing group is rolled back without the others.
            // Only an update started from the plugin's details asks whether to keep a version that does not build.
            var results = await host.ApplyUpdatesAsync(selected, _directory!, trustChangedSource,
                askOnBuildFailure: request.VersionInstall);
            var skipped = Refused;
            // The transaction owns the staging directory now; it holds its journal and is not deleted here.
            _directory = null;
            Clear();
            // Installed versions changed, so the list is read again rather than only redrawn.
            var refresh = results.Any(r => r.Outcome is not (UpdateOutcome.AwaitingReload or UpdateOutcome.AwaitingDecision));
            if (results.Any(r => r.Outcome == UpdateOutcome.AwaitingDecision))
            {
                return new(InstallStep.AwaitingDecision, Refresh: refresh);
            }

            return new(InstallStep.Installed, PluginManagerViewModel.UpdateSummary(results, host.IsRestartPending, skipped),
                PluginManagerViewModel.AnyUpdateFailed(results) || skipped.Count > 0, refresh);
        }
        catch (OperationCanceledException)
        {
            Clear();
            return new(InstallStep.Canceled, "Download canceled. Addon files were not changed.");
        }
        catch (Exception ex)
        {
            // A refusal before the transaction leaves no journal; keeping the staged batch would lock the selection and
            // make every later Update retry it. Clear keeps a directory that already has a journal.
            Clear();
            return new(InstallStep.Failed, "Update failed: " + ex.Message, true);
        }
    }

    /// <summary>Drops the staged batch and its staging directory, unless an installation already journaled it.</summary>
    public void Clear()
    {
        if (_directory is not null && !File.Exists(Path.Combine(_directory, "journal.json")) && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        _directory = null;
        Staged = null;
        Refused = [];
        Request = null;
    }
}
#endif
