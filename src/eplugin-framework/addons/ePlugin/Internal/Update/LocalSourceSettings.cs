#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class LocalSourceConfig
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public List<string> Directories { get; set; } = [];
}

/// <summary>
/// The local plugin directories of the current user. They live in the editor's per-user configuration, never in a
/// project, and are shared by all projects. A directory that does not exist is kept: it may be an unmounted drive or
/// network share that reappears later.
/// </summary>
/// <remarks>
/// Every editor that is open writes the same file, so each change reads it again first instead of saving a list that
/// may be outdated. A file that cannot be read, or that a newer ePlugin version wrote, is never overwritten: the list
/// is read-only until the user repairs or deletes it.
/// </remarks>
internal sealed class LocalSourceSettings(string path)
{
    private LocalSourceConfig _config = new();
    public string FilePath { get; } = path;
    public IReadOnlyList<string> Directories => _config.Directories;
    /// <summary>Why the list cannot be changed right now; null when it can.</summary>
    public string? Problem { get; private set; }

    public void Load()
    {
        Problem = null;
        if (!File.Exists(FilePath)) { _config = new(); return; }
        try
        {
            var config = JsonSerializer.Deserialize<LocalSourceConfig>(File.ReadAllText(FilePath), UpdateStateStore.JsonOptions);
            if (config?.Directories is null) throw new JsonException("The file has no directory list.");
            config.Directories = config.Directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(PackageFiles.PathComparer).ToList();
            _config = config;
            if (config.Schema > LocalSourceConfig.CurrentSchema)
                Problem = $"{FilePath} was written by a newer ePlugin version (schema {config.Schema}); update ePlugin to change the list.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _config = new();
            Problem = $"Cannot read {FilePath}: {ex.Message} Repair or delete the file to change the list.";
        }
    }

    /// <summary>Adds an absolute directory; false when it is already listed.</summary>
    /// <exception cref="InvalidOperationException">The file cannot be changed, see <see cref="Problem"/>.</exception>
    public bool Add(string directory)
    {
        var normalized = Normalize(directory);
        return Change(directories => directories.Contains(normalized, PackageFiles.PathComparer) ? null : [.. directories, normalized]);
    }

    /// <inheritdoc cref="Add"/>
    public bool Remove(string directory) =>
        Change(directories => directories.Any(d => PackageFiles.PathComparer.Equals(d, directory))
            ? directories.Where(d => !PackageFiles.PathComparer.Equals(d, directory)).ToList()
            : null);

    /// <summary>Reads the file again, applies the change and saves; the list in memory changes only once saving worked.</summary>
    private bool Change(Func<List<string>, List<string>?> change)
    {
        Load();
        if (Problem is not null) throw new InvalidOperationException(Problem);
        if (change(_config.Directories) is not { } directories) return false;
        var next = new LocalSourceConfig { Directories = directories };
        AtomicJson.Write(FilePath, next);
        _config = next;
        return true;
    }

    internal static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Choose an absolute directory path.", nameof(directory));
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
#endif
