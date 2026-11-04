#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Where plugins and GDExtension add-ons are below <c>res://addons</c>, and the slugs that name them. Pure: it works on
/// file system paths.
/// <para>
/// A plugin is found like Godot's Plugins list finds it (<c>EditorPluginSettings::_get_plugins</c>, the same from Godot
/// 4.4 to 4.7): every folder below the addons folder whose name does not start with <c>.</c> is a plugin when it has a
/// <c>plugin.cfg</c>, and otherwise is searched the same way. Godot does not look inside a plugin's folder, so a
/// <c>plugin.cfg</c> deeper in it is a sub-plugin that belongs to that plugin. Its slug is the folder's path below
/// <c>res://addons</c> with <c>/</c> (<c>acme/native_tool</c>), the name <c>EditorInterface.set_plugin_enabled</c> takes
/// (Godot reads it as <c>res://addons/&lt;slug&gt;/plugin.cfg</c>, the path <c>editor_plugins/enabled</c> stores), so a
/// plugin directly below the addons folder keeps its folder name as its slug.
/// </para>
/// <para>
/// Godot loads every <c>.gdextension</c> file of the project outside hidden folders, whether a plugin is enabled or not.
/// A GDExtension add-on is a folder that the same search reaches, without a <c>plugin.cfg</c> in it or anywhere below it,
/// that holds a <c>.gdextension</c> file outside hidden folders: the outermost such folder, so a folder directly below the
/// addons folder stays the add-on it was. A folder that holds plugins further down is no add-on; one of its other
/// folders can be.
/// </para>
/// </summary>
internal static class AddonLayout
{
    /// <summary>The <c>res://</c> path of the addons folder with a trailing <c>/</c>.</summary>
    public const string ResourcePrefix = "res://addons/";

    private const string Config = "plugin.cfg";

    /// <summary>The plugins and GDExtension add-ons below an addons folder, by slug.</summary>
    internal sealed record Layout(IReadOnlyList<string> Plugins, IReadOnlyList<string> ExtensionAddons);

    /// <summary>
    /// Whether <paramref name="slug"/> names a folder below the addons folder: one or more folder names joined with
    /// <c>/</c>, none empty, <c>.</c> or <c>..</c>, and without <c>\</c> or <c>:</c>.
    /// </summary>
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Split('/').All(part => ValidPart(part) && part.IndexOf(':') < 0);

    /// <summary>
    /// Like <see cref="IsValidSlug"/>, but also accepting <c>:</c>, which the state files have always accepted in a
    /// slug (on Linux and macOS it may be part of a folder name).
    /// </summary>
    public static bool IsStoredSlug(string? slug) => !string.IsNullOrWhiteSpace(slug) && slug.Split('/').All(ValidPart);

    private static bool ValidPart(string part) => !string.IsNullOrWhiteSpace(part) && part is not ("." or "..") && part.IndexOf('\\') < 0;

    /// <summary>Whether the plugin lies in a subfolder below the addons folder rather than directly in it.</summary>
    public static bool IsNested(string slug) => slug.Contains('/');

    /// <summary>The name of the plugin's own folder, the last part of its slug.</summary>
    public static string Leaf(string slug) => slug[(slug.LastIndexOf('/') + 1)..];

    /// <summary>
    /// The slug of a <c>res://</c> directory below <c>res://addons</c> (<c>res://addons/acme/tool</c> is
    /// <c>acme/tool</c>); null for one outside it.
    /// </summary>
    public static string? SlugOf(string directory)
    {
        var trimmed = directory.TrimEnd('/');
        if (!trimmed.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return null;
        var slug = trimmed[ResourcePrefix.Length..];
        return IsStoredSlug(slug) ? slug : null;
    }

    /// <summary>
    /// The slug of an <c>editor_plugins/enabled</c> entry (<c>res://addons/&lt;slug&gt;/plugin.cfg</c>); null for a
    /// plugin outside the addons folder, which ePlugin does not support.
    /// </summary>
    public static string? SlugOfConfig(string path) =>
        path.EndsWith("/" + Config, StringComparison.Ordinal) ? SlugOf(path[..^(Config.Length + 1)]) is { } slug && IsValidSlug(slug) ? slug : null : null;

    /// <summary>Finds the plugins and GDExtension add-ons below <paramref name="addonsDirectory"/> (a file system path).</summary>
    public static Layout Scan(string addonsDirectory)
    {
        var plugins = new List<string>();
        var addons = new List<string>();
        var visited = new HashSet<string>(PackageFiles.PathComparer);
        Walk(addonsDirectory, "");
        return new(plugins, addons);

        void Walk(string directory, string prefix)
        {
            // a folder linked to one it is in would be searched forever
            if (!visited.Add(RealPath(directory))) return;
            foreach (var child in Folders(directory))
            {
                var slug = prefix + Path.GetFileName(child);
                if (File.Exists(Path.Combine(child, Config)))
                {
                    plugins.Add(slug);
                    continue;
                }
                var (hasConfig, hasExtension) = Contents(child);
                if (hasConfig) Walk(child, slug + "/");
                else if (hasExtension) addons.Add(slug);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="slug"/> is a GDExtension add-on below <paramref name="addonsDirectory"/> (a file system
    /// path): the search of <see cref="Scan"/> reaches its folder, which has no <c>plugin.cfg</c> in it or below it and a
    /// <c>.gdextension</c> file outside hidden folders.
    /// </summary>
    public static bool IsExtensionAddon(string addonsDirectory, string slug)
    {
        if (!IsValidSlug(slug)) return false;
        try
        {
            var parts = slug.Split('/');
            var directory = addonsDirectory;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith('.')) return false;
                directory = Path.Combine(directory, parts[i]);
                if (!Directory.Exists(directory) || File.Exists(Path.Combine(directory, Config))) return false;
                var (hasConfig, hasExtension) = Contents(directory);
                // a folder above it is searched only while it holds plugins further down
                if (i < parts.Length - 1 ? !hasConfig : hasConfig || !hasExtension) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The plugin whose folder holds the plugin <paramref name="slug"/> below <paramref name="addonsDirectory"/> (a file
    /// system path), which makes it a sub-plugin Godot does not list: the outermost folder above it with a
    /// <c>plugin.cfg</c>. Null for a plugin Godot lists.
    /// </summary>
    public static string? ContainingPlugin(string addonsDirectory, string slug)
    {
        var parts = slug.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var outer = string.Join('/', parts[..i]);
            if (File.Exists(Path.Combine(addonsDirectory, outer, Config))) return outer;
        }
        return null;
    }

    /// <summary>
    /// The folders below the plugin folder <paramref name="pluginDirectory"/> (a file system path) that have a
    /// <c>plugin.cfg</c> outside hidden folders, as paths relative to it with <c>/</c>: the sub-plugins Godot does not
    /// list, since they belong to the plugin.
    /// </summary>
    public static IReadOnlyList<string> SubPlugins(string pluginDirectory)
    {
        var found = new List<string>();
        try
        {
            if (Directory.Exists(pluginDirectory)) Collect(pluginDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a folder that cannot be read holds none that can be told */ }
        return found;

        void Collect(string directory)
        {
            foreach (var child in Folders(directory).Where(f => !IsLink(f)))
            {
                if (File.Exists(Path.Combine(child, Config))) found.Add(Path.GetRelativePath(pluginDirectory, child).Replace('\\', '/'));
                Collect(child);
            }
        }
    }

    /// <summary>The folders of <paramref name="directory"/> Godot's search looks at: not hidden, ordered by name.</summary>
    private static string[] Folders(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Whether the folder holds a <c>plugin.cfg</c> or a <c>.gdextension</c> file (other than a runtime copy) below it,
    /// outside hidden folders and folders linked into it.
    /// </summary>
    private static (bool HasConfig, bool HasExtension) Contents(string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        bool hasConfig = false, hasExtension = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part.StartsWith('.'))) continue;
                var name = Path.GetFileName(file);
                // plugin.cfg in the folder itself makes it a plugin, which the callers tell first
                if (name.Equals(Config, PackageFiles.PathComparison) && relative.Contains('/')) hasConfig = true;
                else if (GDExtensionConfig.IsExtensionFile(name) && !PackageFiles.Skip(relative)) hasExtension = true;
                if (hasConfig && hasExtension) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was read so far */ }
        return (hasConfig, hasExtension);
    }

    private static bool IsLink(string directory)
    {
        try { return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string RealPath(string directory)
    {
        try { return Path.GetFullPath(new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Path.GetFullPath(directory); }
    }
}
#endif
