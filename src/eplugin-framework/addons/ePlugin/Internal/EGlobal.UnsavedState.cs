#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Accepted licenses and shown welcome pages the shared state file could not save, kept for the session until a later
/// write succeeds.
/// </summary>
internal sealed partial class EGlobal
{
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
}
#endif
