#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>A popular Godot add-on whose update site ePlugin knows, for plugins that do not name a working one.</summary>
/// <param name="Slug">The add-on's folder in res://addons, as its latest release installs it.</param>
/// <param name="UpdateUrl">Where its releases are published.</param>
/// <param name="WebsiteUrl">Its website; the repository when it has none.</param>
/// <param name="SourceUrl">Its source repository.</param>
internal sealed record KnownPlugin(string Slug, string Name, string UpdateUrl, string? DocumentationUrl, string? WebsiteUrl,
    string? SourceUrl);

/// <summary>
/// ePlugin's built-in list of add-on update sites, the last fallback after a project's update site and the plugin.cfg
/// update_url. It also supplies documentation and source links that plugin.cfg does not set. The list is read-only
/// for users and can be turned off with <see cref="SettingKey"/>.
/// </summary>
/// <remarks>
/// The entries live in KnownPlugins.Data.cs. Each slug was taken from the add-on's latest release, installed the way the
/// updater installs it and validated by <see cref="AddonPackageValidator"/>; add-ons that could not be updated that
/// way are left out.
/// </remarks>
internal static partial class KnownPlugins
{
    public const string SettingKey = "eplugin/updates/builtin_update_sites";

    private static readonly Dictionary<string, KnownPlugin> BySlug =
        Entries().GroupBy(p => p.Slug, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Every known add-on, ordered by name.</summary>
    public static IReadOnlyList<KnownPlugin> All { get; } =
        BySlug.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Slug, StringComparer.Ordinal).ToArray();

    public static KnownPlugin? Find(string slug) => BySlug.GetValueOrDefault(slug);
}
#endif
