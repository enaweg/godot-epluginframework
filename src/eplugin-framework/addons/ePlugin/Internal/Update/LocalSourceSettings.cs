#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class LocalSourceConfig
{
    public int Schema { get; set; } = 1;
    public List<string> Directories { get; set; } = [];
}

/// <summary>
/// The local plugin directories of the current user. They live in the editor's per-user configuration, never in a
/// project, and are shared by all projects. A directory that does not exist is kept: it may be an unmounted drive or
/// network share that reappears later.
/// </summary>
internal sealed class LocalSourceSettings(string path)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private LocalSourceConfig _config = new();
    public string FilePath { get; } = path;
    public IReadOnlyList<string> Directories => _config.Directories;

    public void Load()
    {
        try
        {
            var config = JsonSerializer.Deserialize<LocalSourceConfig>(File.ReadAllText(FilePath), UpdateStateStore.JsonOptions);
            _config = config is { Directories: not null } ? config : new();
            _config.Directories = _config.Directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(PathComparer).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _config = new(); }
    }

    /// <summary>Adds an absolute directory; false when it is already listed.</summary>
    public bool Add(string directory)
    {
        var normalized = Normalize(directory);
        if (_config.Directories.Contains(normalized, PathComparer)) return false;
        _config.Directories.Add(normalized);
        Save();
        return true;
    }

    public bool Remove(string directory)
    {
        if (_config.Directories.RemoveAll(d => PathComparer.Equals(d, directory)) == 0) return false;
        Save();
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

    private void Save() => AtomicJson.Write(FilePath, _config);
}
#endif
