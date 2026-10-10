#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

internal sealed class UpdateSiteEntry
{
    public string Slug { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed class UpdateSiteConfig
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public List<UpdateSiteEntry> Sites { get; set; } = [];
}

/// <summary>
/// The update sites a project sets for its plugins, e.g. for a plugin without an update_url or one whose update_url
/// stopped working. They are tried before the plugin.cfg update_url, which remains the fallback. The file belongs to the
/// project and is committed, so everyone working on it uses the same sites.
/// </summary>
/// <remarks>
/// Each change reads the file again first, as a pull may have changed it. A file that cannot be read, or that a newer
/// ePlugin version wrote, is never overwritten: the sites are read-only until it is repaired or deleted. Entries are
/// kept as they are when read, so one edited by hand to an unsupported URL simply falls back to plugin.cfg.
/// </remarks>
internal sealed class UpdateSiteSettings(string path)
{
    private UpdateSiteConfig _config = new();
    public string FilePath { get; } = path;
    /// <summary>The update site per plugin slug, ordered by slug.</summary>
    public IReadOnlyList<UpdateSiteEntry> Sites => _config.Sites;
    /// <summary>Why the sites cannot be changed right now; null when they can.</summary>
    public string? Problem { get; private set; }

    public string? UrlOf(string slug) => _config.Sites.FirstOrDefault(s => s.Slug == slug)?.Url;

    public void Load()
    {
        Problem = null;
        if (!File.Exists(FilePath)) { _config = new(); return; }
        try
        {
            var config = JsonSerializer.Deserialize<UpdateSiteConfig>(File.ReadAllText(FilePath), UpdateStateStore.JsonOptions);
            if (config?.Sites is null) throw new JsonException("The file has no site list.");
            config.Sites = config.Sites.Where(s => ValidSlug(s.Slug) && !string.IsNullOrWhiteSpace(s.Url))
                .GroupBy(s => s.Slug, StringComparer.Ordinal).Select(g => g.Last())
                .Select(s => new UpdateSiteEntry { Slug = s.Slug, Url = s.Url.Trim() })
                .OrderBy(s => s.Slug, StringComparer.Ordinal).ToList();
            _config = config;
            if (config.Schema > UpdateSiteConfig.CurrentSchema)
                Problem = $"{FilePath} was written by a newer ePlugin version (schema {config.Schema}); update ePlugin to change the update sites.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _config = new();
            Problem = $"Cannot read {FilePath}: {ex.Message} Repair or delete the file to change the update sites.";
        }
    }

    /// <summary>Adds or replaces the update site of a plugin; false when it is already set to this URL.</summary>
    /// <exception cref="ArgumentException">The slug or URL is not valid.</exception>
    /// <exception cref="InvalidOperationException">The file cannot be changed, see <see cref="Problem"/>.</exception>
    public bool Set(string slug, string url)
    {
        if (!ValidSlug(slug)) throw new ArgumentException("Choose a plugin.", nameof(slug));
        url = url.Trim();
        if (!UpdateSourceFactory.IsSupported(url))
            throw new ArgumentException(
                "Enter a GitHub or GitLab releases URL, or a Git repository URL ending in .git or with ?path=.", nameof(url));
        return Change(sites => sites.Any(s => s.Slug == slug && s.Url == url) ? null
            : [.. sites.Where(s => s.Slug != slug), new UpdateSiteEntry { Slug = slug, Url = url }]);
    }

    /// <summary>Removes the update site of a plugin; false when it has none.</summary>
    /// <exception cref="InvalidOperationException">The file cannot be changed, see <see cref="Problem"/>.</exception>
    public bool Remove(string slug) =>
        Change(sites => sites.Any(s => s.Slug == slug) ? sites.Where(s => s.Slug != slug).ToList() : null);

    /// <summary>
    /// Reads the file again, applies the change and saves; the sites in memory change only once saving worked. The
    /// file is deleted with its last site, so a project without update sites has no file.
    /// </summary>
    private bool Change(Func<List<UpdateSiteEntry>, List<UpdateSiteEntry>?> change)
    {
        Load();
        if (Problem is not null) throw new InvalidOperationException(Problem);
        if (change(_config.Sites) is not { } sites) return false;
        var next = new UpdateSiteConfig { Sites = sites.OrderBy(s => s.Slug, StringComparer.Ordinal).ToList() };
        if (next.Sites.Count == 0) File.Delete(FilePath);
        else AtomicJson.Write(FilePath, next);
        _config = next;
        return true;
    }

    private static bool ValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug is not ("." or "..") && slug.IndexOfAny(['/', '\\']) < 0;
}
#endif
