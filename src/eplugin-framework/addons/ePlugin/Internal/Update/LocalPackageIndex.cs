#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>The plugin inside a ZIP file of a local plugin directory, read from its root plugin.cfg.</summary>
/// <param name="Root">The plugin root inside the archive, e.g. <c>addons/my_plugin/</c>, see <see cref="SafeZipExtractor.PluginRoot"/>.</param>
/// <param name="Slug">The name of the root folder, which is the slug of the plugin the package belongs to.</param>
internal sealed record LocalPackage(string ZipPath, string Root, string Slug, string Version, DateTime ModifiedUtc);
/// <param name="Packages">The plugin packages below the directory, also those another listed directory contains too.</param>
internal sealed record LocalDirectoryState(string Path, bool Exists, int Packages);
/// <param name="Cached">Known from an earlier indexing, as the archive did not change since.</param>
internal sealed record LocalIndexFailure(string Path, string Message, bool Cached = false);

/// <summary>An immutable snapshot of every plugin package found in the local plugin directories.</summary>
internal sealed record LocalPackageIndex(IReadOnlyList<LocalPackage> Packages, IReadOnlyList<LocalDirectoryState> Directories,
    int Archives, IReadOnlyList<LocalIndexFailure> Failures)
{
    public static readonly LocalPackageIndex Empty = new([], [], 0, []);
    public IEnumerable<LocalPackage> Matching(string slug) => Packages.Where(p => p.Slug == slug);
    /// <summary>What the last indexing found for a listed directory; null when it was added since.</summary>
    public LocalDirectoryState? StateOf(string directory) => Directories.FirstOrDefault(d => PackageFiles.PathComparer.Equals(d.Path, directory));
}

internal static class LocalPackageIndexer
{
    private const long MaximumConfig = 64 * 1024;
    private static readonly EnumerationOptions Search = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
        // Skipping reparse points keeps linked folders from indexing a tree twice or looping.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    /// <inheritdoc cref="Build(IReadOnlyList{string}, LocalIndexCache?, CancellationToken)"/>
    public static LocalPackageIndex Build(IReadOnlyList<string> directories, CancellationToken ct) => Build(directories, null, ct);

    /// <summary>
    /// Indexes every *.zip below the given directories. Only the central directory and the plugin.cfg entries are
    /// read; nothing is extracted. A directory that does not exist is skipped and reported as missing. With a cache,
    /// archives whose size and modification time did not change are not opened again.
    /// </summary>
    public static LocalPackageIndex Build(IReadOnlyList<string> directories, LocalIndexCache? cache, CancellationToken ct)
    {
        var cached = cache?.Load() ?? new Dictionary<string, LocalIndexCacheEntry>();
        var packages = new List<LocalPackage>();
        var failures = new List<LocalIndexFailure>();
        var states = new List<LocalDirectoryState>();
        // Nested or overlapping directories read and list the same archive only once.
        var read = new Dictionary<string, LocalIndexCacheEntry>(PackageFiles.PathComparer);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) { states.Add(new(directory, false, 0)); continue; }
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.zip", Search))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.GetFullPath(file);
                    if (!read.TryGetValue(path, out var entry))
                    {
                        read[path] = entry = Inspect(path, cached, out var known);
                        if (entry.Package is not null) packages.Add(entry.Package);
                        if (entry.Failure is not null) failures.Add(new(path, entry.Failure, known));
                    }
                    if (entry.Package is not null) count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(new(directory, ex.Message)); }
            states.Add(new(directory, true, count));
        }
        cache?.Save(read.Values);
        return new(packages, states, read.Count, failures);
    }

    private static LocalIndexCacheEntry Inspect(string path, IReadOnlyDictionary<string, LocalIndexCacheEntry> cached, out bool known)
    {
        known = false;
        var info = new FileInfo(path);
        long length; DateTime modified;
        try { length = info.Length; modified = info.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(path, -1, default, null, ex.Message); }
        if (cached.TryGetValue(path, out var entry) && entry.Length == length && entry.ModifiedUtc == modified) { known = true; return entry; }
        try { return new(path, length, modified, Read(path), null); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { return new(path, length, modified, null, ex.Message); }
    }

    /// <summary>
    /// The plugin inside one archive, as <see cref="SafeZipExtractor"/> would extract it for a local package; null for a
    /// ZIP without any plugin.cfg, which is not a plugin package.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is unsafe, ambiguous or has no usable root plugin.cfg.</exception>
    internal static LocalPackage? Read(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        // Any ZIP without a plugin.cfg is ignored without inspecting it further.
        if (!archive.Entries.Any(e => SafeZipExtractor.IsPluginConfig(e.FullName.Replace('\\', '/')))) return null;
        var entries = SafeZipExtractor.Inspect(archive);
        var root = SafeZipExtractor.PluginRoot(entries.Select(e => e.Path));
        var slug = SafeZipExtractor.RootSlug(root)
            ?? throw new InvalidDataException("plugin.cfg is at the archive root; put the plugin in a folder named like its slug.");
        var entry = entries.First(e => e.Path == root + "plugin.cfg").Entry;
        if (entry.Length > MaximumConfig) throw new InvalidDataException($"{root}plugin.cfg is too large.");
        string text;
        using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
        if (!SemVer.TryParse(PluginIni.Parse(text).GetValueOrDefault("version"), out var version))
            throw new InvalidDataException($"{root}plugin.cfg has no comparable version.");
        return new(file, root, slug, version.ToString(), File.GetLastWriteTimeUtc(file));
    }
}

/// <summary>
/// Offers the packages of the local plugin directories as versions of an installed plugin. It works on an index built
/// in the background, so checks and version lists never touch the disk.
/// </summary>
internal static class LocalDirectorySource
{
    /// <summary>One candidate per version, newest first; of several files with the same version the newest file wins.</summary>
    public static IReadOnlyList<UpdateCandidate> Versions(LocalPackageIndex index, PluginUpdateTarget target, UpdateCheckOptions options) =>
        index.Matching(target.Slug)
            .Select(p => (Package: p, Version: SemVer.TryParse(p.Version, out var v) ? v : default))
            .Where(p => options.AllowPrerelease || p.Version.Prerelease is null)
            .OrderByDescending(p => p.Version).ThenByDescending(p => p.Package.ModifiedUtc)
            .DistinctBy(p => p.Version.Key)
            .Select(p => new UpdateCandidate(target.Slug, target.Name, target.InstalledVersion, p.Package.Version, null,
                null, null, new LocalZipPackageRef(p.Package.ZipPath)))
            .ToArray();
}
#endif
