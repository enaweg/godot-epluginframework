#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Optional dependencies: a nested recipe is installed while its plugin is enabled, in whichever order the two plugins
/// are enabled, and reversed when either of them is disabled.
/// </summary>
internal sealed partial class EGlobal
{
    /// <summary>
    /// Determines which optional plugin dependencies of <paramref name="recipe"/> are currently satisfied and
    /// returns their nested recipes. An optional dependency is satisfied when its plugin is enabled and, if a
    /// version constraint was given, the installed version matches. Unsatisfied ones are skipped — they never
    /// enable a plugin and never fail the activation.
    /// </summary>
    /// <param name="ignoreSlug">
    /// When given, that plugin is treated as if it were not enabled. Used to reconstruct which optional
    /// recipes were already installed before a plugin became available.
    /// </param>
    /// <param name="assumeEnabledSlug">
    /// When given, that plugin is treated as if it were still enabled. Used to reconstruct which optional
    /// recipes were installed while a plugin that is being disabled right now was still available.
    /// </param>
    /// <param name="requireReady">
    /// When true, an enabled optional plugin that is in the error state (e.g. blocked by local state) does not
    /// count as satisfied. Used when installing; reconstructing what was installed earlier leaves it false.
    /// </param>
    internal List<EEditorPluginRecipe.OptionalPlugin> ResolveOptionalRecipes(PluginContext context,
        EEditorPluginRecipe recipe, string? ignoreSlug = null, string? assumeEnabledSlug = null,
        bool requireReady = false)
    {
        var resolved = new List<EEditorPluginRecipe.OptionalPlugin>();

        foreach (var optional in recipe.OptionalPluginDependencies)
        {
            if (optional.Slug == ignoreSlug)
            {
                continue;
            }

            if (requireReady && optional.Slug != assumeEnabledSlug &&
                _contexts.FirstOrDefault(c => c.Slug == optional.Slug && c.Plugin is not null)
                    is { State: EEditorPluginState.Error })
            {
                context.Logger?.Warn(
                    $"Optional dependency {optional.Slug} is in an error state, skipping its recipe for {context.Slug}.");
                continue;
            }

            if (optional.Slug != assumeEnabledSlug && !IsDependencyAvailable(optional.Slug))
            {
                context.Logger?.Log(
                    $"Optional dependency {optional.Slug} not enabled, skipping its recipe for {context.Slug}.");
                continue;
            }

            if (optional.Version is not null)
            {
                var optionalContext = _contexts.FirstOrDefault(c => c.Slug == optional.Slug);
                // a plain plugin enabled in this session has no context
                var optionalVersion = optionalContext?.Metadata?.Version ?? InstalledVersion(optional.Slug) ?? "0.0";

                if (!MatchesVersion(optionalVersion, optional.Version, context.Logger))
                {
                    context.Logger?.Log(
                        $"Optional dependency {optional.Slug} {optionalVersion} does not match needed {optional.Version}, skipping its recipe for {context.Slug}.");
                    continue;
                }
            }

            context.Logger?.Log($"Optional dependency {optional.Slug} satisfied for {context.Slug}.");
            resolved.Add(optional);
        }

        return resolved;
    }

    /// <summary>
    /// Re-checks the optional dependencies of every other installed plugin now that the plugin
    /// <paramref name="enabledSlug"/> is available, and installs the nested recipes that became
    /// satisfied. This makes activation order irrelevant: a plugin that was activated before its optional
    /// dependency still picks it up once that dependency is enabled.
    /// </summary>
    private void ReevaluateOptionalDependencies(string enabledSlug)
    {
        foreach (var other in _contexts.ToArray())
        {
            if (other.Slug == enabledSlug || other.Plugin is null || IsBeingDeactivated(other))
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, enabledSlug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) assume everything that
            // was already satisfied before this plugin appeared is installed. Applied entries stay in the
            // snapshot even when they are no longer satisfied, so uninstall still reverses them.
            var applied = new List<EEditorPluginRecipe.OptionalPlugin>(other.AppliedOptionalDependencies ??
                ResolveOptionalRecipes(other, otherRecipe, ignoreSlug: enabledSlug));
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in ResolveOptionalRecipes(other, otherRecipe, requireReady: true))
            {
                if (applied.Contains(optional) || _recipeUpdateJournal is not null && applied.Any(a => a.Slug == optional.Slug && a.Version == optional.Version))
                {
                    continue;
                }

                other.Logger?.Log(
                    $"Installing optional recipe of {other.Slug} that became satisfied by {enabledSlug}.");

                if (!JoinTransition(other, PersistedPluginState.Activated))
                {
                    throw new InvalidOperationException($"Cannot record optional recipe installation for {other.Slug}.");
                }

                applied.Add(optional);
                ApplyRecipe(other, optional.Recipe);

                if (other.State is EEditorPluginState.Error)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Whether the current transition disables the plugin, now or once its dependants are disabled.</summary>
    private bool IsBeingDeactivated(PluginContext context) =>
        _transition?.Participants.GetValueOrDefault(context) == PersistedPluginState.Deactivated || _toCheckDisable.Contains(context);

    /// <summary>
    /// Reverses the nested recipes that other installed plugins got because the plugin
    /// <paramref name="disabledSlug"/> was available. Without this their code would keep referencing a
    /// plugin that is no longer installed and the project would stop compiling.
    /// </summary>
    private void UninstallOptionalDependenciesOn(string disabledSlug)
    {
        foreach (var other in _contexts.ToArray())
        {
            // a plugin this transition disables reverses its whole snapshot itself, e.g. one that a dependant of it
            // declares an optional dependency of
            if (other.Slug == disabledSlug || other.Plugin is null || IsBeingDeactivated(other))
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, disabledSlug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) reconstruct what was
            // installed while the plugin being disabled was still available.
            var applied = other.AppliedOptionalDependencies ??
                          ResolveOptionalRecipes(other, otherRecipe, assumeEnabledSlug: disabledSlug);
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in applied.Where(o => o.Slug == disabledSlug).ToArray())
            {
                other.Logger?.Log(
                    $"Removing optional recipe of {other.Slug} that depended on {disabledSlug}.");

                if (!JoinTransition(other, PersistedPluginState.Activated))
                {
                    throw new InvalidOperationException($"Cannot record optional recipe removal for {other.Slug}.");
                }

                ReverseRecipe(other, optional.Recipe);
                applied.Remove(optional);
            }
        }
    }

    /// <summary>
    /// Returns the recipe of <paramref name="context"/> when it is installed and declares an optional
    /// dependency on <paramref name="slug"/>, otherwise <see langword="null"/>.
    /// </summary>
    private static EEditorPluginRecipe? GetInstalledOptionalRecipeOrNull(PluginContext context, string slug)
    {
        // installed either in an earlier session (Activated after a reload) or in this one (it has a
        // snapshot). Anything else was never installed and resolves its optionals on activation.
        if (context.State is not EEditorPluginState.Activated && context.AppliedOptionalDependencies is null)
        {
            return null;
        }

        if (!TryEnsureRecipe(context, scan: true))
        {
            return null;
        }

        var recipe = context.Builder.PluginRecipe;
        return recipe.OptionalPluginDependencies.Any(o => o.Slug == slug) ? recipe : null;
    }
}
#endif
