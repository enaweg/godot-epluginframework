#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Disabling an ePlugin: its activated dependants are disabled first, then its recipe and the optional recipes it got
/// are reversed, keeping what recipes that stay installed still declare.
/// </summary>
internal sealed partial class EGlobal
{
    public void DisableEPlugin(PluginContext context, bool refreshAtEnd = true, bool manualRetry = false)
    {
        if (context.Plugin is null)
        {
            return;
        }

        if (!IsValid())
        {
            // Without the framework there is no dotnet CLI or state file to uninstall with, and Godot frees the plugin
            // right after this callback, so its uninstall cannot wait for the framework either. A partial uninstall that
            // records nothing would leave the project in a state no retry knows about.
            var warning = $"{context.Slug} was disabled while the ePlugin framework is not running, so its recipe stays installed. " +
                          "Enable the framework, then enable and disable the plugin again to remove it.";
            if (context.Logger is { } logger) logger.Warn(warning);
            else GD.PushWarning(warning);
            return;
        }

        if (_toCheckEnable.Contains(context))
        {
            // plugin in process of enablement, skip disable
            return;
        }

        if (context.State == EEditorPluginState.Deactivated && !manualRetry)
        {
            return;
        }

        if (context.State == EEditorPluginState.Error && !manualRetry)
        {
            return;
        }

        if (manualRetry)
        {
            context.State = EEditorPluginState.Activated;
            context.ErrorDetail = null;
        }

        if (!JoinTransition(context, PersistedPluginState.Deactivated, manualRetry))
        {
            return;
        }

        if (!TryEnsureRecipe(context))
        {
            FailTransition("recipe_creation_failed");
            return;
        }

        // disable plugins dependent on this one
        var pluginsToDisable = new List<PluginContext>();
        foreach (var plugin in _contexts)
        {
            if (plugin == context)
            {
                continue;
            }

            if (plugin.State is not EEditorPluginState.Activated)
            {
                continue;
            }

            if (plugin.Plugin is null)
            {
                // not an ePlugin plugin.
                continue;
            }

            if (!TryEnsureRecipe(plugin, scan: true))
            {
                FailTransition("dependent_recipe_creation_failed");
                return;
            }

            var isDependant = plugin.Builder.PluginRecipe.PluginDependencies.Any(d => d.Slug == context.Slug);
            if (!isDependant)
            {
                continue;
            }

            pluginsToDisable.Add(plugin);
        }

        // A dependant the editor already disabled runs no DisableEPlugin that would resume this plugin, e.g. because its
        // _DisablePlugin threw before calling it; waiting for it would keep the transition open forever.
        foreach (var plugin in pluginsToDisable.Where(p => !_host.IsPluginEnabled(p.Slug)))
        {
            context.Logger?.Warn($"{plugin.Slug} requires {context.Slug}, but the editor already disabled it; its recipe stays installed.");
        }

        pluginsToDisable.RemoveAll(p => !_host.IsPluginEnabled(p.Slug));
        if (pluginsToDisable.Count > 0)
        {
            _toCheckDisable.Push(context);

            foreach (var plugin in pluginsToDisable)
            {
                if (plugin.State is not EEditorPluginState.Activated)
                {
                    continue;
                }

                if (_host.IsPluginEnabled(plugin.Slug))
                {
                    _toCheckDisable.Push(plugin);
                    _host.SetPluginEnabled(plugin.Slug, false);
                }
            }

            // Godot runs a dependant's _DisablePlugin inside the toggle. One that never called DisableEPlugin (e.g. it threw
            // first) is disabled but still activated, and nothing would resume this plugin: it goes on without waiting.
            var silent = pluginsToDisable.Where(p => p.State == EEditorPluginState.Activated && !_host.IsPluginEnabled(p.Slug)).ToArray();
            if (silent.Length > 0 && context.State == EEditorPluginState.Activated && _toCheckDisable.Contains(context))
            {
                var waiting = _toCheckDisable.Where(c => !silent.Contains(c)).Reverse().ToArray();
                _toCheckDisable.Clear();
                foreach (var plugin in waiting) _toCheckDisable.Push(plugin);
                DisableEPlugin(context, refreshAtEnd, manualRetry);
            }

            return;
        }

        ActivationProgress.SetText($"Deactivating {context.Name}...");
        try
        {
            UninstallEPlugin(context, context.Builder.PluginRecipe);

            // other plugins may have installed code that depends on this one via an optional dependency
            UninstallOptionalDependenciesOn(context.Slug);
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Deactivating {context.Slug} failed: {ex.Message}");
            FailTransition("deactivation_failed");
            return;
        }

        context.State = EEditorPluginState.Deactivated;

        // @ local dependencies: can not disable as we do not know which are needed. There is no way to track manual or
        //                       auto enabled plugins right now.

        if (refreshAtEnd)
        {
            // trigger uninstall for waiting plugins
            while (_toCheckDisable.Count > 0)
            {
                var nextPlugin = _toCheckDisable.Pop();

                DisableEPlugin(nextPlugin, false);
            }

            if (FinishTransition())
            {
                RefreshEditor(rebuild: false);
            }
        }
    }

    private void UninstallEPlugin(PluginContext context, EEditorPluginRecipe recipe)
    {
        // without a snapshot (e.g. the context was rebuilt after an assembly reload) fall back to resolving
        // the optional dependencies against the current editor state.
        // the snapshot shrinks as its recipes are reversed, so the ones still installed keep what they share with others.
        var applied = (context.AppliedOptionalDependencies ?? ResolveOptionalRecipes(context, recipe)).ToList();
        context.AppliedOptionalDependencies = applied;

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            ReverseRecipe(context, applied[i].Recipe);
            applied.RemoveAt(i);
        }

        ReverseRecipe(context, recipe);
        context.AppliedOptionalDependencies = null;
    }

    private void ReverseRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        var remaining = RemainingRecipes(context, recipe);
        var ownRemaining = InstalledRecipes(context).Where(r => !ReferenceEquals(r, recipe)).ToArray();
        foreach (var autoload in recipe.Autoloads) ReverseAutoload(context, autoload, remaining);
        foreach (var directory in recipe.Directories) ShowHideHelper.HideDirectory(context, directory, _host);
        foreach (var project in recipe.Projects) ReverseProject(context, project, remaining);
        foreach (var nuget in recipe.Nugets) ReverseNuget(context, nuget, remaining, ownRemaining);
    }
}
#endif
