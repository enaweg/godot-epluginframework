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
    /// <summary>
    /// The size of a directory's plugin packages above which a cleanup is suggested, in gigabytes (2^30 bytes); 0 never
    /// suggests one. Files written before it existed read as the default.
    /// </summary>
    public double CleanupWarningGigabytes { get; set; } = LocalSourceSettings.DefaultCleanupWarningGigabytes;
}

/// <summary>
/// The local directories of the current user. They live in the editor's per-user configuration, never in a
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
    public const double DefaultCleanupWarningGigabytes = 5, MaximumCleanupWarningGigabytes = 100_000;
    private LocalSourceConfig _config = new();
    public string FilePath { get; } = path;
    public IReadOnlyList<string> Directories => _config.Directories;
    /// <inheritdoc cref="LocalSourceConfig.CleanupWarningGigabytes"/>
    public double CleanupWarningGigabytes => _config.CleanupWarningGigabytes;
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
            if (!double.IsFinite(config.CleanupWarningGigabytes) || config.CleanupWarningGigabytes < 0)
                config.CleanupWarningGigabytes = DefaultCleanupWarningGigabytes;
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
        return Change(config => config.Directories.Contains(normalized, PackageFiles.PathComparer) ? null
            : With(config, [.. config.Directories, normalized], config.CleanupWarningGigabytes));
    }

    /// <inheritdoc cref="Add"/>
    public bool Remove(string directory) =>
        Change(config => config.Directories.Any(d => PackageFiles.PathComparer.Equals(d, directory))
            ? With(config, config.Directories.Where(d => !PackageFiles.PathComparer.Equals(d, directory)).ToList(), config.CleanupWarningGigabytes)
            : null);

    /// <summary>Sets <see cref="CleanupWarningGigabytes"/>; false when it has that value already.</summary>
    /// <inheritdoc cref="Add"/>
    public bool SetCleanupWarning(double gigabytes)
    {
        if (!double.IsFinite(gigabytes) || gigabytes < 0 || gigabytes > MaximumCleanupWarningGigabytes)
            throw new ArgumentOutOfRangeException(nameof(gigabytes), gigabytes, $"Choose a size from 0 to {MaximumCleanupWarningGigabytes} GB.");
        return Change(config => config.CleanupWarningGigabytes == gigabytes ? null : With(config, config.Directories, gigabytes));
    }

    private static LocalSourceConfig With(LocalSourceConfig config, List<string> directories, double cleanupWarning) =>
        new() { Schema = LocalSourceConfig.CurrentSchema, Directories = directories, CleanupWarningGigabytes = cleanupWarning };

    /// <summary>Reads the file again, applies the change and saves; the list in memory changes only once saving worked.</summary>
    private bool Change(Func<LocalSourceConfig, LocalSourceConfig?> change)
    {
        Load();
        if (Problem is not null) throw new InvalidOperationException(Problem);
        if (change(_config) is not { } next) return false;
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
