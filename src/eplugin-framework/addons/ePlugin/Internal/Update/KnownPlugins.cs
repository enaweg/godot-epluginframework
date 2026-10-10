#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>A popular Godot add-on whose update site ePlugin knows, for plugins that do not name a working one.</summary>
/// <param name="Slug">The add-on's folder in res://addons, as its latest release installs it.</param>
/// <param name="UpdateUrl">Where its releases are published, or its tag page when it only tags versions.</param>
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
/// The entries live in KnownPlugins.Data.cs, written by tools/generate_known_plugins.cs from the add-ons that
/// tools/build_godot_addons.cs lists. Each slug is the plugin folder
/// in the add-on's repository at its latest release or version tag; the packages are not installed or validated, so an
/// update from the list can still be refused like any other.
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
