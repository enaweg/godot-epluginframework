#if TOOLS
using System;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// What the Review updates and Needs attention dialogs list and offer: only the plugins that need a decision, each with
/// the decisions that apply to it.
/// </summary>
[TestSuite]
public class DecisionDialogTests
{
    [TestCase]
    public static void AReviewListsOnlyUpdatesThatNeedADecision()
    {
        Assertions.AssertObject(UpdateReview.For([Package("a")], [])).IsNull();
        // Info findings, e.g. the restart of a framework update, need no decision.
        Assertions.AssertObject(UpdateReview.For([Package("a", new Finding("R6", FindingSeverity.Info, "Reinstall"))], [])).IsNull();

        var review = UpdateReview.For(
            [Package("plain"), Package("major", new Finding("R14", FindingSeverity.Warning, "Major version change")),
             Package("moved", new Finding("R9", FindingSeverity.Warning, "Update source changed", RequiresTrust: true))],
            [new RefusedPackage(Candidate("broken"), "contains a GDExtension")])!;

        Assertions.AssertThat(review.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("broken", "major", "moved");
        Assertions.AssertInt(review.Unlisted).IsEqual(1);
        // a warning is installed unless left out, a changed host only when trusted, a refused package never
        Assertions.AssertThat(review.Entries.Select(e => (e.Slug, e.Install, e.Decision)).ToArray()).ContainsExactly(
            ("broken", false, "Refused"), ("major", true, "Install"), ("moved", false, "Trust and install"));
        Assertions.AssertString(review.OkText).IsEqual("Install 2 updates");
        Assertions.AssertString(review.Summary).Contains("1 other update has no warnings and is installed too.");
        Assertions.AssertString(review.Summary).Contains("installed only if you check it");
    }

    [TestCase]
    public static void TheReviewInstallsTheCheckedAndUnlistedPackages()
    {
        var staged = new[]
        {
            Package("plain"), Package("major", new Finding("R14", FindingSeverity.Warning, "Major version change")),
            Package("moved", new Finding("R9", FindingSeverity.Warning, "Update source changed", RequiresTrust: true))
        };
        var review = UpdateReview.For(staged, [])!;
        review.Entries.Single(e => e.Slug == "major").Install = false;
        review.Entries.Single(e => e.Slug == "moved").Install = true;

        Assertions.AssertThat(review.Selected(staged).Select(p => p.Candidate.Slug).ToArray()).ContainsExactly("plain", "moved");
        Assertions.AssertThat(review.LeftOut.Select(e => e.Slug).ToArray()).ContainsExactly("major");

        review.Entries.Single(e => e.Slug == "moved").Install = false;
        var single = UpdateReview.For([staged[1]], [], versionInstall: true)!;
        single.Entries[0].Install = false;
        Assertions.AssertInt(single.InstallCount).IsEqual(0);
        Assertions.AssertString(single.OkText).IsEqual("Install");
        single.Entries[0].Install = true;
        Assertions.AssertString(single.OkText).IsEqual("Install version");
    }

    [TestCase]
    public static void AReviewEntryShowsItsFindingsAndWhereUpdatesMoveTo()
    {
        var moved = Package("moved", new Finding("R9", FindingSeverity.Warning, "Update source changed: [new]", RequiresTrust: true)) with
        {
            NewUpdateUrl = "https://example.com/moved/releases"
        };
        var text = UpdateReview.Describe(UpdateReview.For([moved], [])!.Entries[0]);
        Assertions.AssertString(text).StartsWith("1.0.0 → 2.0.0\n[b]Update site:[/b] [url=https://example.org/releases]");
        Assertions.AssertString(text).Contains("[color=#ffd070]Warning:[/color] Update source changed: [lb]new]");
        Assertions.AssertString(text).Contains("move to [url=https://example.com/moved/releases]https://example.com/moved/releases[/url]");

        var refused = UpdateReview.Describe(UpdateReview.For([], [new RefusedPackage(Candidate("broken"), "contains a GDExtension")])!.Entries[0]);
        Assertions.AssertString(refused).Contains("[color=#ff7070]Refused:[/color] contains a GDExtension");
    }

    [TestCase]
    public static void OnlyPluginsThatNeedAttentionAreListedWithTheirDecisions()
    {
        var model = Model(
            Plugin("fine"),
            Plugin("failed") with { FailedAttempt = Attempt("failed", "recipe_operation_failed"), State = EEditorPluginState.Error, Error = "NuGet [x] failed" },
            Plugin("leaving") with { FailedAttempt = Attempt("leaving", "recipe_operation_failed", PersistedPluginState.Deactivated) },
            Plugin("kept") with { FailedAttempt = Attempt("kept", "update_kept_build_failed") },
            Plugin("ePlugin", PluginKind.Framework) with { FailedAttempt = Attempt("ePlugin", "attempt_in_progress") },
            Plugin("native", PluginKind.GDExtension) with { Extensions = [new NativeExtensionStatus("native.gdextension", null, false, false)] });
        var kept = new UpdateJournal { Directory = "/updates/kept", Builds = [new JournalBuild("final", 1, "final.log")] };
        kept.Plugins.Add(new() { Slug = "kept", OldVersion = "1.0.0", NewVersion = "2.0.0" });

        var items = PluginAttention.Collect(model, slug => slug == "kept" ? kept : null, slug => slug == "kept" ? kept : null,
            slug => slug == "failed" ? ["dependency"] : []);

        Assertions.AssertThat(items.Select(i => i.Slug).ToArray()).ContainsExactly("ePlugin", "failed", "kept", "leaving", "native");
        string Actions(string slug)
        {
            var item = items.Single(i => i.Slug == slug);
            return string.Join(",", new[] { item.CanRetry ? "retry" : "", item.CanRollback ? "rollback" : "", item.CanDisable ? "disable" : "", item.Log is null ? "" : "log" }
                .Where(a => a.Length > 0));
        }
        Assertions.AssertString(Actions("failed")).IsEqual("retry,disable");
        // a failed deactivation is finished by Retry; the framework cannot be disabled here
        Assertions.AssertString(Actions("leaving")).IsEqual("retry");
        Assertions.AssertString(Actions("ePlugin")).IsEqual("retry");
        Assertions.AssertString(Actions("kept")).IsEqual("retry,rollback,log");
        Assertions.AssertString(Actions("native")).IsEqual("disable");
        Assertions.AssertString(items.Single(i => i.Slug == "kept").Log).IsEqual(PackageFiles.Inside("/updates/kept", "final.log"));

        var failed = items.Single(i => i.Slug == "failed").Describe();
        Assertions.AssertString(failed).StartsWith("Enabling failed: installing or removing what its recipe declares");
        Assertions.AssertString(failed).Contains("[color=#ff7070]NuGet [lb]x] failed[/color]");
        Assertions.AssertString(failed).Contains("This also retries dependency, which failed together with it or which it needs.");
        Assertions.AssertString(items.Single(i => i.Slug == "ePlugin").Describe()).StartsWith("Enabling the plugin was interrupted");
        Assertions.AssertString(items.Single(i => i.Slug == "kept").Describe()).Contains("[b]Kept update[/b]");
        Assertions.AssertString(items.Single(i => i.Slug == "native").Summary).IsEqual("The file cannot be read.");
    }

    [TestCase]
    public static void EachFailureReasonIsExplainedForTheDirectionItFailedIn()
    {
        string Summary(string reason, PersistedPluginState target = PersistedPluginState.Activated) =>
            new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = Attempt("a", reason, target) }, null), null, null, []).Summary;

        Assertions.AssertString(Summary("dependency_resolution_failed")).StartsWith("Enabling failed: a plugin it depends on");
        Assertions.AssertString(Summary("recipe_creation_failed", PersistedPluginState.Deactivated)).StartsWith("Disabling failed: the plugin's CreateRecipe");
        Assertions.AssertString(Summary("dependent_recipe_creation_failed")).Contains("CreateRecipe of a plugin that depends on it");
        Assertions.AssertString(Summary("solution_build_failed")).IsEqual("Enabling failed: the project did not build afterwards.");
        Assertions.AssertString(Summary("optional_recipe_failed")).Contains("recipe of an optional dependency");
        Assertions.AssertString(Summary("invalid_plugin_version")).StartsWith("plugin.cfg has no valid version");
        Assertions.AssertString(Summary("participant_blocked")).Contains("blocked by an earlier failure");
        Assertions.AssertString(Summary("cannot_record_participant")).Contains("could not be saved in eplugin-state.json");
        Assertions.AssertString(Summary("state_commit_failed")).Contains("could not be saved in eplugin-state.json");
        Assertions.AssertString(Summary("deactivation_failed")).IsEqual("Disabling the plugin failed.");
        Assertions.AssertString(Summary("update_commit_failed")).StartsWith("The update was installed, but recording");
        Assertions.AssertString(Summary("update_rollback_failed")).StartsWith("Rolling back a failed update");
        Assertions.AssertString(Summary("update_swap_failed")).IsEqual("The update failed (update_swap_failed).");
        Assertions.AssertString(Summary("something_new", PersistedPluginState.Deactivated)).IsEqual("Disabling failed (something_new).");
    }

    [TestCase]
    public static void AFailedUpdateIsDescribedByItsJournalAndAPluginWithoutDecisionsSaysWhatToDo()
    {
        var journal = new UpdateJournal { Directory = "/updates/failed", Failure = "Final build failed." };
        journal.Plugins.Add(new() { Slug = "a", OldVersion = "1.0.0", NewVersion = "[2.0.0]" });
        journal.Builds.Add(new JournalBuild("final", 1, "final.log"));
        journal.Builds.Add(new JournalBuild("final-plugins-only", 1, "plugins-only.log"));
        var update = new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = Attempt("a", "update_swap_failed") }, null), journal, null, []);

        var text = update.Describe();
        Assertions.AssertString(text).Contains("a 1.0.0 → [lb]2.0.0]\nFinal build failed.\n");
        Assertions.AssertString(text).Contains("• [b]Retry[/b]: finishes or rolls back the interrupted update.");
        Assertions.AssertString(text).Contains("• [b]Open log[/b]");
        // the log of the full build, not of the plugins-only build that followed it
        Assertions.AssertString(update.Log).IsEqual(PackageFiles.Inside("/updates/failed", "final.log"));
        Assertions.AssertBool(update.CanDisable).IsFalse();

        var leaving = new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = Attempt("a", "recipe_operation_failed", PersistedPluginState.Deactivated) }, null), null, null, []);
        Assertions.AssertString(leaving.Describe()).Contains("• [b]Retry[/b]: disables the plugin again, after you fixed the cause.");

        // a missing plugin in error can neither be retried nor disabled here
        var stuck = new AttentionItem(new PluginRow(Plugin("a") with { State = EEditorPluginState.Error, Missing = true }, null), null, null, []);
        Assertions.AssertString(stuck.Summary).IsEqual("The plugin is in an error state.");
        Assertions.AssertString(stuck.Describe()).EndsWith("Fix the cause, for example by restarting the editor or updating the plugin, then check the list again.");

        var running = new AttentionItem(new PluginRow(Plugin("a"), null), null, null, []);
        Assertions.AssertString(running.Summary).IsEqual("The plugin needs attention.");
        Assertions.AssertString(running.Describe()).Contains("• [b]Disable[/b]: turns the plugin off until the problem is fixed.");
    }

    [TestCase]
    public static void AnAttentionKeyChangesWithTheProblem()
    {
        var first = Attempt("a", "recipe_operation_failed");
        var item = new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = first }, null), null, null, []);
        var same = new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = first }, null), null, null, []);
        var again = new AttentionItem(new PluginRow(Plugin("a") with { FailedAttempt = Attempt("a", "recipe_operation_failed") }, null), null, null, []);
        Assertions.AssertString(item.Key).IsEqual(same.Key);
        Assertions.AssertString(item.Key).IsNotEqual(again.Key);
        Assertions.AssertString(PluginAttention.ButtonText(1)).IsEqual("1 plugin needs attention");
        Assertions.AssertString(PluginAttention.ButtonText(3)).IsEqual("3 plugins need attention");
    }

    private static PluginManagerViewModel Model(params PluginInfo[] plugins) => new(plugins, [], [], null, _ => []);

    private static LocalPluginAttempt Attempt(string slug, string reason, PersistedPluginState target = PersistedPluginState.Activated) =>
        new(Guid.NewGuid(), slug, "1.0.0", target, PersistedPluginState.Failed, reason);

    private static PluginInfo Plugin(string slug, PluginKind kind = PluginKind.EPlugin) =>
        new(slug, slug, kind, true) { Version = "1.0.0" };

    private static ValidatedPackage Package(string slug, params Finding[] findings)
    {
        Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
        return new(Candidate(slug), "/staging/" + slug, version, null, false, findings);
    }

    private static UpdateCandidate Candidate(string slug) =>
        new(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));
}
#endif
