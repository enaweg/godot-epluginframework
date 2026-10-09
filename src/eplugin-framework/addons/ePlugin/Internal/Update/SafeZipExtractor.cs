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
    public static void Extract(string archivePath, string slug, string destination, CancellationToken ct)
    {
        PackageFiles.Normalize(slug);
        if (slug.Contains('/')) throw new InvalidDataException("Invalid addon slug.");
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = Inspect(archive);
        var roots = AddonRoots(entries.Select(e => e.Path)).Where(r => r.Slug == slug).Select(r => r.Root).ToArray();
        if (roots.Length == 0) roots = LooseRoots(entries.Select(e => e.Path));
        if (roots.Length != 1) throw new InvalidDataException(roots.Length == 0 ? $"No plugin.cfg for '{slug}' found in package." : "Ambiguous package: " + string.Join(", ", roots));
        var root = roots[0];
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

    /// <summary>Every <c>addons/&lt;slug&gt;/</c> folder holding a plugin.cfg, at any depth, with its slug.</summary>
    internal static IEnumerable<(string Slug, string Root)> AddonRoots(IEnumerable<string> paths) =>
        paths.Select(p => p.Split('/')).Where(parts => parts.Length >= 3 && parts[^1] == "plugin.cfg" && parts[^3] == "addons")
            .Select(parts => (parts[^2], string.Join('/', parts[..^1]) + "/"));

    /// <summary>An addon at the archive root or in one wrapper folder, used when no matching addons/&lt;slug&gt;/ exists.</summary>
    internal static string[] LooseRoots(IEnumerable<string> paths) =>
        paths.Where(p => p == "plugin.cfg" || p.EndsWith("/plugin.cfg", StringComparison.Ordinal) && p.Count(c => c == '/') == 1)
            .Select(p => p[..^"plugin.cfg".Length]).ToArray();
}
#endif
