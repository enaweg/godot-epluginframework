#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>One addon inside a ZIP file of a local plugin directory, read from its plugin.cfg.</summary>
/// <param name="Root">The addon folder inside the archive, e.g. <c>addons/my_plugin/</c>; empty for an addon at the root.</param>
/// <param name="Slug">The slug of an <c>addons/&lt;slug&gt;/</c> layout; null for an addon at the root or in a wrapper folder.</param>
internal sealed record LocalPackage(string ZipPath, string Root, string? Slug, string Name, string Version, DateTime ModifiedUtc)
{
    /// <summary>
    /// An <c>addons/&lt;slug&gt;/</c> package belongs to the plugin with that slug. Without one, the wrapper folder
    /// name or the plugin name identifies the plugin, the same way <see cref="SafeZipExtractor"/> falls back to it.
    /// </summary>
    public bool Matches(string slug, string name) =>
        Slug is not null ? Slug == slug : Root.TrimEnd('/') == slug || Name.Length > 0 && Name == name.Trim();
}
internal sealed record LocalDirectoryState(string Path, bool Exists);
internal sealed record LocalIndexFailure(string Path, string Message);

/// <summary>An immutable snapshot of every plugin package found in the local plugin directories.</summary>
internal sealed record LocalPackageIndex(IReadOnlyList<LocalPackage> Packages, IReadOnlyList<LocalDirectoryState> Directories,
    int Archives, IReadOnlyList<LocalIndexFailure> Failures)
{
    public static readonly LocalPackageIndex Empty = new([], [], 0, []);
    public IEnumerable<LocalPackage> Matching(string slug, string name) => Packages.Where(p => p.Matches(slug, name));
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

    /// <summary>
    /// Indexes every *.zip below the given directories. Only the central directory and the plugin.cfg entries are
    /// read; nothing is extracted. A directory that does not exist is skipped and reported as missing.
    /// </summary>
    public static LocalPackageIndex Build(IReadOnlyList<string> directories, CancellationToken ct)
    {
        var packages = new List<LocalPackage>();
        var failures = new List<LocalIndexFailure>();
        var states = new List<LocalDirectoryState>();
        var seen = new HashSet<string>(PackageFiles.PathComparer);
        foreach (var directory in directories)
        {
            var exists = Directory.Exists(directory);
            states.Add(new(directory, exists));
            if (!exists) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.zip", Search))
                {
                    ct.ThrowIfCancellationRequested();
                    // Nested or overlapping directories list the same archive only once.
                    if (!seen.Add(Path.GetFullPath(file))) continue;
                    try { packages.AddRange(Read(file)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    { failures.Add(new(file, ex.Message)); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(new(directory, ex.Message)); }
        }
        return new(packages, states, seen.Count, failures);
    }

    /// <summary>The addons inside one archive that <see cref="SafeZipExtractor"/> can extract.</summary>
    internal static IReadOnlyList<LocalPackage> Read(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        // Any ZIP without a plugin.cfg is not a plugin package and is ignored without inspecting it further.
        if (!names.Any(n => n == "plugin.cfg" || n.EndsWith("/plugin.cfg", StringComparison.Ordinal))) return [];
        var entries = SafeZipExtractor.Inspect(archive);
        var paths = entries.Select(e => e.Path).ToArray();
        var roots = SafeZipExtractor.AddonRoots(paths).GroupBy(r => r.Slug)
            // The extractor refuses a slug found in more than one addons/ folder.
            .Where(g => g.Count() == 1).Select(g => (Slug: (string?)g.Key, g.Single().Root)).ToList();
        var loose = SafeZipExtractor.LooseRoots(paths);
        if (loose.Length == 1) roots.Add((null, loose[0]));
        var modified = File.GetLastWriteTimeUtc(file);
        var result = new List<LocalPackage>();
        foreach (var (slug, root) in roots)
        {
            var entry = entries.First(e => e.Path == root + "plugin.cfg").Entry;
            if (entry.Length > MaximumConfig) throw new InvalidDataException($"{root}plugin.cfg is too large.");
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            var metadata = PluginIni.Parse(text);
            if (!SemVer.TryParse(metadata.GetValueOrDefault("version"), out var version)) continue;
            result.Add(new(file, root, slug, metadata.GetValueOrDefault("name")?.Trim() ?? "", version.ToString(), modified));
        }
        return result;
    }
}

/// <summary>
/// Offers the packages of the local plugin directories as versions of an installed plugin. It works on an index built
/// in the background, so checks and version lists never touch the disk.
/// </summary>
internal sealed class LocalDirectorySource(LocalPackageIndex index) : IUpdateSource, IVersionListSource
{
    public Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
        Task.FromResult(Versions(target, options).FirstOrDefault());

    public Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct) =>
        Task.FromResult(Versions(target, options));

    /// <summary>One candidate per version, newest first; of several files with the same version the newest file wins.</summary>
    public IReadOnlyList<UpdateCandidate> Versions(PluginUpdateTarget target, UpdateCheckOptions options) =>
        index.Matching(target.Slug, target.Name)
            .Select(p => (Package: p, Version: SemVer.TryParse(p.Version, out var v) ? v : default))
            .Where(p => options.AllowPrerelease || p.Version.Prerelease is null)
            .OrderByDescending(p => p.Version).ThenByDescending(p => p.Package.ModifiedUtc)
            .DistinctBy(p => p.Package.Version)
            .Select(p => new UpdateCandidate(target.Slug, target.Name, target.InstalledVersion, p.Package.Version, p.Package.ZipPath,
                null, null, new LocalZipPackageRef(p.Package.ZipPath)))
            .ToArray();
}
#endif
