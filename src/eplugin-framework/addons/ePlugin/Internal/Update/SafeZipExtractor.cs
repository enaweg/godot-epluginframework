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
    /// <summary>Extracts the plugin root of the archive (see <see cref="PluginRoot"/>) into <paramref name="destination"/>.</summary>
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
        var root = PluginRoot(entries.Select(e => e.Path));
        if (RootSlug(root) != slug && (requireSlugFolder || root.Count(c => c == '/') > 1))
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
        if (roots.Length > 1) throw new InvalidDataException("Ambiguous package, it has several plugin roots: " + string.Join(", ", roots));
        var outside = folders.FirstOrDefault(f => !f.StartsWith(roots[0], StringComparison.Ordinal));
        if (outside is not null) throw new InvalidDataException($"Ambiguous package, {outside}plugin.cfg is outside the plugin root {roots[0]}.");
        return roots[0];
        static int Depth(string folder) => folder.Count(c => c == '/');
    }

    /// <summary>The slug a plugin root names: its folder name, or null for a plugin at the archive root.</summary>
    internal static string? RootSlug(string root) => root.Length == 0 ? null : root.TrimEnd('/').Split('/')[^1];

    internal static bool IsPluginConfig(string path) => path == "plugin.cfg" || path.EndsWith("/plugin.cfg", StringComparison.Ordinal);
}
#endif
