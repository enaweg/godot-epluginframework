#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Enaweg.Plugin.Internal.Update;

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
    public bool IsUpdatable => Plugin.UpdateUrl is not null || Update is not null;
    public bool HasUpdate => Update is not null;
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
            if (target is null) messages.Add(new("disabled", FindingSeverity.Error, "Plugin is no longer enabled or its update URL was removed."));
            else
            {
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

    public static string StatusText(PluginInfo plugin)
    {
        if (plugin.Missing) return "Missing (no plugin.cfg)";
        var text = plugin.Enabled ? "Enabled" : "Disabled";
        if (plugin.State is { } state && state != EEditorPluginState.Created) text += $" ({state})";
        if (plugin.FailedAttempt is { } attempt) text += $", needs retry: {attempt.Reason}";
        return text;
    }

    /// <summary>The details pane as BBCode. All plugin supplied text is escaped.</summary>
    public static string Describe(PluginRow row)
    {
        var plugin = row.Plugin; var text = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) text.Append($"[b]{label}:[/b] {Escape(value)}\n"); }
        Line("Type", PluginCatalog.KindName(plugin.Kind));
        Line("Status", StatusText(plugin));
        Line("Version", plugin.Version);
        Line("Author", plugin.Author);
        Line("Folder", "res://addons/" + plugin.Slug);
        if (!string.IsNullOrWhiteSpace(plugin.Description)) text.Append('\n').Append(Escape(plugin.Description)).Append('\n');
        if (plugin.Error is not null) text.Append($"\n[color=#ff7070]{Escape(plugin.Error)}[/color]\n");

        text.Append("\n[b]Update[/b]\n");
        if (row.Update is { } update)
        {
            text.Append($"{Escape(update.Candidate.InstalledVersion)} → [color=#70e070]{Escape(update.Candidate.NewVersion)}[/color]\n");
            Line("Source", update.Candidate.SourceUrl);
            if (update.Candidate.ReleaseUrl is not null) Line("Release", update.Candidate.ReleaseUrl);
            foreach (var finding in update.Findings) text.Append($"[color={Color(finding.Severity)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
            if (update.Failed is { } failed) text.Append($"[color=#ff7070]Previously failed {failed.Utc:u}:[/color] {Escape(failed.Reason)}\n");
        }
        else if (plugin.UpdateUrl is not null) { text.Append("No update known.\n"); Line("Source", plugin.UpdateUrl); }
        else text.Append("Not updatable: plugin.cfg has no update_url.\n");

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
    private static bool SameVersion(string left, string right) => SemVer.TryParse(left, out var l) && SemVer.TryParse(right, out var r) ? l.CompareTo(r) == 0 : left == right;
}
#endif
