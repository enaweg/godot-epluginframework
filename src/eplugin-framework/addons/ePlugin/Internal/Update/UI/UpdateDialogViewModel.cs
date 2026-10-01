#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update.UI;

internal sealed class UpdateRow(UpdateCandidate candidate, IReadOnlyList<Finding> findings, FailedUpdate? failed)
{
    public UpdateCandidate Candidate { get; } = candidate;
    public List<Finding> Findings { get; } = findings.ToList();
    public FailedUpdate? Failed { get; } = failed;
    public bool HasError => Findings.Any(f => f.Severity == FindingSeverity.Error);
    public bool Selected { get; set; } = failed is null && !findings.Any(f => f.Severity == FindingSeverity.Error);
}
internal sealed class UpdateDialogViewModel
{
    public List<UpdateRow> Rows { get; } = [];
    public bool TrustChangedSource { get; set; }
    public bool RequiresTrust => Rows.Any(r => r.Selected && r.Findings.Any(f => f.RequiresTrust));
    public bool CanApply => Selected.Count > 0 && (!RequiresTrust || TrustChangedSource);
    public IReadOnlyList<UpdateCandidate> Selected => Rows.Where(r => r.Selected && !r.HasError).Select(r => r.Candidate).ToArray();
    public string OkText => $"Update {Selected.Count} plugin{(Selected.Count == 1 ? "" : "s")}";
    public UpdateDialogViewModel(IReadOnlyList<UpdateCandidate> candidates, IReadOnlyList<PluginUpdateTarget> targets, UpdateCache? cache,
        Func<UpdateCandidate, IReadOnlyList<Finding>> findings)
    {
        foreach (var candidate in candidates)
        {
            var target = targets.FirstOrDefault(t => t.Slug == candidate.Slug);
            var messages = findings(candidate).ToList();
            if (target is null) messages.Add(new("disabled", FindingSeverity.Error, "Plugin is no longer enabled or its update URL was removed."));
            else
            {
                if (target.IsBlocked || target.StoreReadOnly) messages.Add(new("R17", FindingSeverity.Error, "Resolve local state with Retry failed ePlugin addons first."));
                if (target.RecordedVersion is not null && target.RecordedVersion != target.InstalledVersion)
                    messages.Add(new("recorded_version", FindingSeverity.Info, "Last working version: " + target.RecordedVersion));
            }
            if (SemVer.TryParse(candidate.InstalledVersion, out var old) && SemVer.TryParse(candidate.NewVersion, out var next) && next.Major != old.Major)
                messages.Add(new("R14", FindingSeverity.Warning, "Major version change: your project code may need changes."));
            if (candidate.Slug == "ePlugin") messages.Add(new("restart", FindingSeverity.Info, "The editor will restart after this update."));
            var failure = cache?.FailedUpdates.GetValueOrDefault(candidate.Slug)?.LastOrDefault(f => SameVersion(f.Version, candidate.NewVersion));
            Rows.Add(new(candidate, messages, failure));
        }
    }
    private static bool SameVersion(string left, string right) => SemVer.TryParse(left, out var l) && SemVer.TryParse(right, out var r) ? l.CompareTo(r) == 0 : left == right;
}
#endif
