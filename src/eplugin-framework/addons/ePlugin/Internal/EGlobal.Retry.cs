#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Manual retries of failed attempts from the ePlugin Manager's Needs attention dialog: retrying them, and disabling a
/// plugin whose activation failed.
/// </summary>
internal sealed partial class EGlobal
{
    /// <summary>Retries every failed or interrupted attempt.</summary>
    public void RetryFailedPlugins() => RetryFailedPlugins(null);

    /// <summary>
    /// Retries the failed attempts of <paramref name="slugs"/> together with those <see cref="RetryScope"/> adds; null
    /// retries every attempt.
    /// </summary>
    public void RetryFailedPlugins(IReadOnlyCollection<string>? slugs)
    {
        RefreshPlainPlugins();
        if (_stateStore is null || _stateStore.IsReadOnly)
        {
            return;
        }

        Retry(slugs is null ? _stateStore.LocalAttempts : RetryScope(slugs, dependencies: true), disable: null);
    }

    /// <summary>
    /// Gives up on a plugin whose activation or deactivation failed: undoes what it installed and disables it. The other
    /// plugins of the same attempt are retried. False when it has no such attempt, e.g. after a failed update, which is
    /// retried or rolled back instead; the ePlugin Manager then disables it like any other plugin.
    /// </summary>
    public bool DisableFailedPlugin(string slug)
    {
        RefreshPlainPlugins();
        if (_stateStore is null || _stateStore.IsReadOnly || _stateStore.GetLocal(slug) is not { } attempt || IsUpdateAttempt(attempt))
        {
            return false;
        }

        if (attempt.Reason == "invalid_plugin_version" && attempt.TargetState == PersistedPluginState.Activated)
        {
            // The version is checked before anything is installed, and a plugin without one cannot complete a transition.
            // A failed deactivation left its recipe installed: it is retried as one, which needs the version repaired.
            if (!_stateStore.TryAbandonAttempt(attempt.AttemptId)) return false;
            if (_contexts.FirstOrDefault(c => c.Slug == slug) is { } invalid) invalid.State = EEditorPluginState.Deactivated;
            if (_host.IsPluginEnabled(slug)) _host.SetPluginEnabled(slug, false);
            return true;
        }

        Retry(RetryScope([slug], dependencies: false), disable: slug);
        return true;
    }

    /// <summary>
    /// The failed attempts a retry of <paramref name="slugs"/> covers: theirs, those of the plugins that failed in the
    /// same attempt (a retry replaces the attempt of all of them), and with <paramref name="dependencies"/> those of the
    /// failed hard dependencies they need.
    /// </summary>
    internal IReadOnlyCollection<LocalPluginAttempt> RetryScope(IEnumerable<string> slugs, bool dependencies)
    {
        if (_stateStore is null) return [];
        var attempts = _stateStore.LocalAttempts;
        var scope = new Dictionary<string, LocalPluginAttempt>(StringComparer.Ordinal);
        var queue = new Queue<string>(slugs);
        while (queue.TryDequeue(out var slug))
        {
            if (_stateStore.GetLocal(slug) is not { } attempt || !scope.TryAdd(slug, attempt)) continue;
            foreach (var related in attempts.Where(a => a.AttemptId == attempt.AttemptId)) queue.Enqueue(related.Slug);
            if (dependencies)
                foreach (var dependency in GetHardDependencySlugs(slug)) queue.Enqueue(dependency);
        }
        return scope.Values;
    }

    private bool IsUpdateAttempt(LocalPluginAttempt attempt) =>
        attempt.Reason.StartsWith("update_", StringComparison.Ordinal) ||
        _updateJournals?.Read().Any(j => j.AttemptId == attempt.AttemptId) == true;

    /// <param name="disable">A plugin whose failed attempt is retried as a deactivation, whatever it aimed for.</param>
    private void Retry(IReadOnlyCollection<LocalPluginAttempt> attempts, string? disable)
    {
        if (_stateStore is null) return;
        var retriedUpdates = new HashSet<Guid>();
        foreach (var failed in OrderRetries(attempts))
        {
            var attempt = failed.Slug == disable ? failed with { TargetState = PersistedPluginState.Deactivated } : failed;
            var updateJournal = _updateJournals?.Read().FirstOrDefault(j => j.AttemptId == attempt.AttemptId);
            if (updateJournal is not null || attempt.Reason.StartsWith("update_", StringComparison.Ordinal))
            {
                if (retriedUpdates.Add(attempt.AttemptId)) RetryUpdate(updateJournal);
                continue;
            }
            var context = ManagedContext(attempt.Slug);
            var expectedState = attempt.TargetState == PersistedPluginState.Activated
                ? EEditorPluginState.Activated
                : EEditorPluginState.Deactivated;
            if (_stateStore.GetLocal(attempt.Slug) is null && context?.State == expectedState)
            {
                continue; // an earlier retry of this loop already completed it, e.g. as a dependency
            }

            if (context is null && attempt.Reason == "invalid_plugin_version" &&
                !IsManagedPlugin(attempt.Slug))
            {
                if (_plainPluginObserver?.RetryInvalid(attempt.Slug) == true)
                {
                    var plain = _contexts.FirstOrDefault(c => c.Slug == attempt.Slug);
                    if (plain is not null)
                    {
                        plain.State = GetEnabledPluginSlugs().Contains(attempt.Slug)
                            ? EEditorPluginState.Activated : EEditorPluginState.Deactivated;
                    }
                }

                continue;
            }

            if (context is null && !IsManagedPlugin(attempt.Slug))
            {
                _ePluginContext?.Logger.Error($"Cannot retry {attempt.Slug}: {attempt.Reason} requires manual recovery.");
                continue;
            }

            if (context is null)
            {
                try
                {
                    _host.SetPluginEnabled(attempt.Slug, true);
                    context = ManagedContext(attempt.Slug);
                }
                catch (Exception ex)
                {
                    _ePluginContext?.Logger.Error($"Cannot recreate {attempt.Slug} for manual retry: {ex.Message}");
                }
            }

            if (context is null)
            {
                _ePluginContext?.Logger.Error(
                    $"Cannot retry {attempt.Slug}: no editor plugin instance is available. Repair it manually.");
                continue;
            }

            context.RefreshMetadata(ReadInstalledMetadata(context.Slug)); // picks up a repaired plugin.cfg, e.g. a version that was missing
            if (attempt.TargetState == PersistedPluginState.Activated)
            {
                EnableEPlugin(context, manualRetry: true);
            }
            else
            {
                DisableEPlugin(context, manualRetry: true);
                if (context.State == EEditorPluginState.Deactivated && _host.IsPluginEnabled(context.Slug))
                {
                    _host.SetPluginEnabled(context.Slug, false);
                }
            }
        }
    }

    /// <summary>
    /// Orders blocked attempts so a failed hard dependency is retried before the plugins that need it. Otherwise
    /// a dependant would be retried while its dependency is still in error and fail again.
    /// </summary>
    internal IReadOnlyList<LocalPluginAttempt> OrderRetries(IReadOnlyCollection<LocalPluginAttempt> attempts)
    {
        var bySlug = attempts.ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var ordered = new List<LocalPluginAttempt>(attempts.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(LocalPluginAttempt attempt)
        {
            if (!visited.Add(attempt.Slug))
            {
                return; // already ordered, or a dependency cycle
            }

            foreach (var dependency in GetHardDependencySlugs(attempt.Slug))
            {
                if (bySlug.TryGetValue(dependency, out var blockedDependency))
                {
                    Visit(blockedDependency);
                }
            }

            ordered.Add(attempt);
        }

        foreach (var attempt in attempts)
        {
            Visit(attempt);
        }

        return ordered;
    }

    private IEnumerable<string> GetHardDependencySlugs(string slug)
    {
        var context = ManagedContext(slug);
        if (context is null)
        {
            return [];
        }

        if (context.IsRecipeCreated)
        {
            return context.Builder.PluginRecipe.PluginDependencies.Select(d => d.Slug);
        }

        try
        {
            // a throw-away builder: a failing CreateRecipe must not leave a half-filled recipe on the context
            var builder = EEditorPluginBuilder.Create();
            context.Plugin!.CreateRecipe(builder);
            return builder.PluginRecipe.PluginDependencies.Select(d => d.Slug);
        }
        catch (Exception)
        {
            return []; // the retry itself reports the recipe failure
        }
    }
}
#endif
