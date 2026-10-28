#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Welcomes;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The decision dialogs of the ePlugin Manager without an editor: what they show for their model and what each button
/// decides. Signals are emitted directly, as the dialogs are never shown here.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ManagerDialogTests
{
    [TestCase]
    public static void TheBuildFailureDialogDecidesOnceAndRollsBackWhenClosed()
    {
        var decisions = new List<bool>();
        var dialog = UpdateFailureDialog.Create(FailedJournal(), decisions.Add);
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).StartsWith("tool 1.0.0 → 2.0.0\nThe project does not compile");
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).Contains("The errors are in plugin code.");
            // without a build log, the journal's failure is all there is to show
            Assertions.AssertString(dialog.GetNode<TextEdit>("%BuildOutput").Text).IsEqual("Final build failed.");
            Assertions.AssertString(dialog.GetNode<Label>("%LogLocation").TooltipText).IsEqual("/updates/tool");
            Assertions.AssertArray(ButtonTexts(dialog)).Contains("Keep new version", "Open build log");

            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "keep");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            Assertions.AssertThat(decisions).ContainsExactly(true);
        }
        finally { Free(dialog); }

        decisions.Clear();
        foreach (var signal in new[] { AcceptDialog.SignalName.Confirmed, AcceptDialog.SignalName.Canceled, Window.SignalName.CloseRequested })
        {
            dialog = UpdateFailureDialog.Create(FailedJournal(), decisions.Add);
            try { dialog.EmitSignal(signal); }
            finally { Free(dialog); }
        }
        Assertions.AssertThat(decisions).ContainsExactly(false, false, false);
    }

    [TestCase]
    public static void TheBuildFailureDialogGoesWithAnAssemblyReloadWithoutDeciding()
    {
        var decisions = new List<bool>();
        var dialog = UpdateFailureDialog.Create(FailedJournal(), decisions.Add);
        try
        {
            // its buttons would decide nothing after the reload, which asks again
            dialog.OnBeforeSerialize();

            Assertions.AssertBool(dialog.IsQueuedForDeletion()).IsTrue();
            Assertions.AssertThat(decisions).IsEmpty();
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void ADecisionThatThrowsStillReleasesTheDialog()
    {
        // left valid, the hidden dialog would keep welcome pages from opening for the rest of the session
        var dialog = UpdateFailureDialog.Create(FailedJournal(), _ => throw new InvalidOperationException("Rollback failed."));
        try
        {
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            Assertions.AssertBool(dialog.IsQueuedForDeletion()).IsTrue();
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheBuildFailureDialogShowsTheErrorsOfTheBuildLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journal = FailedJournal();
            journal.Directory = directory;
            journal.Builds.Add(new JournalBuild("final", 1, "final.log"));
            File.WriteAllLines(Path.Combine(directory, "final.log"),
                ["Build started", .. Enumerable.Range(1, 20).Select(i => $"Plugin.cs({i},1): error CS0103: missing {i}"), "Build FAILED."]);

            var dialog = UpdateFailureDialog.Create(journal, _ => { });
            try
            {
                var output = dialog.GetNode<TextEdit>("%BuildOutput").Text.Split('\n');
                Assertions.AssertInt(output.Length).IsEqual(15);
                Assertions.AssertString(output[0]).IsEqual("Plugin.cs(1,1): error CS0103: missing 1");
                Assertions.AssertString(dialog.GetNode<Label>("%LogLocation").TooltipText).IsEqual(Path.Combine(directory, "final.log"));
            }
            finally { Free(dialog); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCase]
    public static void TheBuildFailureDialogListsTheProjectsErrorsWhenOnlyProjectCodeFailed()
    {
        var journal = FailedJournal();
        journal.FailureCause = BuildFailureCause.ProjectCode;
        journal.PluginsBuildAlone = true;
        journal.ProjectErrors = [new BuildError("Game.cs", 3, 7, "CS0246", "Type not found"), new BuildError(null, 0, 0, "CS5001", "No entry point")];

        var dialog = UpdateFailureDialog.Create(journal, _ => { });
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).Contains("Only the project's own code does not compile");
            Assertions.AssertString(dialog.GetNode<TextEdit>("%BuildOutput").Text).IsEqual("Game.cs(3,7): CS0246 Type not found\nCS5001 No entry point");
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheWelcomeDialogListsSeveralPagesAndCountsThemAsReadOnceWhenClosed()
    {
        Assertions.AssertThrown(() => WelcomeDialog.Create([])).IsInstanceOf<ArgumentException>();

        var closed = new List<IReadOnlyList<WelcomeEntry>>();
        var dialog = WelcomeDialog.Create(
        [
            new WelcomeEntry("a", "Plugin A", "1.0.0", "README.md", "[b]Hello[/b]"),
            new WelcomeEntry("a", "Plugin A again", "1.0.0", "README.md", "duplicate"),
            new WelcomeEntry("b", "Plugin B", "", "README.md", "", "Cannot read [README.md]")
        ], closed.Add);
        try
        {
            var list = dialog.GetNode<ItemList>("%WelcomeList");
            Assertions.AssertInt(list.ItemCount).IsEqual(2);
            Assertions.AssertBool(list.Visible).IsTrue();
            Assertions.AssertString(dialog.GetNode<Label>("%WelcomeName").Text).IsEqual("Plugin A 1.0.0");
            Assertions.AssertString(dialog.GetNode<RichTextLabel>("%WelcomeText").Text).IsEqual("[b]Hello[/b]");

            list.EmitSignal(ItemList.SignalName.ItemSelected, 1);
            Assertions.AssertString(dialog.GetNode<Label>("%WelcomeName").Text).IsEqual("Plugin B");
            // a problem is plain text, shown in the error color
            Assertions.AssertString(dialog.GetNode<RichTextLabel>("%WelcomeText").Text).IsEqual("[color=#ff7070]Cannot read [lb]README.md][/color]");

            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            Assertions.AssertInt(closed.Count).IsEqual(1);
            Assertions.AssertThat(closed[0].Select(w => w.Slug).ToArray()).ContainsExactly("a", "b");
        }
        finally { Free(dialog); }

        // shown again from the manager's details: one page, nothing recorded
        dialog = WelcomeDialog.Create([new WelcomeEntry("a", "Plugin A", "1.0.0", "README.md", "Hello")]);
        try
        {
            Assertions.AssertString(dialog.Title).IsEqual("ePlugin — Welcome page");
            Assertions.AssertBool(dialog.GetNode<ItemList>("%WelcomeList").Visible).IsFalse();
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheCleanupSuggestionNamesTheDirectoryAndDecidesOnce()
    {
        var decisions = new List<bool>();
        var suggestion = new CleanupSuggestion("/home/user/plugins", 6 * LocalCleanup.Gigabyte + 300L * 1024 * 1024, 5 * LocalCleanup.Gigabyte);
        var dialog = CleanupSuggestionDialog.Create(suggestion, decisions.Add);
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%CleanupMessage").Text).IsEqual(
                "The plugin packages in your local directory /home/user/plugins take 6.3 GB, more than the 5 GB above which you want to clean it up. Clean it up now?");
            Assertions.AssertString(dialog.OkButtonText).IsEqual("Clean up...");
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Assertions.AssertThat(decisions.ToArray()).ContainsExactly(false);
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheCleanupSuggestionGoesWithAnAssemblyReloadWithoutDeciding()
    {
        var decisions = new List<bool>();
        var dialog = CleanupSuggestionDialog.Create(new CleanupSuggestion("/home/user/plugins", 6 * LocalCleanup.Gigabyte, 5 * LocalCleanup.Gigabyte), decisions.Add);
        try
        {
            // its buttons would do nothing after the reload
            dialog.OnBeforeSerialize();

            Assertions.AssertBool(dialog.IsQueuedForDeletion()).IsTrue();
            Assertions.AssertThat(decisions).IsEmpty();
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheStartupFailureShowsTheErrorAndRetriesOnlyWhenAsked()
    {
        var retries = 0;
        var dialog = StartupFailureDialog.Create("The state file is locked.", "ePlugin Framework 1.0\n\nSystem.IO.IOException: The state file is locked.", () => retries++);
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%StartupMessage").Text).IsEqual("ePlugin could not start: The state file is locked.");
            Assertions.AssertString(dialog.GetNode<TextEdit>("%StartupDetails").Text).Contains("System.IO.IOException");
            Assertions.AssertBool(dialog.GetNode<TextEdit>("%StartupDetails").Editable).IsFalse();
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "retry");
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "retry");
            Assertions.AssertInt(retries).IsEqual(1);
        }
        finally { Free(dialog); }

        var closed = StartupFailureDialog.Create("x", "x", () => retries++);
        try
        {
            closed.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Assertions.AssertInt(retries).IsEqual(1);
        }
        finally { Free(closed); }
    }

    [TestCase]
    public static void TheLicenseDialogAsksForEachLicenseInTurn()
    {
        var review = new LicenseReview([License("dep"), License("main")],
            [new LicenseActivation("main", "Main", ["dep", "main"]), new LicenseActivation("dep", "Dep", ["dep"])]);
        LicenseReview? finished = null;
        var dialog = LicenseDialog.Create(review, r => finished = r);
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).StartsWith("Enabling Main, Dep needs these licenses to be accepted");
            Assertions.AssertString(dialog.GetNode<Label>("%LicenseName").Text).IsEqual("dep 1.0.0");
            Assertions.AssertString(dialog.GetNode<Label>("%NeededBy").Text).IsEqual("Needed to enable Main.");
            Assertions.AssertBool(Button(dialog, "Accept all").Visible).IsTrue();
            Assertions.AssertBool(dialog.GetOkButton().Disabled).IsFalse();

            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Assertions.AssertObject(review.DecisionOf("dep")).IsEqual(LicenseDecision.Accepted);
            Assertions.AssertString(dialog.GetNode<Label>("%LicenseName").Text).IsEqual("main 1.0.0");
            Assertions.AssertBool(dialog.GetNode<Label>("%NeededBy").Visible).IsFalse();
            Assertions.AssertBool(Button(dialog, "Accept all").Visible).IsFalse();
            // a decided license cannot be decided again
            dialog.GetNode<ItemList>("%LicenseList").EmitSignal(ItemList.SignalName.ItemSelected, 0);
            Assertions.AssertBool(dialog.GetOkButton().Disabled).IsTrue();
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "decline");
            Assertions.AssertObject(finished).IsNull();

            dialog.GetNode<ItemList>("%LicenseList").EmitSignal(ItemList.SignalName.ItemSelected, 1);
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "decline");
            Assertions.AssertObject(finished).IsSame(review);
            Assertions.AssertObject(review.DecisionOf("main")).IsEqual(LicenseDecision.Declined);
            Assertions.AssertThat(review.Approved.Select(a => a.Slug).ToArray()).ContainsExactly("dep");
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheLicenseDialogAcceptsAllOrDeclinesTheRestWhenClosed()
    {
        var all = new LicenseReview([License("a"), License("b")], [], isUpdate: true);
        var dialog = LicenseDialog.Create(all, _ => { });
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).StartsWith("These updates come with licenses");
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "accept_all");
            Assertions.AssertThat(all.Accepted.Select(e => e.Slug).ToArray()).ContainsExactly("a", "b");
        }
        finally { Free(dialog); }

        var rest = new LicenseReview([License("a"), License("b")], [new LicenseActivation("a", "A", ["a"]), new LicenseActivation("b", "B", ["b"], Enabled: true)]);
        dialog = LicenseDialog.Create(rest, _ => { });
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).StartsWith("Accept the licenses below.");
            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            Assertions.AssertObject(rest.DecisionOf("a")).IsEqual(LicenseDecision.Accepted);
            Assertions.AssertObject(rest.DecisionOf("b")).IsEqual(LicenseDecision.Declined);
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheLicenseDialogSummarizesWhyItAsks()
    {
        string Summary(LicenseReview review)
        {
            var dialog = LicenseDialog.Create(review, _ => { });
            try { return dialog.GetNode<Label>("%Summary").Text; }
            finally { Free(dialog); }
        }

        Assertions.AssertString(Summary(new LicenseReview([License("a")], [], isUpdate: true))).StartsWith("The update of a comes with a license");
        Assertions.AssertString(Summary(new LicenseReview([License("a")], [new LicenseActivation("a", "a", ["a"], Enabled: true)])))
            .StartsWith("a is enabled, but its license is not accepted");
        Assertions.AssertString(Summary(new LicenseReview([License("a"), License("b")],
                [new LicenseActivation("a", "a", ["a"], Enabled: true), new LicenseActivation("b", "b", ["b"], Enabled: true)])))
            .StartsWith("These plugins are enabled");
        Assertions.AssertString(Summary(new LicenseReview([License("a")], [new LicenseActivation("a", "a", ["a"])])))
            .IsEqual("a asks you to accept its license before it is enabled. If you decline, it stays disabled.");
    }

    [TestCase]
    public static void TheLicenseViewerOnlyShowsTheLicenseAndWhetherItWasAccepted()
    {
        string Viewed(LicenseInfo license, Action<LicenseDialog>? check = null)
        {
            var dialog = LicenseDialog.CreateViewer(license);
            try
            {
                Assertions.AssertString(dialog.Title).IsEqual("ePlugin — License");
                Assertions.AssertString(dialog.OkButtonText).IsEqual("Close");
                Assertions.AssertBool(ButtonTexts(dialog).Contains("Decline")).IsFalse();
                check?.Invoke(dialog);
                return dialog.GetNode<Label>("%Summary").Text;
            }
            finally { Free(dialog); }
        }

        var entry = License("a");
        Assertions.AssertString(Viewed(new LicenseInfo(entry, false, null))).IsEqual("a does not ask for its license to be accepted.");
        Assertions.AssertString(Viewed(new LicenseInfo(entry, true, null))).Contains("is not accepted yet. It is asked for when the plugin is enabled.");
        Assertions.AssertString(Viewed(new LicenseInfo(entry, true, new AcceptedLicense("a", "OLD", "0.9.0", DateTimeOffset.UtcNow, false))))
            .Contains("an earlier one was");
        Assertions.AssertString(Viewed(new LicenseInfo(entry, true, new AcceptedLicense("a", entry.Source, "1.0.0", DateTimeOffset.UtcNow, true))))
            .Contains("automatically because " + LicenseSettings.AutoAcceptKey);
        Assertions.AssertString(Viewed(new LicenseInfo(entry with { Problem = "Missing" }, true, null),
            dialog => Assertions.AssertString(dialog.GetNode<RichTextLabel>("%LicenseText").Text).IsEqual("[color=#ff7070]Missing[/color]"))).IsNotEmpty();
    }

    [TestCase]
    public static void TheUpdateReviewShowsAChangedHostFirstAndInstallsOnlyWhenConfirmed()
    {
        var staged = new[]
        {
            Package("major", new Finding("R14", FindingSeverity.Warning, "Major version change")),
            Package("moved", new Finding("R9", FindingSeverity.Warning, "Update source changed", RequiresTrust: true))
        };
        var results = new List<bool>();
        var review = UpdateReview.For(staged, [])!;
        var dialog = UpdateReviewDialog.Create(review, results.Add);
        try
        {
            Assertions.AssertString(dialog.GetNode<Label>("%Summary").Text).IsEqual(review.Summary);
            var tree = dialog.GetNode<Tree>("%ReviewTree");
            var items = tree.GetRoot().GetChildren();
            Assertions.AssertThat(items.Select(i => i.GetText(1)).ToArray()).ContainsExactly("major", "moved");
            Assertions.AssertThat(items.Select(i => i.IsChecked(0)).ToArray()).ContainsExactly(true, false);
            Assertions.AssertString(dialog.GetNode<Label>("%ReviewName").Text).IsEqual("moved");
            Assertions.AssertString(dialog.GetOkButton().Text).IsEqual(review.OkText);
            Assertions.AssertBool(dialog.GetOkButton().Disabled).IsFalse();

            items[0].Select(1);
            tree.EmitSignal(Tree.SignalName.ItemSelected);
            Assertions.AssertString(dialog.GetNode<Label>("%ReviewName").Text).IsEqual("major");
            Assertions.AssertString(dialog.GetNode<RichTextLabel>("%ReviewText").Text).Contains("Major version change");

            dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            dialog.EmitSignal(AcceptDialog.SignalName.Canceled);
            Assertions.AssertThat(results).ContainsExactly(true);
        }
        finally { Free(dialog); }

        // nothing to install: OK is disabled and closing cancels
        results.Clear();
        review = UpdateReview.For([], [new RefusedPackage(Package("broken").Candidate, "contains a GDExtension")])!;
        dialog = UpdateReviewDialog.Create(review, results.Add);
        try
        {
            Assertions.AssertBool(dialog.GetOkButton().Disabled).IsTrue();
            Assertions.AssertBool(dialog.GetNode<Tree>("%ReviewTree").GetRoot().GetChild(0).IsEditable(0)).IsFalse();
            dialog.EmitSignal(Window.SignalName.CloseRequested);
            Assertions.AssertThat(results).ContainsExactly(false);
        }
        finally { Free(dialog); }
    }

    [TestCase]
    public static void TheAttentionDialogOffersTheDecisionsOfTheSelectedPlugin()
    {
        var dialog = GD.Load<PackedScene>(AttentionDialog.ScenePath).Instantiate<AttentionDialog>();
        // in the tree, so the rollback confirmation can pop up
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(dialog);
        try
        {
            dialog.Initialize();
            var requested = new List<string>();
            dialog.Requested += (action, item) => requested.Add($"{action} {item?.Slug}");
            var kept = new UpdateJournal { Directory = "/updates/kept" };
            kept.Plugins.Add(new() { Slug = "kept", OldVersion = "1.0.0", NewVersion = "2.0.0" });
            var items = new[]
            {
                new AttentionItem(Row(Plugin("failed") with { FailedAttempt = Attempt("failed"), State = EEditorPluginState.Error }), null, null, ["dependency"]),
                new AttentionItem(Row(Plugin("kept") with { FailedAttempt = Attempt("kept", "update_kept_build_failed") }), kept, kept, [])
            };

            dialog.Update(items);
            var list = dialog.GetNode<ItemList>("%AttentionList");
            Assertions.AssertInt(list.ItemCount).IsEqual(2);
            Assertions.AssertString(list.GetItemTooltip(1)).IsEqual("The update was kept although the project does not build with it.");
            Assertions.AssertString(dialog.GetNode<RichTextLabel>("%AttentionText").Text).IsEqual(items[0].Describe());
            Assertions.AssertString(Visible(dialog)).IsEqual("retry,disable,folder");
            Assertions.AssertString(dialog.GetNode<Button>("%RetryPluginButton").TooltipText).IsEqual("Also retries dependency.");
            Assertions.AssertBool(Button(dialog, "Retry all").Visible).IsTrue();

            dialog.GetNode<Button>("%RetryPluginButton").EmitSignal(BaseButton.SignalName.Pressed);
            dialog.GetNode<Button>("%DisablePluginButton").EmitSignal(BaseButton.SignalName.Pressed);
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "retry_all");

            list.EmitSignal(ItemList.SignalName.ItemSelected, 1);
            Assertions.AssertString(Visible(dialog)).IsEqual("retry,rollback,log,folder");
            var confirm = dialog.GetNode<ConfirmationDialog>("%RollbackConfirm");
            // confirmed without being asked, it rolls back nothing
            confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
            dialog.GetNode<Button>("%RollbackButton").EmitSignal(BaseButton.SignalName.Pressed);
            confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Assertions.AssertThat(requested).ContainsExactly("Retry failed", "Disable failed", "RetryAll ", "Rollback kept");

            // the list changed while the confirmation was open: the update it named is no longer the one selected
            requested.Clear();
            var other = new UpdateJournal { Id = "other", Directory = "/updates/other" };
            other.Plugins.Add(new() { Slug = "other", OldVersion = "1.0.0", NewVersion = "2.0.0" });
            dialog.GetNode<Button>("%RollbackButton").EmitSignal(BaseButton.SignalName.Pressed);
            dialog.Update([new AttentionItem(Row(Plugin("other") with { FailedAttempt = Attempt("other", "update_kept_build_failed") }), other, other, [])]);
            confirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Assertions.AssertThat(requested).IsEmpty();
            dialog.Update(items);
            list.EmitSignal(ItemList.SignalName.ItemSelected, 1);

            // while the manager carries out a decision, nothing else can be decided
            requested.Clear();
            dialog.SetBusy(true);
            Assertions.AssertBool(dialog.GetNode<Button>("%RetryPluginButton").Disabled).IsTrue();
            dialog.GetNode<Button>("%RetryPluginButton").EmitSignal(BaseButton.SignalName.Pressed);
            dialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "retry_all");
            Assertions.AssertThat(requested).IsEmpty();
            dialog.SetBusy(false);

            // the list follows what is left; the selection stays on its plugin
            dialog.Update([items[1]]);
            Assertions.AssertInt(list.ItemCount).IsEqual(1);
            Assertions.AssertBool(Button(dialog, "Retry all").Visible).IsFalse();
            Assertions.AssertString(dialog.GetNode<Label>("%AttentionName").Text).IsEqual(PluginManagerViewModel.DetailsTitle(items[1].Plugin));
            dialog.Update([]);
            Assertions.AssertBool(dialog.Visible).IsFalse();
        }
        finally { Free(dialog); }
    }

    private static string Visible(AttentionDialog dialog) =>
        string.Join(",", new[] { ("retry", "%RetryPluginButton"), ("rollback", "%RollbackButton"), ("disable", "%DisablePluginButton"), ("log", "%OpenLogButton"), ("folder", "%OpenPluginFolderButton") }
            .Where(b => dialog.GetNode<Button>(b.Item2).Visible).Select(b => b.Item1));

    private static UpdateJournal FailedJournal()
    {
        var journal = new UpdateJournal { Directory = "/updates/tool", Failure = "Final build failed.", FailureCause = BuildFailureCause.PluginCode };
        journal.Plugins.Add(new() { Slug = "tool", OldVersion = "1.0.0", NewVersion = "2.0.0" });
        return journal;
    }

    private static LicenseEntry License(string slug) => new(slug, slug, "1.0.0", "LICENSE", $"License of {slug}");

    private static PluginRow Row(PluginInfo plugin) => new(plugin, null);

    private static PluginInfo Plugin(string slug) => new(slug, slug, PluginKind.EPlugin, true) { Version = "1.0.0" };

    private static LocalPluginAttempt Attempt(string slug, string reason = "recipe_operation_failed") =>
        new(Guid.NewGuid(), slug, "1.0.0", PersistedPluginState.Activated, PersistedPluginState.Failed, reason);

    private static ValidatedPackage Package(string slug, params Finding[] findings)
    {
        Assertions.AssertBool(SemVer.TryParse("2.0.0", out var version)).IsTrue();
        return new(new UpdateCandidate(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null,
            new ZipPackageRef("https://example.org/plugin.zip", slug)), "/staging/" + slug, version, null, false, findings);
    }

    /// <summary>The buttons a dialog added next to OK, which are internal children of the window.</summary>
    private static IEnumerable<Button> Buttons(Node node) =>
        node.GetChildren(true).SelectMany(child => child is Button button ? Buttons(child).Prepend(button) : Buttons(child));

    private static string[] ButtonTexts(Node node) => Buttons(node).Select(b => b.Text).ToArray();

    private static Button Button(Node node, string text) => Buttons(node).Single(b => b.Text == text);

    /// <summary>Frees a dialog now, also one its own code queued for deletion, so the test leaves no orphan nodes.</summary>
    private static void Free(Node node)
    {
        if (GodotObject.IsInstanceValid(node)) node.Free();
    }
}
#endif
