#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private LicenseReview? _pendingLicenseReview;
    private ulong _pendingLicenseFrame;

    private bool LicenseUiAvailable => DisplayServer.GetName() != "headless" && _ePluginContext?.IsInsideTree() == true;

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

        var pending = CollectActivationLicenses(context.Slug);
        if (pending.Count == 0 || AcceptAutomatically(pending))
        {
            return true;
        }

        var names = string.Join(", ", pending.Select(e => e.Name));
        if (_toCheckEnable.Count > 0)
        {
            // A dependency whose license was not reviewed with the plugin that enables it, because its recipe could
            // not be read beforehand. The waiting chain cannot pause for a dialog, so it fails like a missing dependency.
            var reason = $"The license of {names} has to be accepted first. Enable {context.Name} on its own to review it, then use Retry failed.";
            context.Logger?.Error(reason);
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = new Exception(reason);
            _toCheckEnable.Push(context);
            FailAllUncheckedPluginsAndRefresh(reason);
            return false;
        }

        if (!LicenseUiAvailable)
        {
            context.Logger?.Error(
                $"{context.Name} was not enabled: the license of {names} has to be accepted in the editor, or set {LicenseSettings.AutoAcceptKey}.");
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
        Callable.From(() =>
        {
            if (EditorInterface.Singleton.IsPluginEnabled(context.Slug))
            {
                EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
            }

            _contexts.Remove(context);
        }).CallDeferred();
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
                if (!EditorInterface.Singleton.IsPluginEnabled(dependency.Slug))
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
        var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
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
        var directory = $"res://addons/{slug}";
        using var config = new ConfigFile();
        if (config.Load(directory + "/plugin.cfg") != Error.Ok)
        {
            return null;
        }

        var script = PluginCatalog.ResolveScript(directory, config.GetValue("plugin", "script", "").AsString().Trim());
        var type = script is null || !script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? null
            : typeof(EGlobal).Assembly.GetTypes().FirstOrDefault(t =>
                typeof(EditorPlugin).IsAssignableFrom(t) && typeof(IEEditorPlugin).IsAssignableFrom(t) &&
                t.GetCustomAttribute<ScriptPathAttribute>()?.Path == script);
        if (type is null)
        {
            return null;
        }

        EditorPlugin? instance = null;
        try
        {
            instance = (EditorPlugin)Activator.CreateInstance(type)!;
            var builder = EEditorPluginBuilder.Create();
            ((IEEditorPlugin)instance).CreateRecipe(builder);
            return builder.PluginRecipe;
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Warn($"Cannot read the recipe of {slug} to collect its license and dependencies: {ex.Message}");
            return null;
        }
        finally
        {
            instance?.Free();
        }
    }

    private static LicenseInfo? ReadInstalledLicense(string slug, EEditorPluginRecipe? recipe) =>
        ReadLicense(slug, recipe, $"res://addons/{slug}/plugin.cfg", ProjectSettings.GlobalizePath($"res://addons/{slug}"));

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
        ReadInstalledLicense(slug, RecipeOf(slug)) is { } license
            ? license with { Accepted = _stateStore?.GetLicense(slug) }
            : null;

    /// <summary>Accepts the licenses without asking when the project allows it; returns whether it did.</summary>
    private bool AcceptAutomatically(IReadOnlyCollection<LicenseEntry> entries)
    {
        if (!LicenseSettings.AutoAccept)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            _ePluginContext?.Logger.Warn(
                $"Accepted the license of {entry.Name} ({PluginLicense.Display(entry.Slug, entry.Source)}) without showing it, because {LicenseSettings.AutoAcceptKey} is enabled.");
        }

        RecordLicenses(entries, automatic: true);
        return true;
    }

    private void RecordLicenses(IEnumerable<LicenseEntry> entries, bool automatic)
    {
        var now = DateTimeOffset.UtcNow;
        var accepted = entries.Select(e => new AcceptedLicense(e.Slug, e.Source, e.Version, now, automatic)).ToArray();
        if (_stateStore is null || accepted.Length == 0 || _stateStore.TryRecordLicenses(accepted))
        {
            return;
        }

        _ePluginContext?.Logger.Error(
            "Accepted licenses could not be saved to addons/eplugin-state.json; they are asked for again after the editor restarts.");
    }

    /// <summary>
    /// The licenses to review before enabling a plugin from the ePlugin Manager, or null when there are none left
    /// after auto-accepting.
    /// </summary>
    internal LicenseReview? ReviewActivationLicenses(string slug, string name)
    {
        var pending = CollectActivationLicenses(slug);
        if (pending.Count == 0 || AcceptAutomatically(pending))
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
    /// <see cref="ReviewEnabledLicenses"/>.
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

        if (pending.Count == 0 || AcceptAutomatically(pending))
        {
            return null;
        }

        return new LicenseReview(pending, pending.Select(e => new LicenseActivation(e.Slug, e.Name, [e.Slug])).ToArray(),
            isUpdate: true);
    }

    /// <summary>
    /// Asks about enabled plugins whose license is not accepted as it is now: an update changed the license its recipe
    /// sets, or the plugin was enabled without the framework asking. Declining disables the plugin.
    /// </summary>
    private void ReviewEnabledLicenses()
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

        if (pending.Count == 0 || AcceptAutomatically(pending))
        {
            return;
        }

        if (!LicenseUiAvailable)
        {
            _ePluginContext?.Logger.Warn(
                $"The license of {string.Join(", ", pending.Select(e => e.Name))} is not accepted; accept it in the editor or set {LicenseSettings.AutoAcceptKey}.");
            return;
        }

        QueueLicenseReview(new LicenseReview(pending,
            pending.Select(e => new LicenseActivation(e.Slug, e.Name, [e.Slug], Enabled: true)).ToArray()));
    }

    /// <summary>Records the licenses the user accepted in a review.</summary>
    internal void RecordLicenses(LicenseReview review) => RecordLicenses(review.Accepted, automatic: false);

    private void QueueLicenseReview(LicenseReview review)
    {
        _pendingLicenseReview = _pendingLicenseReview?.Merge(review) ?? review;
        _pendingLicenseFrame = Engine.GetProcessFrames();
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
        foreach (var activation in review.Canceled)
        {
            var declined = review.Entries.Where(e => activation.Licenses.Contains(e.Slug) &&
                                                     review.DecisionOf(e.Slug) == LicenseDecision.Declined);
            _ePluginContext?.Logger.Warn(
                $"{activation.Name} {(activation.Enabled ? "was disabled" : "was not enabled")}: the license of {string.Join(", ", declined.Select(e => e.Name))} was declined.");
        }

        // never inside the dialog's signal: enabling and disabling run recipes and may rebuild the assembly
        foreach (var (activation, enable) in review.Approved.Where(a => !a.Enabled).Select(a => (a, true))
                     .Concat(review.Canceled.Where(a => a.Enabled).Select(a => (a, false))))
        {
            Callable.From(() =>
            {
                if (EditorInterface.Singleton.IsPluginEnabled(activation.Slug) != enable)
                {
                    EditorInterface.Singleton.SetPluginEnabled(activation.Slug, enable);
                }
            }).CallDeferred();
        }
    }
}
#endif
