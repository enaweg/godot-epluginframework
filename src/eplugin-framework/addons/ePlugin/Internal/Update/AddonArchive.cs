#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// The add-on in a ZIP file chosen in the ePlugin Manager, read from the archive's central directory and plugin.cfg
/// without extracting anything. It is what staging installs as <c>res://addons/&lt;Slug&gt;</c>
/// (<see cref="SafeZipExtractor.Extract"/> with <c>requireSlugFolder</c>), so only the add-ons the update system can
/// update later are accepted: a plugin with a root plugin.cfg in a folder named like its slug, or a GDExtension without
/// plugin.cfg in an <c>addons/&lt;slug&gt;/</c> folder.
/// </summary>
/// <param name="Root">The add-on's folder inside the archive, e.g. <c>addons/my_plugin/</c>.</param>
/// <param name="Slug">The name of that folder, which becomes the add-on's folder below <c>res://addons</c>.</param>
/// <param name="Name">plugin.cfg's name; the slug for a GDExtension without plugin.cfg.</param>
/// <param name="Version">plugin.cfg's version; "" for a GDExtension without plugin.cfg, whose files name none.</param>
/// <param name="Files">The files staging extracts.</param>
/// <param name="LeftOut">The files of the archive outside the add-on's folder, which are not installed.</param>
internal sealed record AddonArchive(string ZipPath, string Root, string Slug, string Name, string Version, bool IsExtensionAddon,
    int Files, int LeftOut)
{
    private const long MaximumConfig = 64 * 1024;

    /// <summary>The candidate that installs this archive over the installed plugin, or as a new add-on.</summary>
    public UpdateCandidate Candidate(string name, string installedVersion) =>
        new(Slug, name, installedVersion, Version, null, null, null, new LocalZipPackageRef(ZipPath));

    /// <exception cref="InvalidDataException">The archive is unsafe, ambiguous or holds no add-on ePlugin can install.</exception>
    public static AddonArchive Read(string zipPath)
    {
        if (!Path.IsPathFullyQualified(zipPath) || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .zip file.");
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = SafeZipExtractor.Inspect(archive);
        var paths = entries.Select(e => e.Path).ToArray();
        var configured = paths.Any(SafeZipExtractor.IsPluginConfig);
        var root = configured ? SafeZipExtractor.PluginRoot(paths) : SafeZipExtractor.ExtensionRoot(paths, ExtensionSlug(paths));
        var slug = SafeZipExtractor.RootSlug(root)
            ?? throw new InvalidDataException("plugin.cfg is at the root of the ZIP file; ePlugin installs a plugin only from a folder named like it, such as addons/my_plugin/.");
        if (slug.StartsWith('.')) throw new InvalidDataException($"The plugin folder '{slug}' is hidden; Godot would not see it.");
        var name = slug;
        var version = "";
        if (configured)
        {
            var entry = entries.First(e => e.Path == root + "plugin.cfg").Entry;
            if (entry.Length > MaximumConfig) throw new InvalidDataException($"{root}plugin.cfg is too large.");
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            var metadata = PluginIni.Parse(text);
            // Updates compare versions, so a plugin without a comparable one could never be updated.
            if (!SemVer.TryParse(metadata.GetValueOrDefault("version"), out var parsed))
                throw new InvalidDataException($"{root}plugin.cfg has no comparable version.");
            version = parsed.ToString();
            if (!string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("name"))) name = metadata["name"];
        }
        // What SafeZipExtractor.Extract writes: files below the root, no folders, links or skipped files.
        var files = entries.Where(e => !e.Entry.FullName.EndsWith('/') && !e.Entry.FullName.EndsWith('\\') &&
                                       ((e.Entry.ExternalAttributes >> 16) & 0xF000) != 0xA000 && !PackageFiles.Skip(e.Path)).ToArray();
        var inside = files.Count(e => e.Path.StartsWith(root, StringComparison.Ordinal) && e.Path.Length > root.Length);
        return new(zipPath, root, slug, name, version, !configured, inside, files.Length - inside);
    }

    /// <summary>
    /// The slug of a GDExtension without plugin.cfg: nothing in its files names it, so its <c>.gdextension</c> files must
    /// lie in one <c>addons/&lt;slug&gt;/</c> folder, where Godot would load it from.
    /// </summary>
    private static string ExtensionSlug(string[] paths)
    {
        var extensions = paths.Where(p => GDExtensionConfig.IsExtensionFile(p) && !PackageFiles.Skip(p) &&
                                          !p.Split('/').SkipLast(1).Any(part => part.StartsWith('.'))).ToArray();
        if (extensions.Length == 0) throw new InvalidDataException("The ZIP file contains neither a plugin.cfg nor a GDExtension (.gdextension file).");
        var slugs = extensions.Select(path =>
        {
            var parts = path.Split('/');
            var addons = Array.IndexOf(parts, "addons");
            return addons >= 0 && addons + 2 < parts.Length ? parts[addons + 1] : null;
        }).ToArray();
        if (slugs.Any(s => s is null))
            throw new InvalidDataException($"The GDExtension {extensions[Array.IndexOf(slugs, null)]} has no plugin.cfg and is not in an addons/<name>/ folder, so its folder name is unknown.");
        var distinct = slugs.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1) throw new InvalidDataException("The ZIP file contains several GDExtensions: " + string.Join(", ", distinct) + ". Install one add-on at a time.");
        return distinct[0]!;
    }
}
#endif
