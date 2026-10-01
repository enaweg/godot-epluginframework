#if TOOLS
using System;
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
        var roots = entries.Where(e => e.Path == $"addons/{slug}/plugin.cfg" || e.Path.EndsWith($"/addons/{slug}/plugin.cfg", StringComparison.Ordinal))
            .Select(e => e.Path[..^"plugin.cfg".Length]).ToArray();
        if (roots.Length == 0)
            roots = entries.Where(e => e.Path == "plugin.cfg" || e.Path.EndsWith("/plugin.cfg", StringComparison.Ordinal) && e.Path.Count(c => c == '/') == 1)
                .Select(e => e.Path[..^"plugin.cfg".Length]).ToArray();
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
}
#endif
