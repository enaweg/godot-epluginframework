#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Kinds;
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

    /// <summary>Extracts and validates the add-on of a ZIP file that is not in the project yet.</summary>
    Task<(ValidatedPackage Package, string Directory)> StageNewAddonAsync(AddonArchive archive, CancellationToken ct);

    AddonInstallResult InstallNewAddon(ValidatedPackage package, string directory);
}

/// <summary>What to install: the checked updates, or one version chosen in a plugin's details.</summary>
internal sealed record InstallRequest(IReadOnlyList<UpdateCandidate> Batch, bool VersionInstall = false,
    bool AllowDowngrade = false, bool AllowReinstall = false);

internal enum InstallStep
{
    /// <summary>The updates were installed; their results are in the notice.</summary>
    Installed,
    /// <summary>A build failed and the update failure dialog takes over the decision.</summary>
    AwaitingDecision,
    /// <summary>The download or the review was canceled, or every update was left out; addon files were not changed.</summary>
    Canceled,
    /// <summary>Staging or installing failed before an outcome; addon files were not changed unless a journal says so.</summary>
    Failed,
    /// <summary>The manager went away meanwhile; nothing is shown.</summary>
    Abandoned,
    /// <summary>A new add-on was staged from a ZIP file and waits for the user to confirm its installation.</summary>
    Staged
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
    /// <summary>Addon files are about to be replaced; the manager can no longer be closed.</summary>
    public Action Swapping { get; init; } = () => { };
}

/// <summary>
/// The update workflows of the ePlugin Manager without its UI: checking for updates, loading a plugin's published
/// versions, and staging, reviewing and installing a batch. The dialog shows what they report.
/// </summary>
/// <param name="reviewUpdates">
/// Shows the Review updates dialog and completes once the user decided: true to install what the review selects, false
/// to cancel the whole batch.
/// </param>
/// <param name="reviewLicenses">Shows the license dialog and completes once every license was decided.</param>
/// <param name="beginProgress">Starts a progress scope in ePlugin's progress window.</param>
internal sealed class UpdateFlow(IUpdateWorkflowHost host, Func<UpdateReview, Task<bool>> reviewUpdates,
    Func<LicenseReview, Task<LicenseReview>> reviewLicenses, Func<string, IEPluginProgressScope> beginProgress)
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

    /// <summary>The batch being installed; null when none is.</summary>
    public InstallRequest? Request { get; private set; }

    /// <summary>The validated packages of the batch being installed; null before they are staged.</summary>
    public IReadOnlyList<ValidatedPackage>? Staged { get; private set; }

    /// <summary>Packages of the staged batch that were refused; the others are installed without them.</summary>
    public IReadOnlyList<RefusedPackage> Refused { get; private set; } = [];

    /// <summary>The new add-on staged from a ZIP file, waiting for the user's confirmation; null when there is none.</summary>
    public ValidatedPackage? StagedAddon { get; private set; }

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
    /// Stages and validates the batch, has the user review packages with warnings, a changed update host or a refusal,
    /// asks for changed licenses, then installs.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(InstallRequest request, InstallView view, CancellationToken ct)
    {
        Request = request;
        try
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

            // Warnings, a changed host and refusals discovered inside the packages are decided before the first project
            // side effect.
            IReadOnlyList<UpdateReviewEntry> leftOut = [];
            var selected = Staged.Where(p => request.Batch.Any(c => c.Slug == p.Candidate.Slug)).ToArray();
            if (UpdateReview.For(selected, Refused, request.VersionInstall) is { } review)
            {
                view.Status("Review the updates.");
                var install = await reviewUpdates(review);
                if (!view.Alive())
                {
                    Clear();
                    return new(InstallStep.Abandoned);
                }

                if (!install)
                {
                    Clear();
                    return new(InstallStep.Canceled, "Update canceled. Addon files were not changed.");
                }

                selected = review.Selected(selected).ToArray();
                leftOut = review.LeftOut;
                if (selected.Length == 0)
                {
                    Clear();
                    return new(InstallStep.Canceled, "No update was installed: every update was left out or refused. Addon files were not changed.");
                }
            }

            ct.ThrowIfCancellationRequested();
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
            // The review installs a package from or moving to another repository only when the user checked it.
            var results = await host.ApplyUpdatesAsync(selected, _directory!, selected.Any(p => p.Findings.Any(f => f.RequiresTrust)),
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

            return new(InstallStep.Installed, PluginManagerViewModel.UpdateSummary(results, host.IsRestartPending, skipped, leftOut),
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

    /// <summary>
    /// Extracts and validates the add-on of a ZIP file that is not in the project yet. A package that can be installed
    /// stays staged as <see cref="StagedAddon"/> until <see cref="InstallAddon"/> or <see cref="Clear"/>.
    /// </summary>
    public async Task<InstallOutcome> StageAddonAsync(AddonArchive archive, InstallView view, CancellationToken ct)
    {
        Clear();
        var text = $"Extracting and validating {archive.Name}...";
        view.Status(text);
        try
        {
            (ValidatedPackage Package, string Directory) staged;
            using (beginProgress(text)) staged = await host.StageNewAddonAsync(archive, ct);
            _directory = staged.Directory;
            if (!view.Alive())
            {
                Clear();
                return new(InstallStep.Abandoned);
            }

            if (!staged.Package.IsValid)
            {
                var reasons = string.Join("; ", staged.Package.Findings.Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Message));
                Clear();
                return new(InstallStep.Failed, $"{archive.Name} cannot be installed: {reasons}", true);
            }

            StagedAddon = staged.Package;
            return new(InstallStep.Staged);
        }
        catch (OperationCanceledException)
        {
            Clear();
            return new(InstallStep.Canceled, "Installation canceled. Addon files were not changed.");
        }
        catch (Exception ex)
        {
            Clear();
            return new(InstallStep.Failed, $"{archive.Name} cannot be installed: {ex.Message}", true);
        }
    }

    /// <summary>Installs the confirmed <see cref="StagedAddon"/>; the plugin list has to be read again afterwards.</summary>
    public InstallOutcome InstallAddon()
    {
        var package = StagedAddon ?? throw new InvalidOperationException("No add-on is staged.");
        var candidate = package.Candidate;
        try
        {
            var result = host.InstallNewAddon(package, _directory!);
            // The staging folder holds the log of the failed build; the start that deletes abandoned ones removes it.
            if (result.BuildLog is not null) _directory = null;
            Clear();
            if (!result.Installed) return new(InstallStep.Failed, result.Failure, true, true);
            var version = candidate.NewVersion.Length == 0 ? "" : " " + candidate.NewVersion;
            return new(InstallStep.Installed, $"Installed {candidate.PluginName}{version} into res://addons/{candidate.Slug}." +
                (package.Kind == PluginKind.GDExtensionAddon ? "" : " Enable it with its On check box."), Refresh: true);
        }
        catch (Exception ex)
        {
            Clear();
            return new(InstallStep.Failed, $"Installing {candidate.PluginName} failed: {ex.Message}", true, true);
        }
    }

    /// <summary>
    /// Drops the staged batch and its staging directory, unless an installation already journaled it. A directory that
    /// cannot be deleted now, e.g. while a scanner holds a file open, is left to the start that deletes abandoned ones.
    /// </summary>
    public void Clear()
    {
        if (_directory is not null && !File.Exists(Path.Combine(_directory, "journal.json")) && Directory.Exists(_directory))
        {
            try { AddonFiles.DeleteTree(_directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        _directory = null;
        Staged = null;
        StagedAddon = null;
        Refused = [];
        Request = null;
    }
}
#endif
