#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>What indexing read from one ZIP file: its package, or why it was refused; neither for a ZIP without a plugin.</summary>
internal sealed record LocalIndexCacheEntry(string Path, long Length, DateTime ModifiedUtc, LocalPackage? Package, string? Failure);

internal sealed class LocalIndexCacheFile
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public int Indexer { get; set; } = LocalIndexCache.IndexerVersion;
    public List<LocalIndexCacheEntry> Archives { get; set; } = [];
}

/// <summary>
/// Remembers what indexing read from each ZIP file, keyed by its path, size and modification time, so indexing again
/// (every editor start and every assembly reload) only opens new or changed archives. It is a per-user file next to the
/// list of local plugin directories, shared by all editors, and only a cache: anything wrong with it just means the
/// archives are read again. A file written by a newer schema or indexer is used by nobody here and never overwritten.
/// </summary>
internal sealed class LocalIndexCache(string path)
{
    /// <summary>Raise when what <see cref="LocalPackageIndexer.Read"/> takes from an archive changes, so it reads them again.</summary>
    public const int IndexerVersion = 2;
    private bool _newer;
    public string FilePath { get; } = path;

    public IReadOnlyDictionary<string, LocalIndexCacheEntry> Load()
    {
        _newer = false;
        try
        {
            var file = JsonSerializer.Deserialize<LocalIndexCacheFile>(File.ReadAllText(FilePath), UpdateStateStore.JsonOptions);
            if (file is null) return new Dictionary<string, LocalIndexCacheEntry>();
            _newer = file.Schema > LocalIndexCacheFile.CurrentSchema || file.Indexer > IndexerVersion;
            if (file.Schema != LocalIndexCacheFile.CurrentSchema || file.Indexer != IndexerVersion || file.Archives is null)
                return new Dictionary<string, LocalIndexCacheEntry>();
            return file.Archives.Where(a => a?.Path is not null).DistinctBy(a => a.Path, PackageFiles.PathComparer)
                .ToDictionary(a => a.Path, PackageFiles.PathComparer);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Dictionary<string, LocalIndexCacheEntry>();
        }
    }

    /// <summary>Replaces the cache with the archives of the last indexing; failing to write it is not an error.</summary>
    public void Save(IEnumerable<LocalIndexCacheEntry> archives)
    {
        if (_newer) return;
        try { AtomicJson.Write(FilePath, new LocalIndexCacheFile { Archives = archives.ToList() }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
#endif
