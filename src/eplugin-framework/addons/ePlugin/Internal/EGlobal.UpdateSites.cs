#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
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

    private void InitializeUpdateSites()
    {
        _updateSites = new UpdateSiteSettings(Path.GetFullPath(ProjectSettings.GlobalizePath(UpdateSitesPath)));
        _updateSites.Load();
        if (_updateSites.Problem is { } problem) _ePluginContext?.Logger.Warn($"Update sites: {problem}");
    }

    /// <summary>Reads the file again, as a pull may have changed it since.</summary>
    internal void ReloadUpdateSites() => _updateSites?.Load();

    /// <summary>The update site the project sets for a plugin; null when it uses its plugin.cfg update_url.</summary>
    internal string? UpdateSiteOf(string slug) => _updateSites?.UrlOf(slug);

    /// <summary>Adds or replaces the update site of a plugin; false when nothing changed.</summary>
    /// <exception cref="ArgumentException">The URL is not a supported update site.</exception>
    /// <exception cref="InvalidOperationException">The file cannot be changed, see <see cref="UpdateSitesProblem"/>.</exception>
    internal bool SetUpdateSite(string slug, string url) => ChangeUpdateSites(sites => sites.Set(slug, url));

    /// <inheritdoc cref="SetUpdateSite"/>
    internal bool RemoveUpdateSite(string slug) => ChangeUpdateSites(sites => sites.Remove(slug));

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
