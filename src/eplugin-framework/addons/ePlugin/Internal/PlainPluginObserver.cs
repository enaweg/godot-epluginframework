#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal;

/// <summary>Records plain plugin toggles only when a consumer requests a refresh.</summary>
internal sealed class PlainPluginObserver(
    PluginStateStore store,
    Func<IReadOnlySet<string>> enabledSlugs,
    Func<string, string?> installedVersion,
    Func<string, bool> isManaged,
    Func<string, bool> isOwned,
    ILogger? logger)
{
    private HashSet<string>? _previous;
    private readonly Dictionary<string, Guid> _failedWrites = new(StringComparer.Ordinal);

    public void Refresh()
    {
        var enabled = enabledSlugs();
        if (_previous is null)
        {
            foreach (var saved in store.SharedStates.Where(s => s.State == PersistedPluginState.Activated &&
                         !enabled.Contains(s.Slug) && !isManaged(s.Slug) && !isOwned(s.Slug)))
            {
                logger?.Warn($"Plugin {saved.Slug} is disabled or missing but recorded as activated; persisted state was preserved.");
            }
        }

        var candidates = enabled.Concat(_previous ?? []).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var slug in candidates)
        {
            if (isManaged(slug) || isOwned(slug) || (store.IsBlocked(slug) && !OwnsFailedWrite(slug)) || store.IsReadOnly)
            {
                continue;
            }

            var active = enabled.Contains(slug);
            var saved = store.GetShared(slug);
            if (_previous is null && active && string.IsNullOrWhiteSpace(installedVersion(slug)))
            {
                store.TryRecordInvalid(slug, installedVersion(slug), "invalid_plugin_version");
                continue;
            }

            // Startup discrepancies are reported by ReloadContexts, never reconciled here.
            if ((_previous is null && saved is not null) ||
                (_previous is not null && _previous.Contains(slug) == active) ||
                (!active && saved is null))
            {
                continue;
            }

            Record(slug, active, manualRetry: OwnsFailedWrite(slug));
        }

        _previous = new HashSet<string>(enabled, StringComparer.Ordinal);
    }

    public bool RetryInvalid(string slug)
    {
        if (isManaged(slug) || isOwned(slug) ||
            store.GetLocal(slug) is not { Reason: "invalid_plugin_version" })
        {
            return false;
        }

        return Record(slug, enabledSlugs().Contains(slug), manualRetry: true);
    }

    private bool OwnsFailedWrite(string slug) =>
        _failedWrites.TryGetValue(slug, out var id) && store.GetLocal(slug)?.AttemptId == id;

    private bool Record(string slug, bool active, bool manualRetry)
    {
        var version = installedVersion(slug);
        if (string.IsNullOrWhiteSpace(version))
        {
            if (!manualRetry)
            {
                store.TryRecordInvalid(slug, version, "invalid_plugin_version");
            }

            return false;
        }

        var target = active ? PersistedPluginState.Activated : PersistedPluginState.Deactivated;
        if (store.TryBeginAttempt(slug, version, target, out var id, manualRetry))
        {
            if (store.TryComplete(id, [new SharedPluginState(slug, version, target)]))
            {
                _failedWrites.Remove(slug);
                return true;
            }

            _failedWrites[slug] = id;
        }

        logger?.Error($"Cannot record observed state for plain plugin {slug}; persisted state was preserved.");
        return false;
    }
}
#endif
