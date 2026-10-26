#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Update sites the project sets for its plugins, in the committed res://addons/eplugin-update-sites.json. They replace a
/// plugin's plugin.cfg update_url, which is still used when the project's site is unsupported or fails.
/// </summary>
internal sealed partial class EGlobal
{
    internal const string UpdateSitesPath = "res://addons/eplugin-update-sites.json";
    private UpdateSiteSettings? _updateSites;

    internal IReadOnlyList<UpdateSiteEntry> UpdateSites => _updateSites?.Sites ?? [];
    /// <summary>Why the update sites cannot be changed; null when they can.</summary>
    internal string? UpdateSitesProblem => _updateSites?.Problem;

    internal void InitializeUpdateSites()
    {
        _updateSites = new UpdateSiteSettings(Path.GetFullPath(Path.Combine(_host.ProjectRoot, UpdateSitesPath["res://".Length..])));
        _updateSites.Load();
        if (_updateSites.Problem is { } problem) _ePluginContext?.Logger.Warn($"Update sites: {problem}");
    }

    /// <summary>Whether ePlugin's built-in list of add-on update sites is used, see <see cref="KnownPlugins"/>.</summary>
    internal static bool UseKnownPlugins => ProjectSettings.GetSetting(KnownPlugins.SettingKey, true).AsBool();

    /// <summary>The built-in entry of a plugin; null when it has none or the list is turned off.</summary>
    internal static KnownPlugin? KnownPluginOf(string slug) => UseKnownPlugins ? KnownPlugins.Find(slug) : null;

    /// <summary>Reads the file again, as a pull may have changed it since.</summary>
    internal void ReloadUpdateSites() => _updateSites?.Load();

    /// <summary>The update site the project sets for a plugin; null when it uses its plugin.cfg update_url.</summary>
    internal string? UpdateSiteOf(string slug) => _updateSites?.UrlOf(slug);

    /// <summary>
    /// The plugins whose update site only repeats the one of the built-in list, see
    /// <see cref="UpdateSiteSettings.RepeatsBuiltIn"/>.
    /// </summary>
    internal IReadOnlySet<string> UpdateSitesRepeatingBuiltIn() => UpdateSites
        .Where(site => UpdateSiteSettings.RepeatsBuiltIn(site.Url,
            EditorPluginExtensions.ReadMetadata(_host.PluginDirectory(site.Slug) + "/plugin.cfg")?.UpdateUrl, KnownPluginOf(site.Slug)?.UpdateUrl))
        .Select(site => site.Slug).ToHashSet(StringComparer.Ordinal);

    /// <summary>Adds or replaces the update site of a plugin; false when nothing changed.</summary>
    /// <exception cref="ArgumentException">The URL is not a supported update site.</exception>
    /// <exception cref="InvalidOperationException">The file cannot be changed, see <see cref="UpdateSitesProblem"/>.</exception>
    internal bool SetUpdateSite(string slug, string url) => ChangeUpdateSites(sites => sites.Set(slug, url));

    /// <inheritdoc cref="SetUpdateSite"/>
    internal bool RemoveUpdateSite(string slug) => ChangeUpdateSites(sites => sites.Remove(slug));

    /// <summary>
    /// Removes the update sites that only repeat the built-in list (<see cref="UpdateSitesRepeatingBuiltIn"/>), which
    /// leaves the sites that are tried as they are; false when there are none.
    /// </summary>
    /// <inheritdoc cref="SetUpdateSite" path="/exception"/>
    internal bool RemoveUpdateSitesRepeatingBuiltIn()
    {
        // A pull may have changed the sites since they were shown.
        ReloadUpdateSites();
        var slugs = UpdateSitesRepeatingBuiltIn();
        return slugs.Count > 0 && ChangeUpdateSites(sites => sites.Remove(slugs));
    }

    private bool ChangeUpdateSites(Func<UpdateSiteSettings, bool> change)
    {
        if (_updateSites is null) return false;
        if (!change(_updateSites)) return false;
        // An update found at a site the plugin no longer uses is not offered anymore.
        RefreshPendingUpdates();
        return true;
    }
}
#endif
