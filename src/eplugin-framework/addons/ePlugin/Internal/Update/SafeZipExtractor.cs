#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

internal static class SafeZipExtractor
{
    /// <summary>Extracts the plugin folder of the archive (see <see cref="PackageRoot"/>) into <paramref name="destination"/>.</summary>
    /// <param name="requireSlugFolder">
    /// The root folder must be named like the slug. Downloads may also have the plugin at the archive root or in one
    /// wrapper folder, such as a repository archive, because their update_url already identifies the plugin.
    /// </param>
    public static void Extract(string archivePath, string slug, string destination, CancellationToken ct, bool requireSlugFolder = false)
    {
        PackageFiles.Normalize(slug);
        if (slug.Contains('/')) throw new InvalidDataException("Invalid addon slug.");
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = Inspect(archive);
        var root = PackageRoot(entries.Select(e => e.Path), slug);
        if (requireSlugFolder && RootSlug(root) != slug)
            throw new InvalidDataException($"The plugin root '{(root.Length == 0 ? "/" : root)}' of the package is not a folder named '{slug}'.");
        if (entries.Any(e => e.Path.StartsWith(root, StringComparison.Ordinal) && e.Path.Split('/').Last().Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Packages cannot contain .gitmodules.");
        Directory.CreateDirectory(destination);
        long actual = 0;
        var buffer = new byte[81920];
        foreach (var item in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.Path.StartsWith(root, StringComparison.Ordinal) || item.Path.Length <= root.Length ||
                item.Entry.FullName.EndsWith('/') || item.Entry.FullName.EndsWith('\\') ||
                ((item.Entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || PackageFiles.Skip(item.Path)) continue;
            var target = PackageFiles.Inside(destination, item.Path[root.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = item.Entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long size = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if ((size += count) > PackageFiles.MaximumFile || (actual += count) > PackageFiles.MaximumTotal)
                    throw new InvalidDataException("Archive exceeds actual size limits.");
                output.Write(buffer, 0, count);
            }
            if (size != item.Entry.Length) throw new InvalidDataException("Archive file length mismatch.");
        }
    }

    /// <summary>
    /// Checks the central directory only (entry count, safe and unique paths, declared sizes), so an archive can be
    /// refused or indexed without decompressing anything.
    /// </summary>
    internal static (ZipArchiveEntry Entry, string Path)[] Inspect(ZipArchive archive)
    {
        if (archive.Entries.Count > PackageFiles.MaximumFiles) throw new InvalidDataException("Archive contains too many entries.");
        var entries = archive.Entries.Select(e => (Entry: e, Path: PackageFiles.Normalize(e.FullName))).ToArray();
        if (entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
        long declared = 0;
        foreach (var entry in entries)
        {
            if (entry.Entry.Length > PackageFiles.MaximumFile || (declared += entry.Entry.Length) > PackageFiles.MaximumTotal)
                throw new InvalidDataException("Archive exceeds size limits.");
        }
        return entries;
    }

    /// <summary>
    /// The folder of the package's one root plugin.cfg, the shallowest one ("" at the archive root). plugin.cfg files
    /// below that folder belong to sub-plugins; any other plugin.cfg makes the package ambiguous and it is refused.
    /// </summary>
    internal static string PluginRoot(IEnumerable<string> paths)
    {
        var folders = paths.Where(IsPluginConfig).Select(p => p[..^"plugin.cfg".Length]).ToArray();
        if (folders.Length == 0) throw new InvalidDataException("The package contains no plugin.cfg.");
        var depth = folders.Min(Depth);
        var roots = folders.Where(f => Depth(f) == depth).ToArray();
        if (roots.Length > 1) throw new InvalidDataException("Ambiguous package, it has several plugin roots: " + string.Join(", ", roots) + ". " + AddonPackageValidator.ReportToAuthor);
        var outside = folders.FirstOrDefault(f => !f.StartsWith(roots[0], StringComparison.Ordinal));
        if (outside is not null) throw new InvalidDataException($"Ambiguous package, {outside}plugin.cfg is outside the plugin root {roots[0]}. " + AddonPackageValidator.ReportToAuthor);
        return roots[0];
        static int Depth(string folder) => folder.Count(c => c == '/');
    }

    /// <summary>
    /// The folder of the package to install as <paramref name="slug"/>: its plugin root (see <see cref="PluginRoot"/>)
    /// when that is named like the slug or lies at most one folder deep (at the archive root or in a wrapper folder, as in
    /// a source archive). Otherwise, as in a project or repository that holds other plugins too (the gdUnit4 of its tests,
    /// godot-ai's migration plugin), the shallowest folder named like the slug that holds a plugin.cfg, else the folder of
    /// a GDExtension add-on of that name (see <see cref="ExtensionRoot"/>). Several equally deep folders of that name
    /// make the package ambiguous, and it is refused like one with neither.
    /// </summary>
    internal static string PackageRoot(IEnumerable<string> paths, string slug)
    {
        var all = paths.ToArray();
        string? plugin = null;
        InvalidDataException? ambiguous = null;
        try { plugin = PluginRoot(all); }
        catch (InvalidDataException ex) { ambiguous = ex; }
        if (plugin is not null && (RootSlug(plugin) == slug || plugin.Count(c => c == '/') <= 1)) return plugin;
        var named = all.Where(IsPluginConfig).Select(p => p[..^"plugin.cfg".Length]).Where(f => RootSlug(f) == slug)
            .GroupBy(f => f.Count(c => c == '/')).MinBy(g => g.Key)?.ToArray() ?? [];
        if (named.Length > 1) throw new InvalidDataException($"Ambiguous package, it has several plugin folders named '{slug}': " + string.Join(", ", named) + ". " + AddonPackageValidator.ReportToAuthor);
        if (named.Length == 1) return named[0];
        try { return ExtensionRoot(all, slug); }
        catch (InvalidDataException) when (all.Any(IsPluginConfig))
        {
            throw plugin is null
                ? new InvalidDataException(ambiguous!.Message.Replace(" " + AddonPackageValidator.ReportToAuthor, "", StringComparison.Ordinal) +
                                           $" None of them is named '{slug}'. " + AddonPackageValidator.ReportToAuthor)
                : new InvalidDataException($"The plugin root '{plugin}' of the package is not a folder named '{slug}'.");
        }
    }

    /// <summary>
    /// The root of a GDExtension add-on, which has no plugin.cfg: the shallowest folder named <paramref name="slug"/>
    /// that contains a <c>.gdextension</c> file. Nothing else identifies the add-on, so its folder must be named like it;
    /// several such folders at the same depth make the package ambiguous and it is refused.
    /// </summary>
    internal static string ExtensionRoot(IEnumerable<string> paths, string slug)
    {
        var folders = paths.Where(p => Kinds.GDExtensionConfig.IsExtensionFile(p) && !PackageFiles.Skip(p))
            .SelectMany(p => Ancestors(p).Where(folder => RootSlug(folder) == slug)).Distinct(StringComparer.Ordinal).ToArray();
        if (folders.Length == 0) throw new InvalidDataException($"The package contains no plugin.cfg and no folder named '{slug}' with a GDExtension.");
        var depth = folders.Min(f => f.Count(c => c == '/'));
        var roots = folders.Where(f => f.Count(c => c == '/') == depth).ToArray();
        if (roots.Length > 1) throw new InvalidDataException($"Ambiguous package, it has several GDExtension folders named '{slug}': " + string.Join(", ", roots) + ". " + AddonPackageValidator.ReportToAuthor);
        return roots[0];

        // "a/b/c.gdextension" has the folders "a/" and "a/b/".
        static IEnumerable<string> Ancestors(string path)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) yield return path[..(slash + 1)];
        }
    }

    /// <summary>The slug a plugin root names: its folder name, or null for a plugin at the archive root.</summary>
    internal static string? RootSlug(string root) => root.Length == 0 ? null : root.TrimEnd('/').Split('/')[^1];

    internal static bool IsPluginConfig(string path) => path == "plugin.cfg" || path.EndsWith("/plugin.cfg", StringComparison.Ordinal);
}
#endif
