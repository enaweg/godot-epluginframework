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
            // One enabled before the framework was initialized waits for its activation; resumed, it would be enabled again.
            StopWaiting(context);
            var warning = $"{context.Slug} was disabled while the ePlugin framework is not running, so its recipe stays installed. " +
                          "Enable the framework, then enable and disable the plugin again to remove it.";
            if (context.Logger is { } logger) logger.Warn(warning);
            else GD.PushWarning(warning);
            return;
        }

        if (_toCheckEnable.Contains(context))
        {
            // Disabled while it waits for a dependency: it installed nothing yet, and resumed it would be enabled again.
            CancelWaitingActivation(context, refreshAtEnd);
            return;
        }

        // A plugin that waits for this one would enable it again once resumed, overriding the user.
        FailPluginsWaitingFor(context.Slug);

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
            // Its recipe is gone, so the optional recipes reversed next keep nothing for it that they also declare.
            context.State = EEditorPluginState.Deactivated;

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

    /// <summary>
    /// Cancels the activation of a plugin disabled while it waits for a dependency: it stops waiting and leaves the
    /// transition, which records nothing for it, since nothing of its recipe was installed. The plugins that wait for it
    /// fail like for any dependency that did not activate; the others of the transition finish without it.
    /// </summary>
    private void CancelWaitingActivation(PluginContext context, bool refreshAtEnd)
    {
        StopWaiting(context);
        // also keeps a callback that still comes for it from activating it, see EnableEPlugin
        context.State = EEditorPluginState.Deactivated;
        context.ErrorDetail = null;
        context.Logger?.Log($"{context.Slug} was disabled while it waited for its dependencies; its activation is canceled.");
        if (_transition is { } transition && transition.Participants.Remove(context))
        {
            if (_stateStore?.TryRemoveParticipant(transition.AttemptId, context.Slug) == false)
            {
                // its attempt_in_progress entry would keep the others from completing
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = new InvalidOperationException(RecordFailedDetail);
                transition.Participants[context] = PersistedPluginState.Deactivated;
                FailTransition("cannot_record_participant", RecordFailedDetail);
                return;
            }

            if (transition.Participants.Count == 0)
            {
                _transition = null;
            }
        }

        if (FailPluginsWaitingFor(context.Slug) || !refreshAtEnd || _transition is null)
        {
            return;
        }

        if (FinishTransition())
        {
            RefreshEditor(rebuild: false);
        }
    }

    /// <summary>
    /// Fails the plugins that wait for <paramref name="slug"/> to activate, because it is being disabled. Resumed, they
    /// would enable it again. Returns whether there were any.
    /// </summary>
    private bool FailPluginsWaitingFor(string slug)
    {
        var dependants = _toCheckEnable.Where(c => c.Slug != slug && c.IsRecipeCreated &&
                                                   c.Builder.PluginRecipe.PluginDependencies.Any(d => d.Slug == slug))
            .Select(c => c.Slug).Distinct(StringComparer.Ordinal).ToArray();
        if (dependants.Length == 0)
        {
            return false;
        }

        var names = string.Join(", ", dependants);
        FailWaitingPlugins($"Plugin dependency {slug} was disabled while {names} waited for it to activate. " +
                           $"Enable {slug} again and retry, or disable {names}.", "dependency_disabled");
        return true;
    }

    /// <summary>Takes a plugin off the plugins that wait for the framework to be initialized.</summary>
    private void StopWaiting(PluginContext context)
    {
        _enabledWithFramework.Remove(context);
        if (!_toCheckEnable.Contains(context)) return;
        var waiting = _toCheckEnable.Where(c => c != context).Reverse().ToArray();
        _toCheckEnable.Clear();
        foreach (var plugin in waiting) _toCheckEnable.Push(plugin);
    }

    private void ReverseRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        var remaining = RemainingRecipes(context, recipe);
        var ownRemaining = InstalledRecipes(context).Where(r => !ReferenceEquals(r, recipe)).ToArray();
        foreach (var autoload in recipe.Autoloads) ReverseAutoload(context, autoload, remaining);
        foreach (var directory in recipe.Directories) ReverseDirectory(context, directory, remaining);
        foreach (var project in recipe.Projects) ReverseProject(context, project, remaining);
        foreach (var nuget in recipe.Nugets) ReverseNuget(context, nuget, remaining, ownRemaining);
    }
}
#endif
