#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// A plugin the Needs attention dialog lists: what went wrong and which decisions it offers. Built from a
/// <see cref="PluginRow"/> that <see cref="PluginRow.NeedsAttention"/>; pure, so it is tested without the editor.
/// </summary>
/// <param name="Journal">The journal of the update whose failed attempt blocks the plugin.</param>
/// <param name="Kept">The plugin's update kept with build errors, which can be rolled back.</param>
/// <param name="RetriedWith">The other plugins a retry of this one retries, see <see cref="EGlobal.RetryScope"/>.</param>
internal sealed record AttentionItem(PluginRow Row, UpdateJournal? Journal, UpdateJournal? Kept, IReadOnlyList<string> RetriedWith)
{
    public PluginInfo Plugin => Row.Plugin;
    public string Slug => Plugin.Slug;
    private LocalPluginAttempt? Attempt => Plugin.FailedAttempt;

    /// <summary>
    /// Identifies this problem of the plugin, so the ePlugin Manager opens the dialog by itself only for problems it did
    /// not show yet.
    /// </summary>
    public string Key => string.Join("|", new[] { Slug, Attempt?.AttemptId.ToString() ?? "", Plugin.State?.ToString() ?? "" }
        .Concat(Plugin.Extensions.Select(e => e.Problem ?? "")));

    public bool IsUpdate => Journal is not null || Attempt?.Reason.StartsWith("update_", StringComparison.Ordinal) == true;
    public bool CanRetry => Attempt is not null;
    public bool CanRollback => Kept is not null;

    /// <summary>
    /// Disabling gives up on an activation, or turns off a plugin that runs with a problem. A failed deactivation is
    /// finished by Retry, and a failed update is retried or rolled back.
    /// </summary>
    public bool CanDisable => !IsUpdate && Row.CanToggle && Plugin.Kind != PluginKind.Framework &&
                              (Attempt is { TargetState: PersistedPluginState.Activated } || Attempt is null && Plugin.Enabled);

    /// <summary>The build log of the failed update, or its journal folder when it has none; null for other problems.</summary>
    public string? Log => Journal is null ? null : FailedBuildLog(Journal) ?? Journal.Directory;

    /// <summary>The log of the full build that failed, not of the plugins-only build that followed it.</summary>
    public static string? FailedBuildLog(UpdateJournal journal) =>
        journal.Builds.LastOrDefault(b => !b.Name.EndsWith("-plugins-only", StringComparison.Ordinal))?.LogFile is { } name
            ? PackageFiles.Inside(journal.Directory, name)
            : null;

    /// <summary>One sentence about the problem, for the list's tooltip.</summary>
    public string Summary =>
        Kept is not null ? "The update was kept although the project does not build with it."
        : Attempt is { } attempt ? Reason(attempt)
        : Plugin.State == EEditorPluginState.Error ? "The plugin is in an error state."
        : Plugin.Extensions.FirstOrDefault(e => e.Problem is not null)?.Problem ?? "The plugin needs attention.";

    /// <summary>What went wrong and what each decision does, as BBCode. All plugin supplied text is escaped.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(Escape(Summary)).Append('\n');
        if (Plugin.Error is { } error) text.Append(CultureInfo.CurrentCulture, $"[color=#ff7070]{Escape(error)}[/color]\n");
        if (Kept is not null) text.Append(PluginManagerViewModel.KeptUpdateSection(Kept));
        else if (Journal is not null)
        {
            text.Append('\n');
            foreach (var plugin in Journal.Plugins) text.Append(CultureInfo.CurrentCulture, $"{Escape(plugin.Slug)} {Escape(plugin.OldVersion)} → {Escape(plugin.NewVersion)}\n");
            if (Journal.Failure is { } failure) text.Append(Escape(failure.TrimEnd('.'))).Append(".\n");
        }
        text.Append(PluginManagerViewModel.ExtensionSection(Plugin.Extensions));

        text.Append("\n[b]What you can do[/b]\n");
        if (CanRetry)
        {
            text.Append(CultureInfo.CurrentCulture, $"• [b]Retry[/b]: {RetryText()}");
            if (RetriedWith.Count > 0)
                text.Append(CultureInfo.CurrentCulture, $" This also retries {Escape(string.Join(", ", RetriedWith))}, which failed together with it or which it needs.");
            text.Append('\n');
        }
        if (CanRollback) text.Append("• [b]Roll back[/b]: restores the versions the update replaced, so the project builds again.\n");
        if (CanDisable)
            text.Append(Attempt is null
                ? "• [b]Disable[/b]: turns the plugin off until the problem is fixed.\n"
                : "• [b]Disable[/b]: gives up on enabling it, removes what it installed and leaves it disabled.\n");
        if (Log is not null) text.Append("• [b]Open log[/b]: shows the build output of the update.\n");
        if (!CanRetry && !CanRollback && !CanDisable)
            text.Append("Fix the cause, for example by restarting the editor or updating the plugin, then check the list again.\n");
        return text.ToString().TrimEnd();
    }

    private string RetryText() =>
        Kept is not null ? "once you fixed the project's code, finishes the update."
        : IsUpdate ? "finishes or rolls back the interrupted update."
        : Attempt!.TargetState == PersistedPluginState.Activated ? "enables the plugin again, after you fixed the cause."
        : "disables the plugin again, after you fixed the cause.";

    private static string Reason(LocalPluginAttempt attempt)
    {
        var verb = attempt.TargetState == PersistedPluginState.Activated ? "Enabling" : "Disabling";
        return attempt.Reason switch
        {
            "attempt_in_progress" => $"{verb} the plugin was interrupted, for example by an editor crash or a restart.",
            "dependency_resolution_failed" => $"{verb} failed: a plugin it depends on is missing or has a version that does not match.",
            "recipe_creation_failed" => $"{verb} failed: the plugin's CreateRecipe threw an exception.",
            "dependent_recipe_creation_failed" => $"{verb} failed: CreateRecipe of a plugin that depends on it threw an exception.",
            "recipe_operation_failed" => $"{verb} failed: installing or removing what its recipe declares (NuGet packages, projects, autoloads, directories) failed.",
            "solution_build_failed" => $"{verb} failed: the project did not build afterwards.",
            "optional_recipe_failed" => $"{verb} failed: applying or removing the recipe of an optional dependency failed.",
            "invalid_plugin_version" => "plugin.cfg has no valid version. Add one before you retry.",
            "participant_blocked" => $"{verb} failed: a plugin changed together with it is blocked by an earlier failure.",
            "cannot_record_participant" or "state_commit_failed" => $"{verb} failed: its state could not be saved in eplugin-state.json.",
            "deactivation_failed" => "Disabling the plugin failed.",
            "update_kept_build_failed" => "The update was kept although the project does not build with it.",
            "update_commit_failed" => "The update was installed, but recording its new version failed.",
            "update_rollback_failed" => "Rolling back a failed update did not finish.",
            var other when other.StartsWith("update_", StringComparison.Ordinal) => $"The update failed ({other}).",
            var other => $"{verb} failed ({other})."
        };
    }

    private static string Escape(string value) => PluginManagerViewModel.Escape(value);
}

internal static class PluginAttention
{
    /// <summary>The plugins of <paramref name="model"/> that need attention, in the list's order.</summary>
    /// <param name="journal">The journal of the update whose failed attempt blocks a plugin.</param>
    /// <param name="kept">A plugin's update kept with build errors.</param>
    /// <param name="retriedWith">The other plugins a retry of a plugin retries.</param>
    public static IReadOnlyList<AttentionItem> Collect(PluginManagerViewModel model, Func<string, UpdateJournal?> journal,
        Func<string, UpdateJournal?> kept, Func<string, IReadOnlyList<string>> retriedWith) =>
        model.Plugins.Where(r => r.NeedsAttention)
            .Select(r => new AttentionItem(r, journal(r.Plugin.Slug), kept(r.Plugin.Slug),
                r.Plugin.FailedAttempt is null ? [] : retriedWith(r.Plugin.Slug)))
            .ToArray();

    /// <summary>The text of the ePlugin Manager's button that opens the dialog.</summary>
    public static string ButtonText(int count) => $"{count} plugin{(count == 1 ? "" : "s")} need{(count == 1 ? "s" : "")} attention";
}
#endif
