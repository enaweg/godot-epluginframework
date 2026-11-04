#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private LicenseReview? _pendingLicenseReview;
    private ulong _pendingLicenseFrame;

    /// <summary>The license review waiting for its dialog, for tests; <see cref="TakeLicenseReview"/> hands it out.</summary>
    internal LicenseReview? PendingLicenseReview => _pendingLicenseReview;

    private bool LicenseUiAvailable => _ePluginContext is not null && _host.CanShowDialogs(_ePluginContext);

    private string FrameworkSlug => _ePluginContext?.GetPluginSlug() ?? "ePlugin";

    /// <summary>
    /// Asks for the licenses a plugin needs before it is installed. Returns false when the activation has to wait: the
    /// plugin is then disabled again and enabled once its licenses are accepted.
    /// </summary>
    private bool PassesLicenseGate(PluginContext context)
    {
        if (_stateStore is null)
        {
            return true; // EGlobal's existing headless tests do not initialize the editor.
        }

        var pending = AcceptAutomatically(CollectActivationLicenses(context.Slug));
        if (pending.Count == 0)
        {
            return true;
        }

        var names = string.Join(", ", pending.Select(e => e.Name));
        if (_toCheckEnable.Count > 0)
        {
            // A dependency whose license was not reviewed with the plugin that enables it, because its recipe could
            // not be read beforehand. The waiting chain cannot pause for a dialog, so it fails like a missing dependency.
            var reason = UnreadableReason(pending) is { } unreadable
                ? $"{context.Name} was not enabled: {unreadable}. {PluginLicense.UnreadableAdvice}"
                : $"The license of {names} has to be accepted first. Enable {context.Name} on its own to review it, then retry it under Needs attention in the ePlugin Manager.";
            context.Logger?.Error(reason);
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = new InvalidOperationException(reason);
            _toCheckEnable.Push(context);
            FailAllUncheckedPluginsAndRefresh(reason);
            return false;
        }

        if (!LicenseUiAvailable)
        {
            context.Logger?.Error(NotEnabledWithoutDialog(context.Name, pending));
        }
        else
        {
            context.Logger?.Log($"{context.Name} waits for the license of {names} to be accepted.");
            QueueLicenseReview(new LicenseReview(pending,
                [new LicenseActivation(context.Slug, context.Name, pending.Select(e => e.Slug).ToArray())]));
        }

        // Godot is still running this plugin's _EnablePlugin. Disabling it right away would free the plugin inside its
        // own callback. Deactivated makes the DisableEPlugin it triggers a no-op, as nothing was installed.
        context.State = EEditorPluginState.Deactivated;
        Post(() =>
        {
            if (_host.IsPluginEnabled(context.Slug))
            {
                _host.SetPluginEnabled(context.Slug, false);
            }

            _contexts.Remove(context);
        });
        return false;
    }

    /// <summary>
    /// The licenses not accepted yet of a plugin and of every disabled hard dependency that enabling it would enable,
    /// dependencies first.
    /// </summary>
    internal IReadOnlyList<LicenseEntry> CollectActivationLicenses(string slug)
    {
        if (_stateStore is null)
        {
            return [];
        }

        var pending = new List<LicenseEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string current)
        {
            if (!visited.Add(current))
            {
                return;
            }

            var recipe = RecipeOf(current);
            foreach (var dependency in recipe?.PluginDependencies ?? [])
            {
                if (!IsDependencyAvailable(dependency.Slug))
                {
                    Visit(dependency.Slug);
                }
            }

            if (current != FrameworkSlug && ReadInstalledLicense(current, recipe) is { Required: true } license &&
                !_stateStore.IsLicenseAccepted(current, license.Entry.Source))
            {
                pending.Add(license.Entry);
            }
        }

        Visit(slug);
        return pending;
    }

    /// <summary>
    /// The recipe of an ePlugin: from its editor instance, or for a disabled ePlugin from a throw-away instance of its
    /// compiled script type. Null for other plugins and when the recipe cannot be created.
    /// </summary>
    private EEditorPluginRecipe? RecipeOf(string slug)
    {
        var context = ManagedContext(slug);
        if (context is null)
        {
            return ReadRecipeOfDisabledPlugin(slug);
        }

        if (context.IsRecipeCreated)
        {
            return context.Builder.PluginRecipe;
        }

        try
        {
            // a throw-away builder: a failing CreateRecipe must not leave a half-filled recipe on the context
            var builder = EEditorPluginBuilder.Create();
            context.Plugin!.CreateRecipe(builder);
            return builder.PluginRecipe;
        }
        catch (Exception)
        {
            return null; // the activation itself reports the recipe failure
        }
    }

    /// <summary>
    /// A disabled ePlugin has no editor instance, but its script type is compiled. A throw-away instance creates the
    /// recipe, which is deterministic and side-effect free.
    /// </summary>
    private EEditorPluginRecipe? ReadRecipeOfDisabledPlugin(string slug)
    {
        var builder = EEditorPluginBuilder.Create();
        try
        {
            return _host.CreateRecipeOfDisabledPlugin(slug, builder) ? builder.PluginRecipe : null;
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Warn($"Cannot read the recipe of {slug} to collect its license and dependencies: {ex.Message}");
            return null;
        }
    }

    private LicenseInfo? ReadInstalledLicense(string slug, EEditorPluginRecipe? recipe) =>
        ReadLicense(slug, recipe, $"{_host.PluginDirectory(slug)}/plugin.cfg", ProjectSettings.GlobalizePath(_host.PluginDirectory(slug)));

    /// <summary>
    /// The license of a plugin: the one its recipe sets, else the license_file of its plugin.cfg, else its LICENSE file.
    /// Null when plugin.cfg cannot be read.
    /// </summary>
    private static LicenseInfo? ReadLicense(string slug, EEditorPluginRecipe? recipe, string configPath, string pluginDirectory)
    {
        using var config = new ConfigFile();
        if (config.Load(configPath) != Error.Ok)
        {
            return null;
        }

        var requiredValue = config.GetValue("plugin", PluginLicense.RequiredKey, false);
        var required = requiredValue.VariantType == Variant.Type.Bool
            ? requiredValue.AsBool()
            : PluginLicense.IsTrue(requiredValue.AsString());
        string Value(string key) => config.GetValue("plugin", key, "").AsString();
        var name = Value("name");
        var version = Value("version");
        var entry = recipe?.PluginLicense switch
        {
            { Path: { } path } => PluginLicense.FromFile(slug, name, version, PluginLicense.SourceOfPath(slug, path),
                ProjectSettings.GlobalizePath(path)),
            { Text: { } text } => PluginLicense.FromText(slug, name, version, text),
            _ => PluginLicense.FromConfig(slug, name, version, Value(PluginLicense.FileKey), pluginDirectory)
        };
        return new LicenseInfo(entry, required, null);
    }

    /// <summary>The license the ePlugin Manager shows for a plugin, with what was accepted for it.</summary>
    internal LicenseInfo? DescribeLicense(string slug) =>
        (ReadInstalledLicense(slug, RecipeOf(slug)) ?? ExtensionAddonLicense(slug)) is { } license
            ? license with { Accepted = _stateStore?.GetLicense(slug) }
            : null;

    /// <summary>The LICENSE file of a GDExtension add-on, which has no plugin.cfg to name another or to require it.</summary>
    private LicenseInfo? ExtensionAddonLicense(string slug) => IsExtensionAddon(slug)
        ? new LicenseInfo(PluginLicense.FromConfig(slug, KnownPluginOf(slug)?.Name ?? slug, ExtensionAddonVersion(slug) ?? "", null,
            ProjectSettings.GlobalizePath(_host.PluginDirectory(slug))), false, null)
        : null;

    /// <summary>
    /// Accepts the licenses without asking when the project allows it. Returns those left to review: all of them without
    /// auto-accept, else those that cannot be read, which are never accepted.
    /// </summary>
    private IReadOnlyList<LicenseEntry> AcceptAutomatically(IReadOnlyList<LicenseEntry> entries)
    {
        if (!LicenseSettings.AutoAccept)
        {
            return entries;
        }

        var readable = entries.Where(e => e.Problem is null).ToArray();
        foreach (var entry in readable)
        {
            _ePluginContext?.Logger.Warn(
                $"Accepted the license of {entry.Name} ({PluginLicense.Display(entry.Slug, entry.Source)}) without showing it, because {LicenseSettings.AutoAcceptKey} is enabled.");
        }

        RecordLicenses(readable, automatic: true);
        return entries.Where(e => e.Problem is not null).ToArray();
    }

    /// <summary>Why the licenses cannot be accepted because some cannot be read; null when all of them can be.</summary>
    private static string? UnreadableReason(IReadOnlyList<LicenseEntry> entries) => new LicenseReview(entries, []).UnreadableReason;

    /// <summary>The error of a plugin a headless editor did not enable because of its licenses.</summary>
    private static string NotEnabledWithoutDialog(string name, IReadOnlyList<LicenseEntry> pending) =>
        UnreadableReason(pending) is { } unreadable
            ? $"{name} was not enabled: {unreadable}. {PluginLicense.UnreadableAdvice}"
            : $"{name} was not enabled: the license of {string.Join(", ", pending.Select(e => e.Name))} has to be accepted in the editor, or set {LicenseSettings.AutoAcceptKey}.";

    private void RecordLicenses(IEnumerable<LicenseEntry> entries, bool automatic)
    {
        var now = DateTimeOffset.UtcNow;
        // a license nobody could read is never recorded, also not by a caller that missed it
        var accepted = entries.Where(e => e.Problem is null)
            .Select(e => new AcceptedLicense(e.Slug, e.Source, e.Version, now, automatic)).ToArray();
        if (_stateStore is null || accepted.Length == 0 || _stateStore.TryRecordLicenses(accepted))
        {
            return;
        }

        Stash(UnsavedLicensesMeta, accepted);
        _ePluginContext?.Logger.Error(
            "Accepted licenses could not be saved to addons/eplugin-state.json; they are asked for again after the editor restarts.");
    }

    // Licenses and welcome pages the state file could not save, kept in Engine metadata until it can: the store is
    // created anew by every assembly reload, which follows right after the build of an activation.
    private const string UnsavedLicensesMeta = "eplugin_unsaved_licenses";
    private const string UnsavedWelcomesMeta = "eplugin_unsaved_welcomes";

    private static void Stash<T>(string key, IEnumerable<T> entries) =>
        Engine.Singleton.SetMeta(key, JsonSerializer.Serialize(Unstash<T>(key).Concat(entries).ToArray()));

    private static T[] Unstash<T>(string key)
    {
        if (!Engine.Singleton.HasMeta(key)) return [];
        try { return JsonSerializer.Deserialize<T[]>(Engine.Singleton.GetMeta(key).AsString()) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Records the licenses and welcome pages an earlier load of this assembly could not save, unless the file holds newer
    /// ones; those it still cannot save stay stashed. Called by Initialize, after the store is loaded.
    /// </summary>
    internal void RestoreUnsavedState()
    {
        if (_stateStore is not { } store) return;
        var licenses = Unstash<AcceptedLicense>(UnsavedLicensesMeta)
            .Where(l => store.GetLicense(l.Slug) is not { } current || current.AcceptedUtc < l.AcceptedUtc).ToArray();
        var welcomes = Unstash<ShownWelcome>(UnsavedWelcomesMeta).Where(w => !store.IsWelcomeShown(w.Slug)).ToArray();
        ForgetUnsavedState();
        if (licenses.Length > 0 && !store.TryRecordLicenses(licenses)) Stash(UnsavedLicensesMeta, licenses);
        if (welcomes.Length > 0 && !store.TryRecordWelcomes(welcomes)) Stash(UnsavedWelcomesMeta, welcomes);
    }

    /// <summary>Drops the stashed licenses and welcome pages, e.g. when the framework is disabled.</summary>
    internal static void ForgetUnsavedState()
    {
        foreach (var key in new[] { UnsavedLicensesMeta, UnsavedWelcomesMeta })
            if (Engine.Singleton.HasMeta(key)) Engine.Singleton.RemoveMeta(key);
    }

    /// <summary>
    /// The licenses to review before enabling a plugin from the ePlugin Manager, or null when there are none left
    /// after auto-accepting. A license that cannot be read is always left, so the dialog explains why the plugin stays
    /// disabled.
    /// </summary>
    internal LicenseReview? ReviewActivationLicenses(string slug, string name)
    {
        var pending = AcceptAutomatically(CollectActivationLicenses(slug));
        if (pending.Count == 0)
        {
            return null;
        }

        return new LicenseReview(pending, [new LicenseActivation(slug, name, pending.Select(e => e.Slug).ToArray())]);
    }

    /// <summary>
    /// The licenses of staged updates that were not accepted as they are in the new version, or null when there are
    /// none left after auto-accepting. An update that keeps the accepted license file is not asked about again.
    /// </summary>
    /// <remarks>
    /// A license set by a recipe is only known once the new code is compiled. Those are checked after the update by
    /// <see cref="ReviewEnabledLicenses"/>. A required license the new version cannot show is listed, also with
    /// auto-accept: it cannot be accepted, so that update is left out like one whose license was declined.
    /// </remarks>
    internal LicenseReview? ReviewUpdateLicenses(IEnumerable<ValidatedPackage> packages)
    {
        if (_stateStore is null)
        {
            return null;
        }

        var pending = new List<LicenseEntry>();
        foreach (var package in packages)
        {
            var slug = package.Candidate.Slug;
            if (RecipeOf(slug)?.PluginLicense is not null)
            {
                continue;
            }

            if (ReadLicense(slug, null, Path.Combine(package.StagingDir, "plugin.cfg"), package.StagingDir) is { Required: true } license &&
                !_stateStore.IsLicenseAccepted(slug, license.Entry.Source))
            {
                pending.Add(license.Entry);
            }
        }

        var left = AcceptAutomatically(pending);
        if (left.Count == 0)
        {
            return null;
        }

        return new LicenseReview(left, left.Select(e => new LicenseActivation(e.Slug, e.Name, [e.Slug])).ToArray(),
            isUpdate: true);
    }

    /// <summary>
    /// Asks about enabled plugins whose license is not accepted as it is now: an update changed the license its recipe
    /// sets, or the plugin was enabled without the framework asking. Declining disables the plugin.
    /// </summary>
    internal void ReviewEnabledLicenses()
    {
        if (_stateStore is null || _stateStore.IsReadOnly || _updateJournals?.Read().Any(j => j.IsActive) == true)
        {
            return;
        }

        var pending = new List<LicenseEntry>();
        foreach (var slug in GetEnabledPluginSlugs().Where(s => s != FrameworkSlug && !_stateStore.IsBlocked(s)).Order(StringComparer.Ordinal))
        {
            if (_contexts.Any(c => c.Slug == slug && c.Plugin is not null && c.State != EEditorPluginState.Activated))
            {
                continue; // not installed, e.g. in error
            }

            // plugin.cfg says whether a license is required; only then is the recipe needed for the license itself
            if (ReadInstalledLicense(slug, null) is { Required: true } &&
                ReadInstalledLicense(slug, RecipeOf(slug)) is { } license &&
                !_stateStore.IsLicenseAccepted(slug, license.Entry.Source))
            {
                pending.Add(license.Entry);
            }
        }

        var left = AcceptAutomatically(pending);
        if (left.Count == 0)
        {
            return;
        }

        if (!LicenseUiAvailable)
        {
            _ePluginContext?.Logger.Warn(UnreadableReason(left) is { } unreadable
                ? $"The license of an enabled plugin is not accepted: {unreadable}. {PluginLicense.UnreadableAdvice}"
                : $"The license of {string.Join(", ", left.Select(e => e.Name))} is not accepted; accept it in the editor or set {LicenseSettings.AutoAcceptKey}.");
            return;
        }

        QueueLicenseReview(new LicenseReview(left,
            left.Select(e => new LicenseActivation(e.Slug, e.Name, [e.Slug], Enabled: true)).ToArray()));
    }

    /// <summary>Records the licenses the user accepted in a review.</summary>
    internal void RecordLicenses(LicenseReview review) => RecordLicenses(review.Accepted, automatic: false);

    private void QueueLicenseReview(LicenseReview review)
    {
        _pendingLicenseReview = _pendingLicenseReview?.Merge(review) ?? review;
        _pendingLicenseFrame = Engine.GetProcessFrames();
        SetWaitingForLicenses(WaitingForLicenses().Union(review.Activations.Where(a => !a.Enabled).Select(a => a.Slug), StringComparer.Ordinal));
    }

    // The plugins a queued or shown license review would enable, kept in Engine metadata until it is completed: an
    // assembly reload (e.g. a build in another IDE) drops the review and its dialog, while the license gate already
    // disabled them. Enabled plugins are not kept, as Initialize reviews those anyway (ReviewEnabledLicenses).
    private const string LicenseReviewsMeta = "eplugin_license_reviews";

    private static string[] WaitingForLicenses() =>
        Engine.Singleton.HasMeta(LicenseReviewsMeta) ? Engine.Singleton.GetMeta(LicenseReviewsMeta).AsStringArray() : [];

    private static void SetWaitingForLicenses(IEnumerable<string> slugs)
    {
        var waiting = slugs.ToArray();
        if (waiting.Length > 0) Engine.Singleton.SetMeta(LicenseReviewsMeta, waiting);
        else if (Engine.Singleton.HasMeta(LicenseReviewsMeta)) Engine.Singleton.RemoveMeta(LicenseReviewsMeta);
    }

    /// <summary>
    /// Queues the license reviews an assembly reload dropped again, so their dialog reappears; called by Initialize,
    /// before <see cref="ResumeLicenseDecisions"/>. A plugin whose licenses were accepted meanwhile, e.g. in another
    /// editor, is enabled as if they had been accepted in its dialog.
    /// </summary>
    internal void ResumeLicenseReviews()
    {
        var waiting = WaitingForLicenses();
        SetWaitingForLicenses([]);
        if (_stateStore is null) return;
        foreach (var slug in waiting)
        {
            // enabled again meanwhile, which asked again; or removed
            if (_host.IsPluginEnabled(slug) || ReadInstalledMetadata(slug) is null) continue;
            var pending = AcceptAutomatically(CollectActivationLicenses(slug));
            var name = PluginName(slug);
            if (pending.Count == 0)
            {
                _ePluginContext?.Logger.Log($"The licenses {name} waited for are accepted now; enabling it.");
                RememberLicenseDecisions([(slug, true)]);
            }
            else if (!LicenseUiAvailable)
            {
                _ePluginContext?.Logger.Error(NotEnabledWithoutDialog(name, pending));
            }
            else
            {
                _ePluginContext?.Logger.Log($"{name} still waits for the license of {string.Join(", ", pending.Select(e => e.Name))} to be accepted.");
                QueueLicenseReview(new LicenseReview(pending, [new LicenseActivation(slug, name, pending.Select(e => e.Slug).ToArray())]));
            }
        }
    }

    /// <summary>
    /// The licenses that plugins enabled outside the ePlugin Manager wait for. Only handed out a frame after they were
    /// queued: by then Godot finished the plugin toggle and reopened Project Settings, so the dialog opens above it.
    /// </summary>
    internal LicenseReview? TakeLicenseReview()
    {
        if (_pendingLicenseReview is null || Engine.GetProcessFrames() <= _pendingLicenseFrame)
        {
            return null;
        }

        var review = _pendingLicenseReview;
        _pendingLicenseReview = null;
        return review;
    }

    /// <summary>
    /// Records what was accepted, enables the plugins whose licenses are all accepted now and disables enabled plugins
    /// whose license was declined.
    /// </summary>
    internal void CompleteLicenseReview(LicenseReview review)
    {
        RecordLicenses(review);
        SetWaitingForLicenses(WaitingForLicenses().Except(review.Activations.Select(a => a.Slug), StringComparer.Ordinal));
        foreach (var activation in review.Canceled)
        {
            var text = $"{activation.Name} {(activation.Enabled ? "was disabled" : "was not enabled")}: {review.CanceledReason(activation)}.";
            if (activation.Licenses.Any(l => review.DecisionOf(l) == LicenseDecision.Unreadable))
                _ePluginContext?.Logger.Error(text + " " + PluginLicense.UnreadableAdvice);
            else
                _ePluginContext?.Logger.Warn(text);
        }

        // never inside the dialog's signal: enabling and disabling run recipes and may rebuild the assembly
        var toggles = review.Approved.Where(a => !a.Enabled).Select(a => (a.Slug, Enable: true))
            .Concat(review.Canceled.Where(a => a.Enabled).Select(a => (a.Slug, Enable: false))).ToArray();
        if (toggles.Length > 0)
        {
            RememberLicenseDecisions(toggles);
            Post(ApplyLicenseDecisions);
        }
    }

    // The toggles a license review decided, kept in Engine metadata until they are applied: they wait for a pending
    // assembly reload, which discards the posted work with the rest of the old assembly.
    private const string LicenseDecisionsMeta = "eplugin_license_decisions";

    private static void RememberLicenseDecisions(IEnumerable<(string Slug, bool Enable)> toggles)
    {
        var remembered = RememberedLicenseDecisions().Where(t => toggles.All(n => n.Slug != t.Slug)).Concat(toggles);
        Engine.Singleton.SetMeta(LicenseDecisionsMeta, remembered.Select(t => (t.Enable ? "+" : "-") + t.Slug).ToArray());
    }

    private static (string Slug, bool Enable)[] RememberedLicenseDecisions() =>
        Engine.Singleton.HasMeta(LicenseDecisionsMeta)
            ? Engine.Singleton.GetMeta(LicenseDecisionsMeta).AsStringArray().Where(t => t.Length > 1).Select(t => (t[1..], t[0] == '+')).ToArray()
            : [];

    /// <summary>
    /// Remembers that the plugin is enabled because its licenses were accepted in the ePlugin Manager, until the manager
    /// enables it (<see cref="TakeLicenseEnable"/>) or, when an assembly reload interrupts it, Initialize does.
    /// </summary>
    internal static void RememberLicenseEnable(string slug) => RememberLicenseDecisions([(slug, true)]);

    /// <summary>Forgets what <see cref="RememberLicenseEnable"/> kept, as the caller enables the plugin itself.</summary>
    internal static void TakeLicenseEnable(string slug)
    {
        var rest = RememberedLicenseDecisions().Where(t => t.Slug != slug).ToArray();
        ForgetLicenseDecisions(reviews: false);
        if (rest.Length > 0) RememberLicenseDecisions(rest);
    }

    /// <summary>Whether a license review decided to enable or disable the plugin, and that was not applied yet.</summary>
    internal static bool HasLicenseDecision(string slug) => RememberedLicenseDecisions().Any(t => t.Slug == slug);

    /// <summary>
    /// Drops the decisions not applied yet and, unless <paramref name="reviews"/> is false, the reviews waiting for
    /// their dialog, e.g. when the framework is disabled.
    /// </summary>
    internal static void ForgetLicenseDecisions(bool reviews = true)
    {
        if (Engine.Singleton.HasMeta(LicenseDecisionsMeta)) Engine.Singleton.RemoveMeta(LicenseDecisionsMeta);
        if (reviews) SetWaitingForLicenses([]);
    }

    /// <summary>Applies the decisions of a license review that an assembly reload interrupted; called by Initialize.</summary>
    internal void ResumeLicenseDecisions()
    {
        if (Engine.Singleton.HasMeta(LicenseDecisionsMeta)) Post(ApplyLicenseDecisions);
    }

    /// <summary>
    /// Toggles the plugins of a license review in one transition, so the project is built once, after the last of them,
    /// and none of them starts while that build's assembly reload is due.
    /// </summary>
    private void ApplyLicenseDecisions()
    {
        if (AssemblyReload.IsPending)
        {
            Post(ApplyLicenseDecisions);
            return;
        }

        var toggles = RememberedLicenseDecisions();
        ForgetLicenseDecisions(reviews: false);
        if (toggles.Length == 0) return;
        _transitionHold++;
        try
        {
            foreach (var (slug, enable) in toggles)
            {
                if (_host.IsPluginEnabled(slug) != enable)
                {
                    _host.SetPluginEnabled(slug, enable);
                }
            }
        }
        finally
        {
            _transitionHold--;
        }

        FinishPlainPluginTransition();
    }
}
#endif
