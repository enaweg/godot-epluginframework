#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Welcomes;

namespace Enaweg.Plugin.Internal.Manager;

internal sealed class UpdateRow(UpdateCandidate candidate, IReadOnlyList<Finding> findings, FailedUpdate? failed)
{
    public UpdateCandidate Candidate { get; } = candidate;
    public List<Finding> Findings { get; } = findings.ToList();
    public FailedUpdate? Failed { get; } = failed;
    public bool HasError => Findings.Any(f => f.Severity == FindingSeverity.Error);
    public bool Selected { get; set; } = failed is null && !findings.Any(f => f.Severity == FindingSeverity.Error);
}
internal sealed class PluginRow(PluginInfo plugin, UpdateRow? update)
{
    public PluginInfo Plugin { get; } = plugin;
    public UpdateRow? Update { get; } = update;
    public bool IsEPlugin => Plugin.Kind is PluginKind.Framework or PluginKind.EPlugin;
    public bool IsUpdatable => Plugin.UpdateUrl is not null || Plugin.UpdateSite is not null || Plugin.Known is not null ||
                               Plugin.LocalPackages > 0 || Update is not null;
    public bool HasUpdate => Update is not null;
    /// <summary>Godot loads a GDExtension add-on whatever is enabled, so it has nothing to enable or disable.</summary>
    public bool CanToggle => !Plugin.Missing && Plugin.Kind != PluginKind.GDExtensionAddon;
    /// <summary>
    /// The plugin failed, a failed attempt of it waits for a decision under Needs attention, or Godot does not run one of its GDExtensions
    /// although it should.
    /// </summary>
    public bool NeedsAttention => Plugin.FailedAttempt is not null || Plugin.State == EEditorPluginState.Error ||
                                  Plugin.Extensions.Any(e => e.Problem is not null);
}
/// <summary>What the toolbar button of the ePlugin Manager shows with the color of its puzzle piece.</summary>
internal enum ManagerStatus { Normal, UpdatesAvailable, NeedsAttention }
/// <param name="ReplacesUnknown">The installed version is unknown (a GDExtension add-on ePlugin did not install).</param>
internal sealed record VersionOption(string Version, UpdateCandidate? Candidate, bool Installed, bool IsDowngrade, bool ReplacesUnknown = false)
{
    public bool IsLocal => Candidate?.Package is LocalZipPackageRef;
    /// <summary>The installed version offered again, e.g. new commits of a pinned git branch or to repair changed files.</summary>
    public bool IsReinstall => Installed && Candidate is not null;
    /// <summary>The text of the button and confirmation that install this version.</summary>
    public string Action => Installed ? "Reinstall" : IsDowngrade ? "Downgrade" : ReplacesUnknown ? "Install" : "Update";
}
internal sealed class PluginManagerViewModel
{
    public List<PluginRow> Plugins { get; } = [];
    public List<UpdateRow> Updates { get; } = [];
    public bool CanApply => Selected.Count > 0;
    /// <summary>
    /// A plugin that needs attention comes first; updates count only when they can be installed, so an update the
    /// project rules out neither turns the button green nor red.
    /// </summary>
    public ManagerStatus Status => Plugins.Any(p => p.NeedsAttention) ? ManagerStatus.NeedsAttention
        : Updates.Any(u => !u.HasError) ? ManagerStatus.UpdatesAvailable : ManagerStatus.Normal;
    public IReadOnlyList<UpdateCandidate> Selected => Updates.Where(r => r.Selected && !r.HasError).Select(r => r.Candidate).ToArray();
    public string OkText => $"Update {Selected.Count} plugin{(Selected.Count == 1 ? "" : "s")}";
    public PluginManagerViewModel(IReadOnlyList<PluginInfo> plugins, IReadOnlyList<UpdateCandidate> candidates,
        IReadOnlyList<PluginUpdateTarget> targets, UpdateCache? cache, Func<UpdateCandidate, IReadOnlyList<Finding>> findings)
    {
        foreach (var candidate in candidates)
        {
            var target = targets.FirstOrDefault(t => t.Slug == candidate.Slug);
            var messages = findings(candidate).ToList();
            if (target is null) messages.Add(new("disabled", FindingSeverity.Error, "Plugin is no longer enabled."));
            else
            {
                if (candidate.SourceUrl is not null && !target.UpdateUrls.Contains(candidate.SourceUrl))
                    messages.Add(new("source_changed", FindingSeverity.Error, "The plugin's update site changed or was removed; check for updates again."));
                if (target.IsBlocked || target.StoreReadOnly) messages.Add(new("R17", FindingSeverity.Error, "Resolve the plugin's problem under Needs attention first."));
                if (target.RecordedVersion is not null && !SemVer.Same(target.RecordedVersion, target.InstalledVersion))
                    messages.Add(new("recorded_version", FindingSeverity.Info, "Last working version: " + target.RecordedVersion));
            }
            if (SemVer.TryParse(candidate.InstalledVersion, out var old) && SemVer.TryParse(candidate.NewVersion, out var next) && next.Major != old.Major)
                messages.Add(new("R14", FindingSeverity.Warning, "Major version change: your project code may need changes."));
            if (candidate.Slug == "ePlugin") messages.Add(new("restart", FindingSeverity.Info, "The editor will restart after this update."));
            var failure = cache?.LastFailure(candidate);
            Updates.Add(new(candidate, messages, failure));
        }
        foreach (var plugin in plugins) Plugins.Add(new(plugin, Updates.FirstOrDefault(u => u.Candidate.Slug == plugin.Slug)));
        // An update known from the cache whose addon folder is gone is still listed, so its error can be read.
        foreach (var update in Updates.Where(u => plugins.All(p => p.Slug != u.Candidate.Slug)))
            Plugins.Add(new(new PluginInfo(update.Candidate.Slug, update.Candidate.PluginName, PluginKind.Unknown, false)
            { Version = update.Candidate.InstalledVersion, UpdateUrl = update.Candidate.SourceUrl, Missing = true }, update));
        Plugins.Sort((left, right) =>
        {
            var framework = (right.Plugin.Kind == PluginKind.Framework).CompareTo(left.Plugin.Kind == PluginKind.Framework);
            if (framework != 0) return framework;
            var name = string.Compare(left.Plugin.Name, right.Plugin.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : string.CompareOrdinal(left.Plugin.Slug, right.Plugin.Slug);
        });
    }
    public PluginRow? Find(string slug) => Plugins.FirstOrDefault(p => p.Plugin.Slug == slug);

    /// <summary>
    /// Leaves out the updates the user left out in <paramref name="previous"/>, the model this one replaces, so refreshing
    /// the list does not check them again. A newer version of a left-out plugin is offered checked.
    /// </summary>
    public void KeepUnselected(PluginManagerViewModel? previous)
    {
        if (previous is null) return;
        var unselected = previous.Updates.Where(u => !u.Selected).Select(u => (u.Candidate.Slug, u.Candidate.NewVersion)).ToHashSet();
        foreach (var update in Updates.Where(u => unselected.Contains((u.Candidate.Slug, u.Candidate.NewVersion)))) update.Selected = false;
    }

    /// <summary>The update state shown on the update icon of an updatable plugin; null for a plugin that is not.</summary>
    public static string? UpdateState(PluginRow row) =>
        row.Update is { } update ? $"Update available: {update.Candidate.InstalledVersion} → {update.Candidate.NewVersion}"
        : row.IsUpdatable ? "Updatable" : null;

    /// <summary>The published versions plus the installed one, newest first.</summary>
    public static IReadOnlyList<VersionOption> VersionOptions(string installedVersion, IReadOnlyList<UpdateCandidate> versions)
    {
        var known = SemVer.TryParse(installedVersion, out var installed);
        var options = versions.Select(candidate =>
        {
            var valid = SemVer.TryParse(candidate.NewVersion, out var version);
            var current = known && valid && version.CompareTo(installed) == 0;
            return new VersionOption(candidate.NewVersion, candidate, current, known && valid && version.CompareTo(installed) < 0,
                installedVersion.Length == 0);
        }).ToList();
        // A GDExtension add-on ePlugin did not install has no known version to list.
        if (!options.Any(o => o.Installed) && installedVersion.Length > 0) options.Add(new(installedVersion, null, true, false));
        return options.OrderByDescending(o => SemVer.TryParse(o.Version, out var v) ? v : default).ToArray();
    }

    /// <summary>The text of a version in the version choice, e.g. "2.1.0 (latest, local)".</summary>
    public static string VersionLabel(VersionOption option, bool latest)
    {
        var notes = new List<string>();
        if (option.Installed) notes.Add("installed");
        else if (latest) notes.Add("latest");
        if (option.IsLocal) notes.Add("local");
        return notes.Count == 0 ? option.Version : $"{option.Version} ({string.Join(", ", notes)})";
    }

    /// <summary>Where a listed version would be installed from.</summary>
    public static string? VersionSource(VersionOption option) => option.Candidate switch
    {
        { Package: LocalZipPackageRef local } => "Local package: " + local.Path,
        { SourceUrl: { } url, Package: GitPackageRef git } => $"From the update site {url}, commit {git.Commit[..Math.Min(git.Commit.Length, 10)]}",
        { SourceUrl: { } url } => "From the update site " + url,
        _ => null
    };

    /// <summary>Why the selected version cannot be installed, or null when it can.</summary>
    public static string? VersionChangeBlocked(PluginRow row, VersionOption option)
    {
        if (option.Installed && option.Candidate is null)
            return "This version is installed. Neither its update site nor a local directory offers it to reinstall.";
        if (row.Plugin.Missing || option.Candidate is null) return "The plugin folder is missing.";
        if (!row.Plugin.Enabled) return "Enable the plugin to change its version.";
        if (row.Plugin.FailedAttempt is not null) return "Resolve the plugin's problem under Needs attention first.";
        if (option.IsDowngrade && row.Plugin.Kind == PluginKind.Framework)
            return "The ePlugin Framework cannot be downgraded: older releases cannot finish or recover the update that installs them.";
        return null;
    }

    /// <summary>The installed version as the manager shows it: "unknown" for a GDExtension add-on ePlugin did not install.</summary>
    public static string VersionText(PluginInfo plugin) =>
        plugin.Version.Length == 0 && plugin.Kind == PluginKind.GDExtensionAddon ? "unknown" : plugin.Version;

    /// <summary>The heading of the details: the plugin's name followed by its installed version.</summary>
    public static string DetailsTitle(PluginInfo plugin) =>
        plugin.Version.Length > 0 ? $"{plugin.Name} {plugin.Version}"
        : plugin.Kind == PluginKind.GDExtensionAddon ? $"{plugin.Name} (version unknown)" : plugin.Name;

    /// <summary>The plugin as the list shows it: its name, followed by its author in parentheses when it names one.</summary>
    public static string ListName(PluginInfo plugin) =>
        string.IsNullOrWhiteSpace(plugin.Author) ? plugin.Name : $"{plugin.Name} ({plugin.Author.Trim()})";

    public static string StatusText(PluginInfo plugin)
    {
        if (plugin.Missing) return "Missing (no plugin.cfg)";
        var text = plugin.Kind == PluginKind.GDExtensionAddon ? "Always loaded (no plugin.cfg to enable)" : plugin.Enabled ? "Enabled" : "Disabled";
        if (plugin.State is { } state && state != EEditorPluginState.Created) text += $" ({state})";
        if (plugin.FailedAttempt is { } attempt) text += $", needs retry: {attempt.Reason}";
        return text;
    }

    /// <summary>
    /// When updates were checked and will be checked next, as the status line ends. <paramref name="next"/> comes from
    /// <see cref="UpdateScheduler.NextCheck"/>: a scheduled check runs only when the editor starts, so one that is due
    /// waits for the next start.
    /// </summary>
    public static string CheckSchedule(DateTimeOffset? last, DateTimeOffset? next, DateTimeOffset now, IFormatProvider culture) =>
        "Last checked: " + (last?.ToLocalTime().ToString("g", culture) ?? "never") + " · Next check: " +
        (next is not { } due ? "off" : due <= now ? "at the next editor start" : "after " + due.ToLocalTime().ToString("g", culture));

    /// <summary>The tooltip of <see cref="CheckSchedule"/>.</summary>
    public static string CheckScheduleHint(DateTimeOffset? next) => next is null
        ? "Scheduled update checks are off (Settings > General, eplugin/updates/check_enabled). Check for updates still checks now."
        : "The editor checks for updates when it starts and the last successful check is older than the hours between checks " +
          "(Settings > General, eplugin/updates/check_interval_hours). Check for updates checks now.";

    /// <summary>The meta of the license link in <see cref="Describe"/>, which opens the license dialog.</summary>
    public const string LicenseMeta = "eplugin-license";

    /// <summary>
    /// The license line of the details: a link that shows the license, with whether it was accepted when the plugin asks
    /// for it. A plugin without a license of its own falls back to its LICENSE file and shows it as missing without it.
    /// </summary>
    public static string LicenseLine(LicenseInfo? license)
    {
        if (license is null) return "";
        var entry = license.Entry;
        if (entry.Problem is not null)
            return !license.Required && entry.Source == PluginLicense.DefaultFile
                ? $"[b]License:[/b] missing (no {PluginLicense.DefaultFile} file in the plugin directory)\n"
                : $"[b]License:[/b] [color=#ff7070]{Escape(entry.Problem)}[/color]\n";
        var state = !license.Required ? ""
            : license.IsAccepted ? $" (accepted {license.Accepted!.AcceptedUtc.ToLocalTime():d}{(license.Accepted.Automatic ? " automatically" : "")})"
            : license.Accepted is not null ? " (not accepted yet, an earlier license was)"
            : " (not accepted yet)";
        return $"[b]License:[/b] [url={LicenseMeta}]{Escape(PluginLicense.Display(entry.Slug, entry.Source))}[/url]{state}\n";
    }

    /// <summary>The meta of the welcome page link in <see cref="Describe"/>, which opens the welcome dialog.</summary>
    public const string WelcomeMeta = "eplugin-welcome";

    /// <summary>The welcome page line of the details: a link that shows the page again; empty without a page.</summary>
    public static string WelcomeLine(WelcomeEntry? welcome) =>
        welcome is null ? ""
        : welcome.Problem is not null ? $"[b]Welcome page:[/b] [color=#ff7070]{Escape(welcome.Problem)}[/color]\n"
        : $"[b]Welcome page:[/b] [url={WelcomeMeta}]{Escape(PluginLicense.Display(welcome.Slug, welcome.Source))}[/url]\n";

    /// <summary>
    /// The status line after installing a batch: what was installed and, per group that was not, why. A group that failed
    /// names its log folder. <paramref name="refused"/> are the packages left out when the batch was staged.
    /// </summary>
    /// <param name="leftOut">The updates the user left out in the review.</param>
    public static string UpdateSummary(IReadOnlyList<UpdateGroupResult> results, bool restarting, IReadOnlyList<RefusedPackage>? refused = null,
        IReadOnlyList<UpdateReviewEntry>? leftOut = null)
    {
        static string Names(IEnumerable<ValidatedPackage> packages) => string.Join(", ", packages.Select(p => $"{p.Candidate.PluginName} {p.NewVersion}"));
        static string Reason(string? failure, string fallback) => (failure ?? fallback).TrimEnd('.');
        var parts = new List<string>();
        var installed = results.Where(r => r.Outcome is UpdateOutcome.Completed or UpdateOutcome.AwaitingReload).SelectMany(r => r.Packages).ToArray();
        if (installed.Length > 0) parts.Add($"Installed {Names(installed)}.");
        foreach (var result in results)
        {
            if (result.Outcome is null) parts.Add($"Skipped {Names(result.Packages)}: {Reason(result.Failure, "it was refused")}. Its files were not changed.");
            else if (result.Outcome == UpdateOutcome.RolledBack) parts.Add($"Rolled back {Names(result.Packages)}: {Reason(result.Failure, "the update failed")}. Logs: {result.Directory}");
            else if (result is { Outcome: UpdateOutcome.KeptWithErrors, KeptForProjectCode: true })
                parts.Add($"Kept {Names(result.Packages)}: {Reason(result.Failure, "the project's own code does not compile with it")}. " +
                          $"Needs attention lists the errors and can roll it back. Logs: {result.Directory}");
            else if (result.Outcome == UpdateOutcome.KeptWithErrors)
                parts.Add($"{Names(result.Packages)} needs recovery: {Reason(result.Failure, "Retry it under Needs attention in the ePlugin Manager")}. Logs: {result.Directory}");
        }
        if (refused is { Count: > 0 }) parts.Add(RefusedSummary(refused).TrimEnd());
        if (leftOut is { Count: > 0 })
            parts.Add($"Left out {string.Join(", ", leftOut.Select(e => $"{e.Candidate.PluginName} {e.NewVersion}"))}.");
        if (restarting)
            parts.Add(installed.Length switch
            {
                0 => "Restarting the editor to load the restored plugins...",
                1 => "Restarting the editor to load the update...",
                _ => "Restarting the editor to load the updates..."
            });
        return string.Join(" ", parts);
    }

    /// <summary>The packages left out of a staged batch and why, each followed by a space; empty when none was.</summary>
    public static string RefusedSummary(IReadOnlyList<RefusedPackage> refused) =>
        string.Concat(refused.Select(r => $"Skipped {r.Candidate.PluginName} {r.Candidate.NewVersion}: {r.Reason.TrimEnd('.')}. Its files were not changed. "));

    /// <summary>Whether any group of the batch was not installed.</summary>
    public static bool AnyUpdateFailed(IReadOnlyList<UpdateGroupResult> results) =>
        results.Any(r => r.Outcome is null or UpdateOutcome.RolledBack or UpdateOutcome.KeptWithErrors);

    /// <summary>The meta of the link in <see cref="Describe"/> that opens the Needs attention dialog at the plugin.</summary>
    public const string AttentionMeta = "eplugin-attention";

    /// <summary>Project errors listed in the details; the dialog and the build log have the rest.</summary>
    public const int ShownProjectErrors = 10;

    /// <summary>
    /// An update kept with build errors: which versions it replaced, why it was kept, and the errors left in the project's
    /// own code. The Needs attention dialog rolls it back.
    /// </summary>
    public static string KeptUpdateSection(UpdateJournal kept)
    {
        var text = new StringBuilder("\n[b]Kept update[/b]\n");
        foreach (var plugin in kept.Plugins) text.Append(CultureInfo.CurrentCulture, $"{Escape(plugin.Slug)} {Escape(plugin.OldVersion)} → [color=#ff7070]{Escape(plugin.NewVersion)}[/color]\n");
        text.Append(Escape((kept.Failure ?? "The project does not build with the new version").TrimEnd('.'))).Append(".\n");
        if (kept.ProjectErrors.Count > 0)
        {
            foreach (var error in kept.ProjectErrors.Take(ShownProjectErrors)) text.Append("[code]").Append(Escape(BuildDiagnostics.Format(error))).Append("[/code]\n");
            if (kept.ProjectErrors.Count > ShownProjectErrors)
                text.Append(CultureInfo.CurrentCulture, $"and {kept.ProjectErrors.Count - ShownProjectErrors} more, see the build log in {Escape(kept.Directory)}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The GDExtensions of a plugin: per <c>.gdextension</c> file whether Godot loaded it, whether it is reloadable, the
    /// platforms it has libraries for, and why Godot does not run it; empty without one.
    /// </summary>
    public static string ExtensionSection(IReadOnlyList<NativeExtensionStatus> extensions)
    {
        if (extensions.Count == 0) return "";
        var text = new StringBuilder("\n[b]GDExtension[/b]\n");
        foreach (var extension in extensions)
        {
            var state = extension.Config is null ? "" : !extension.ForThisPlatform ? "not for this platform"
                : extension.Loaded ? "loaded" : "[color=#ff7070]not loaded[/color]";
            var reload = extension.Config is null ? "" : extension.Config.Reloadable ? "reloadable" : "not reloadable";
            var details = string.Join(", ", new[] { state, reload }.Where(d => d.Length > 0));
            text.Append(CultureInfo.CurrentCulture, $"{Escape(extension.File)}{(details.Length > 0 ? ": " + details : "")}\n");
            if (extension.Platforms.Count > 0) text.Append("Platforms: ").Append(Escape(string.Join(", ", extension.Platforms))).Append('\n');
            if (extension.Problem is { } problem) text.Append(CultureInfo.CurrentCulture, $"[color=#ff7070]{Escape(problem)}[/color]\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The details pane as BBCode, below the heading (<see cref="DetailsTitle"/>) and location. All plugin supplied text
    /// is escaped.
    /// </summary>
    /// <param name="kept">The plugin's update kept with build errors, see <see cref="KeptUpdateSection"/>.</param>
    public static string Describe(PluginRow row, LicenseInfo? license = null, WelcomeEntry? welcome = null, UpdateJournal? kept = null)
    {
        var plugin = row.Plugin; var text = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) text.Append(CultureInfo.CurrentCulture, $"[b]{label}:[/b] {Escape(value)}\n"); }
        void Link(string label, string? value)
        {
            if (value is null || !IsWebUrl(value)) Line(label, value);
            else text.Append(CultureInfo.CurrentCulture, $"[b]{label}:[/b] [url={value}]{Escape(value)}[/url]\n");
        }
        if (row.NeedsAttention) text.Append(CultureInfo.CurrentCulture, $"[color=#ff7070]This plugin needs attention.[/color] [url={AttentionMeta}]See what happened and what you can do[/url]\n\n");
        if (!string.IsNullOrWhiteSpace(plugin.Description)) text.Append(Escape(plugin.Description.Trim())).Append("\n\n");
        Line("Type", PluginCatalog.KindName(plugin.Kind));
        Line("Status", StatusText(plugin));
        Line("Author", plugin.Author);
        // ePlugin's built-in list fills in what plugin.cfg does not set
        Link("Documentation", plugin.DocumentationUrl ?? plugin.Known?.DocumentationUrl);
        var source = plugin.SourceUrl ?? plugin.Known?.SourceUrl;
        Link("Source", source);
        if (plugin.Known?.WebsiteUrl is { } website && website != source) Link("Website", website);
        text.Append(LicenseLine(license));
        text.Append(WelcomeLine(welcome));
        if (plugin.Error is not null) text.Append(CultureInfo.CurrentCulture, $"\n[color=#ff7070]{Escape(plugin.Error)}[/color]\n");
        text.Append(ExtensionSection(plugin.Extensions));
        if (kept is not null) text.Append(KeptUpdateSection(kept));

        text.Append("\n[b]Update[/b]\n");
        if (row.Update is { } update)
        {
            text.Append(CultureInfo.CurrentCulture, $"{Escape(update.Candidate.InstalledVersion)} → [color=#70e070]{Escape(update.Candidate.NewVersion)}[/color]\n");
            if (update.Candidate.Package is LocalZipPackageRef local) Line("Local package", local.Path);
            else Link("Update site", update.Candidate.SourceUrl);
            if (update.Candidate.ReleaseUrl is not null) Link("Release", update.Candidate.ReleaseUrl);
            foreach (var finding in update.Findings) text.Append(CultureInfo.CurrentCulture, $"[color={Color(finding.Severity)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
            if (update.Failed is { } failed) text.Append(CultureInfo.CurrentCulture, $"[color=#ff7070]Previously failed {failed.Utc:u}:[/color] {Escape(failed.Reason)}\n");
        }
        else if ((plugin.UpdateSite ?? plugin.UpdateUrl ?? plugin.Known?.UpdateUrl) is { } site) Link("Update site", site);
        else if (plugin.Kind == PluginKind.GDExtensionAddon)
            text.Append("Not updatable: the add-on has no plugin.cfg to name an update site, the project sets none, and ePlugin's " +
                        "built-in list does not know it. Add an update site for it under Update sites.\n");
        else if (plugin.LocalPackages == 0)
            text.Append("Not updatable: plugin.cfg has no update_url, the project sets no update site, ePlugin's built-in list does " +
                        "not know it, and no local directory holds a package of it.\n");
        if (plugin.Kind == PluginKind.GDExtensionAddon && plugin.Version.Length == 0 && row.IsUpdatable)
            text.Append("ePlugin does not know the installed version, because the add-on has no plugin.cfg and ePlugin did not " +
                        "install it, so no update is offered. Install the latest version, which is preselected below, or choose another; " +
                        "from then on, updates are found.\n");
        if (plugin.UpdateSite is not null)
        {
            var replaced = plugin.UpdateUrl is not null && plugin.UpdateUrl != plugin.UpdateSite;
            text.Append(replaced ? "The project sets this update site. It replaces plugin.cfg's update_url, which is used when it does not work:\n"
                : "The project sets this update site.\n");
            if (replaced) Link("plugin.cfg update_url", plugin.UpdateUrl);
        }
        if (plugin.Known is { } known)
        {
            if (plugin.UpdateSite is null && plugin.UpdateUrl is null)
                text.Append("This update site comes from ePlugin's built-in list.\n");
            else if (known.UpdateUrl != plugin.UpdateSite && known.UpdateUrl != plugin.UpdateUrl)
                Link("Built-in update site", known.UpdateUrl);
        }
        if (plugin.LocalPackages > 0) Line("Local packages", plugin.LocalPackages.ToString(CultureInfo.CurrentCulture));

        if (plugin.Recipe is { } recipe)
        {
            var hard = recipe.PluginDependencies.Where(d => d.Slug != "ePlugin").Select(d => d.Slug + Version(d.Version)).ToArray();
            List("Plugin dependencies", hard);
            List("Optional dependencies", recipe.OptionalPluginDependencies.Select(d => d.Slug + Version(d.Version)));
            List("NuGet packages", recipe.Nugets.Select(n => n.Name + Version(n.Version)));
            List("Projects", recipe.Projects.Select(p => p.Path));
            List("Autoloads", recipe.Autoloads.Select(a => a.Name));
            List("Managed directories", recipe.Directories);
        }
        return text.ToString().TrimEnd();

        void List(string title, IEnumerable<string> items)
        {
            var values = items.ToArray();
            if (values.Length == 0) return;
            text.Append(CultureInfo.CurrentCulture, $"\n[b]{title}[/b]\n");
            foreach (var value in values) text.Append("• ").Append(Escape(value)).Append('\n');
        }
    }
    private static string Version(string? version) => string.IsNullOrWhiteSpace(version) ? "" : " " + version;
    private static string Color(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => "#ff7070", FindingSeverity.Warning => "#ffd070", _ => "#a0c0ff"
    };
    internal static string Escape(string value) => value.Replace("[", "[lb]");
    /// <summary>An http(s) URL without credentials that can be embedded in a BBCode url tag and opened in a browser.</summary>
    /// <summary>
    /// Scales the <c>[font_size]</c> of a plugin's BBCode page by the editor scale. Its sizes are pixels written for 100%,
    /// while the default font of the dialog is scaled, so a heading would otherwise be smaller than the text at 200%.
    /// </summary>
    internal static string ScaleFontSizes(string bbcode, float scale) => Math.Abs(scale - 1f) < 0.01f ? bbcode
        : FontSize.Replace(bbcode, m => $"[font_size={Math.Max(1, (int)Math.Round(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * scale))}]");
    private static readonly Regex FontSize = new(@"\[font_size=(\d{1,4})\]", RegexOptions.CultureInvariant);

    internal static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 &&
        value!.IndexOfAny(['[', ']']) < 0;
}
#endif
