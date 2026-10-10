#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    public bool IsUpdatable => Plugin.UpdateUrl is not null || Plugin.LocalPackages > 0 || Update is not null;
    public bool HasUpdate => Update is not null;
    public bool CanToggle => !Plugin.Missing;
}
internal sealed record VersionOption(string Version, UpdateCandidate? Candidate, bool Installed, bool IsDowngrade)
{
    public bool IsLocal => Candidate?.Package is LocalZipPackageRef;
}
internal sealed class PluginManagerViewModel
{
    public List<PluginRow> Plugins { get; } = [];
    public List<UpdateRow> Updates { get; } = [];
    public bool TrustChangedSource { get; set; }
    public bool RequiresTrust => Updates.Any(r => r.Selected && r.Findings.Any(f => f.RequiresTrust));
    public bool CanApply => Selected.Count > 0 && (!RequiresTrust || TrustChangedSource);
    public bool CanRetry => Plugins.Any(p => p.Plugin.FailedAttempt is not null);
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
                if (candidate.SourceUrl is not null && target.UpdateUrl != candidate.SourceUrl)
                    messages.Add(new("source_changed", FindingSeverity.Error, "The plugin's update_url changed or was removed; check for updates again."));
                if (target.IsBlocked || target.StoreReadOnly) messages.Add(new("R17", FindingSeverity.Error, "Resolve local state with Retry failed first."));
                if (target.RecordedVersion is not null && target.RecordedVersion != target.InstalledVersion)
                    messages.Add(new("recorded_version", FindingSeverity.Info, "Last working version: " + target.RecordedVersion));
            }
            if (SemVer.TryParse(candidate.InstalledVersion, out var old) && SemVer.TryParse(candidate.NewVersion, out var next) && next.Major != old.Major)
                messages.Add(new("R14", FindingSeverity.Warning, "Major version change: your project code may need changes."));
            if (candidate.Slug == "ePlugin") messages.Add(new("restart", FindingSeverity.Info, "The editor will restart after this update."));
            var failure = cache?.FailedUpdates.GetValueOrDefault(candidate.Slug)?.LastOrDefault(f => SameVersion(f.Version, candidate.NewVersion));
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
            return new VersionOption(candidate.NewVersion, candidate, current, known && valid && version.CompareTo(installed) < 0);
        }).ToList();
        if (!options.Any(o => o.Installed)) options.Add(new(installedVersion, null, true, false));
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
        { SourceUrl: { } url } => "From the update site " + url,
        _ => null
    };

    /// <summary>Why the selected version cannot be installed, or null when it can.</summary>
    public static string? VersionChangeBlocked(PluginRow row, VersionOption option)
    {
        if (option.Installed) return "This version is installed.";
        if (row.Plugin.Missing || option.Candidate is null) return "The plugin folder is missing.";
        if (!row.Plugin.Enabled) return "Enable the plugin to change its version.";
        if (row.Plugin.FailedAttempt is not null) return "Resolve the plugin's failed state with Retry failed first.";
        if (option.IsDowngrade && row.Plugin.Kind == PluginKind.Framework)
            return "The ePlugin Framework cannot be downgraded: older releases cannot finish or recover the update that installs them.";
        return null;
    }

    public static string StatusText(PluginInfo plugin)
    {
        if (plugin.Missing) return "Missing (no plugin.cfg)";
        var text = plugin.Enabled ? "Enabled" : "Disabled";
        if (plugin.State is { } state && state != EEditorPluginState.Created) text += $" ({state})";
        if (plugin.FailedAttempt is { } attempt) text += $", needs retry: {attempt.Reason}";
        return text;
    }

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

    /// <summary>The details pane as BBCode. All plugin supplied text is escaped.</summary>
    /// <param name="reviewed">Findings of a staged package for an explicitly chosen version, awaiting confirmation.</param>
    public static string Describe(PluginRow row, IReadOnlyList<Finding>? reviewed = null, LicenseInfo? license = null,
        WelcomeEntry? welcome = null)
    {
        var plugin = row.Plugin; var text = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) text.Append($"[b]{label}:[/b] {Escape(value)}\n"); }
        void Link(string label, string? value)
        {
            if (value is null || !IsWebUrl(value)) Line(label, value);
            else text.Append($"[b]{label}:[/b] [url={value}]{Escape(value)}[/url]\n");
        }
        Line("Type", PluginCatalog.KindName(plugin.Kind));
        Line("Status", StatusText(plugin));
        Line("Version", plugin.Version);
        Line("Author", plugin.Author);
        text.Append(LicenseLine(license));
        text.Append(WelcomeLine(welcome));
        if (!string.IsNullOrWhiteSpace(plugin.Description)) text.Append('\n').Append(Escape(plugin.Description)).Append('\n');
        if (plugin.Error is not null) text.Append($"\n[color=#ff7070]{Escape(plugin.Error)}[/color]\n");

        text.Append("\n[b]Update[/b]\n");
        if (row.Update is { } update)
        {
            text.Append($"{Escape(update.Candidate.InstalledVersion)} → [color=#70e070]{Escape(update.Candidate.NewVersion)}[/color]\n");
            if (update.Candidate.Package is LocalZipPackageRef local) Line("Local package", local.Path);
            else Link("Update site", update.Candidate.SourceUrl);
            if (update.Candidate.ReleaseUrl is not null) Link("Release", update.Candidate.ReleaseUrl);
            foreach (var finding in update.Findings) text.Append($"[color={Color(finding.Severity)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
            if (update.Failed is { } failed) text.Append($"[color=#ff7070]Previously failed {failed.Utc:u}:[/color] {Escape(failed.Reason)}\n");
        }
        else if (plugin.UpdateUrl is not null) Link("Update site", plugin.UpdateUrl);
        else if (plugin.LocalPackages == 0) text.Append("Not updatable: plugin.cfg has no update_url and no local plugin directory holds a package of it.\n");
        if (plugin.LocalPackages > 0) Line("Local packages", plugin.LocalPackages.ToString());

        if (reviewed is { Count: > 0 })
        {
            text.Append("\n[b]Reviewed package[/b]\n");
            foreach (var finding in reviewed) text.Append($"[color={Color(finding.Severity)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
        }

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
            text.Append($"\n[b]{title}[/b]\n");
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
    internal static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 &&
        value!.IndexOfAny(['[', ']']) < 0;
    private static bool SameVersion(string left, string right) => SemVer.TryParse(left, out var l) && SemVer.TryParse(right, out var r) ? l.CompareTo(r) == 0 : left == right;
}
#endif
