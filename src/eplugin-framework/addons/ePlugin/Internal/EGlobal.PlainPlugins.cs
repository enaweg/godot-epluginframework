#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Licenses;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    // Engine metadata survives assembly reloads, so a plugin toggled between a build and its reload is still noticed.
    private const string PluginTogglesMeta = "eplugin_enabled_plugins";
    private PluginToggles? _pluginToggles;

    /// <summary>Starts from the plugins known before an assembly reload, else from the ones enabled now.</summary>
    internal void InitializePluginToggles()
    {
        _pluginToggles = new PluginToggles(Engine.Singleton.HasMeta(PluginTogglesMeta)
            ? Engine.Singleton.GetMeta(PluginTogglesMeta).AsStringArray()
            : GetEnabledPluginSlugs());
        SavePluginToggles();
    }

    /// <summary>Forgets the known plugins, so enabling the framework again starts from the plugins enabled then.</summary>
    internal static void ForgetPluginToggles()
    {
        if (Engine.Singleton.HasMeta(PluginTogglesMeta)) Engine.Singleton.RemoveMeta(PluginTogglesMeta);
    }

    private void SavePluginToggles()
    {
        if (_pluginToggles is not null)
            Engine.Singleton.SetMeta(PluginTogglesMeta, _pluginToggles.Known.ToArray());
    }

    private void NotePluginToggle(string slug, bool enabled)
    {
        _pluginToggles?.Note(slug, enabled);
        SavePluginToggles();
    }

    /// <summary>
    /// Handles plain plugins (GDScript or C# plugins without a recipe) that were enabled or disabled since the last call,
    /// in Project Settings, the ePlugin Manager or by a declined license. They run no framework code when they are
    /// toggled, so this is called every frame and right after the ePlugin Manager toggles a plugin.
    /// </summary>
    internal void ObservePluginToggles()
    {
        if (!IsValid() || _pluginToggles is null)
        {
            return;
        }

        var current = GetEnabledPluginSlugs();
        var (enabled, disabled) = _pluginToggles.Changes(current);
        if (enabled.Length == 0 && disabled.Length == 0)
        {
            return;
        }

        // Handled once the new assembly is loaded: work started now would run on the old code.
        if (AssemblyReload.IsPending)
        {
            return;
        }

        // Taken as handled first, so a failure below is not repeated every frame.
        _pluginToggles.Rebase(current);
        SavePluginToggles();

        // Updates disable and enable plain plugins while they replace them, and reconcile recipes themselves.
        if (_recipeUpdateJournal is not null || _updateRefreshSuppression > 0 ||
            _updateJournals?.Read().Any(j => j.IsActive) == true)
        {
            return;
        }

        foreach (var slug in disabled.Where(IsPlainPlugin))
        {
            Guard(slug, () => OnPlainPluginDisabled(slug));
        }

        foreach (var slug in enabled.Where(IsPlainPlugin))
        {
            Guard(slug, () => OnPlainPluginEnabled(slug));
        }

        void Guard(string slug, Action handle)
        {
            try
            {
                handle();
            }
            catch (Exception ex)
            {
                _ePluginContext?.Logger.Error($"Handling the toggle of {slug} failed: {ex.Message}");
            }
        }
    }

    private bool IsPlainPlugin(string slug) => slug != FrameworkSlug && !IsManagedPlugin(slug);

    private string PluginName(string slug) => ReadInstalledMetadata(slug)?.Name is { Length: > 0 } name ? name : slug;

    /// <summary>
    /// Asks for the plugin's license like for an ePlugin, then installs the optional recipes of other plugins that it
    /// satisfies now.
    /// </summary>
    private void OnPlainPluginEnabled(string slug)
    {
        // a context left from before the plugin was disabled holds old metadata; versions are read from plugin.cfg
        _contexts.RemoveAll(c => c.Slug == slug && c.Plugin is null);
        var name = PluginName(slug);
        var pending = CollectActivationLicenses(slug);
        if (pending.Count > 0 && !AcceptAutomatically(pending))
        {
            var names = string.Join(", ", pending.Select(e => e.Name));
            if (!LicenseUiAvailable)
            {
                _ePluginContext?.Logger.Error(
                    $"{name} was not enabled: the license of {names} has to be accepted in the editor, or set {LicenseSettings.AutoAcceptKey}.");
            }
            else
            {
                _ePluginContext?.Logger.Log($"{name} waits for the license of {names} to be accepted.");
                QueueLicenseReview(new LicenseReview(pending,
                    [new LicenseActivation(slug, name, pending.Select(e => e.Slug).ToArray())]));
            }

            // Accepting enables it again, which is handled like any other toggle. The scope reopens Project Settings
            // afterwards, so its plugin list shows the plugin as disabled.
            using (ActivationProgress.Begin($"Disabling {name} until its license is accepted..."))
            {
                _host.SetPluginEnabled(slug, false);
            }

            NotePluginToggle(slug, false);
            return;
        }

        if (!PluginsWithOptionalDependencyOn(slug).Any())
        {
            return;
        }

        using var progress = ActivationProgress.Begin($"Installing optional recipes for {name}...");
        try
        {
            ReevaluateOptionalDependencies(slug);
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Error($"Installing the optional recipes that {name} satisfies failed: {ex.Message}");
            FailTransition("optional_recipe_failed");
            return;
        }

        FinishPlainPluginTransition();
    }

    /// <summary>
    /// Disables the ePlugins that require the plugin and removes the optional recipes that were installed because of it,
    /// in one transition, so the project is built once.
    /// </summary>
    private void OnPlainPluginDisabled(string slug)
    {
        _contexts.RemoveAll(c => c.Slug == slug && c.Plugin is null);
        var dependants = _contexts.Where(c => c.Plugin is not null && c.State == EEditorPluginState.Activated &&
                                              RecipeOf(c.Slug)?.PluginDependencies.Any(d => d.Slug == slug) == true)
            .ToArray();
        if (dependants.Length == 0 && !PluginsWithOptionalDependencyOn(slug).Any())
        {
            return;
        }

        var name = PluginName(slug);
        using var progress = ActivationProgress.Begin($"Deactivating what needs {name}...");
        _transitionHold++;
        try
        {
            foreach (var dependant in dependants)
            {
                // Its uninstall resolves its optional recipes against the editor, where this plugin is gone already.
                if (dependant.AppliedOptionalDependencies is null && GetInstalledOptionalRecipeOrNull(dependant, slug) is { } recipe)
                {
                    dependant.AppliedOptionalDependencies = ResolveOptionalRecipes(dependant, recipe, assumeEnabledSlug: slug);
                }
            }

            foreach (var dependant in dependants.Where(d => _host.IsPluginEnabled(d.Slug)))
            {
                dependant.Logger?.Warn($"Disabling {dependant.Name}: it requires {name}, which was disabled.");
                _host.SetPluginEnabled(dependant.Slug, false);
            }

            UninstallOptionalDependenciesOn(slug);
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Error($"Removing what needs {name} failed: {ex.Message}");
            FailTransition("optional_recipe_failed");
            return;
        }
        finally
        {
            _transitionHold--;
        }

        FinishPlainPluginTransition();
    }

    /// <summary>The installed ePlugins that declare an optional dependency on the plugin.</summary>
    private IEnumerable<PluginContext> PluginsWithOptionalDependencyOn(string slug) =>
        _contexts.ToArray().Where(c => c.Plugin is not null &&
                                       c.State is not (EEditorPluginState.Deactivated or EEditorPluginState.Error) &&
                                       DeclaresOptionalDependency(c, slug));

    private static bool DeclaresOptionalDependency(PluginContext context, string slug)
    {
        try
        {
            return GetInstalledOptionalRecipeOrNull(context, slug) is not null;
        }
        catch (Exception ex)
        {
            context.Logger?.Error($"Cannot read the recipe of {context.Slug}: {ex.Message}");
            return false;
        }
    }

    private void FinishPlainPluginTransition()
    {
        if (_transition is not null && FinishTransition())
        {
            RefreshEditor(rebuild: false);
        }
    }
}
#endif
