#if TOOLS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>The update workflows of the ePlugin Manager reach the framework through <see cref="IUpdateWorkflowHost"/>.</summary>
internal sealed partial class EGlobal : IUpdateWorkflowHost
{
    bool IUpdateWorkflowHost.IsRestartPending => IsRestartPending;

    Task<UpdateCheckResult> IUpdateWorkflowHost.CheckForUpdatesAsync(bool force) => CheckForUpdatesAsync(force);

    Task<IReadOnlyList<UpdateCandidate>> IUpdateWorkflowHost.ListRemoteVersionsAsync(PluginUpdateTarget target,
        CancellationToken ct) => ListRemoteVersionsAsync(target, ct);

    Task<(IReadOnlyList<ValidatedPackage> Packages, IReadOnlyList<RefusedPackage> Refused, string Directory)>
        IUpdateWorkflowHost.StageUpdatesAsync(IReadOnlyList<UpdateCandidate> candidates, IProgress<double>? progress,
            bool allowDowngrade, bool allowReinstall, CancellationToken ct) =>
        StageUpdatesAsync(candidates, progress, ct, allowDowngrade, allowReinstall);

    LicenseReview? IUpdateWorkflowHost.ReviewUpdateLicenses(IEnumerable<ValidatedPackage> packages) => ReviewUpdateLicenses(packages);

    void IUpdateWorkflowHost.RecordLicenses(LicenseReview review) => RecordLicenses(review);

    Task<IReadOnlyList<UpdateGroupResult>> IUpdateWorkflowHost.ApplyUpdatesAsync(IReadOnlyList<ValidatedPackage> packages,
        string directory, bool trustChangedSources, bool askOnBuildFailure) =>
        ApplyUpdatesAsync(packages, directory, trustChangedSources, askOnBuildFailure);
}
#endif
