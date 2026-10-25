#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>An update the Review updates dialog lists, and whether it is installed.</summary>
internal sealed class UpdateReviewEntry
{
    public UpdateReviewEntry(ValidatedPackage package)
    {
        Package = package; Candidate = package.Candidate; NewVersion = package.NewVersion.ToString(); Findings = package.Findings;
        // Another repository is never trusted without the user's explicit choice.
        Install = !NeedsTrust;
    }

    public UpdateReviewEntry(RefusedPackage refused)
    {
        Candidate = refused.Candidate; NewVersion = refused.Candidate.NewVersion; Findings = []; RefusedReason = refused.Reason;
    }

    public ValidatedPackage? Package { get; }
    public UpdateCandidate Candidate { get; }
    public string Slug => Candidate.Slug;
    public string NewVersion { get; }
    public IReadOnlyList<Finding> Findings { get; }
    /// <summary>Why the package cannot be installed; null for a package that can.</summary>
    public string? RefusedReason { get; }
    public bool CanInstall => RefusedReason is null;
    /// <summary>
    /// The package comes from, or moves the plugin's updates to, another repository than the plugin's update site, which
    /// only the user can trust.
    /// </summary>
    public bool NeedsTrust => Findings.Any(f => f.RequiresTrust);
    public bool Install { get; set; }
    /// <summary>The text next to the entry's check box.</summary>
    public string Decision => !CanInstall ? "Refused" : NeedsTrust ? "Trust and install" : "Install";
}

/// <summary>
/// The packages of a staged batch the user decides about before anything is installed: those with warnings, those that
/// come from or move to another repository, and the refused ones. The others are installed without being listed.
/// </summary>
internal sealed class UpdateReview
{
    private UpdateReview(IReadOnlyList<UpdateReviewEntry> entries, int unlisted, bool versionInstall)
    {
        Entries = entries; Unlisted = unlisted; VersionInstall = versionInstall;
    }

    public IReadOnlyList<UpdateReviewEntry> Entries { get; }
    /// <summary>Staged packages without warnings, installed with the chosen ones.</summary>
    public int Unlisted { get; }
    /// <summary>The batch is one version chosen in a plugin's details rather than the checked updates.</summary>
    public bool VersionInstall { get; }
    public int InstallCount => Unlisted + Entries.Count(e => e.Install);
    /// <summary>Entries the user left out although they could be installed.</summary>
    public IReadOnlyList<UpdateReviewEntry> LeftOut => Entries.Where(e => e.CanInstall && !e.Install).ToArray();

    /// <summary>The review of a staged batch; null when no package needs a decision.</summary>
    public static UpdateReview? For(IReadOnlyList<ValidatedPackage> staged, IReadOnlyList<RefusedPackage> refused, bool versionInstall = false)
    {
        var listed = staged.Where(p => p.Findings.Any(f => f.Severity == FindingSeverity.Warning || f.RequiresTrust)).ToArray();
        if (listed.Length == 0 && refused.Count == 0) return null;
        var entries = listed.Select(p => new UpdateReviewEntry(p)).Concat(refused.Select(r => new UpdateReviewEntry(r)))
            .OrderBy(e => e.Candidate.PluginName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(entries, staged.Count - listed.Length, versionInstall);
    }

    /// <summary>The staged packages to install: the unlisted ones and the entries the user kept.</summary>
    public IReadOnlyList<ValidatedPackage> Selected(IReadOnlyList<ValidatedPackage> staged)
    {
        var left = Entries.Where(e => !e.Install).Select(e => e.Slug).ToHashSet(StringComparer.Ordinal);
        return staged.Where(p => !left.Contains(p.Candidate.Slug)).ToArray();
    }

    public string OkText => InstallCount == 0 ? "Install" : VersionInstall ? "Install version"
        : $"Install {InstallCount} update{(InstallCount == 1 ? "" : "s")}";

    public string Summary
    {
        get
        {
            var text = VersionInstall
                ? "The chosen version needs your decision before it is installed."
                : "These updates need your decision before they are installed. Uncheck an update to leave it out.";
            if (Entries.Any(e => e.NeedsTrust))
                text += " An update that comes from or moves to another repository is installed only if you check it.";
            if (Unlisted > 0)
                text += $" {Unlisted} other update{(Unlisted == 1 ? " has" : "s have")} no warnings and {(Unlisted == 1 ? "is" : "are")} installed too.";
            return text;
        }
    }

    /// <summary>An entry's version change, findings and source as BBCode. All package supplied text is escaped.</summary>
    public static string Describe(UpdateReviewEntry entry)
    {
        var candidate = entry.Candidate;
        var text = new StringBuilder();
        text.Append(CultureInfo.CurrentCulture, $"{Escape(candidate.InstalledVersion)} → {Escape(entry.NewVersion)}\n");
        if (candidate.Package is LocalZipPackageRef local) text.Append(CultureInfo.CurrentCulture, $"[b]Local package:[/b] {Escape(local.Path)}\n");
        else if (candidate.SourceUrl is { } source) text.Append(CultureInfo.CurrentCulture, $"[b]Update site:[/b] {Link(source)}\n");
        if (candidate.ReleaseUrl is { } release) text.Append(CultureInfo.CurrentCulture, $"[b]Release:[/b] {Link(release)}\n");
        if (entry.RefusedReason is { } reason)
            text.Append(CultureInfo.CurrentCulture, $"\n[color={TextColors.Error}]Refused:[/color] {Escape(reason)}\nIt is not installed; its files are not changed.\n");
        if (entry.Findings.Count > 0) text.Append('\n');
        foreach (var finding in entry.Findings)
            text.Append(CultureInfo.CurrentCulture, $"[color={Color(finding.Severity)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
        if (!entry.NeedsTrust) return text.ToString().TrimEnd();
        if (entry.Findings.Any(f => f.Code == "R9" && f.RequiresTrust) && entry.Package is { NewUpdateUrl: var url } && string.IsNullOrWhiteSpace(url))
            text.Append("\n[b]The plugin no longer names its update site.[/b] ")
                .Append("Later updates come from the project's update site, if it sets one, or from ePlugin's built-in list, which matches add-ons by folder name only. ");
        else if (entry.Findings.Any(f => f.Code == "R9" && f.RequiresTrust))
            text.Append(CultureInfo.CurrentCulture, $"\n[b]The plugin's updates move to {Link(entry.Package?.NewUpdateUrl ?? "another repository")}.[/b] ")
                .Append("Later updates are downloaded from there. ");
        else
            text.Append(CultureInfo.CurrentCulture, $"\n[b]This update comes from {Link(candidate.SourceUrl ?? "another repository")}, not from the plugin's update site.[/b] ");
        text.Append("Check this update only if you trust that source, for example because the plugin's author announced the move.\n");
        return text.ToString().TrimEnd();
    }

    private static string Link(string url) => PluginManagerViewModel.IsWebUrl(url) ? $"[url={url}]{Escape(url)}[/url]" : Escape(url);
    private static string Escape(string value) => PluginManagerViewModel.Escape(value);
    private static string Color(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => TextColors.Error, FindingSeverity.Warning => TextColors.Warning, _ => TextColors.Info
    };
}
#endif
