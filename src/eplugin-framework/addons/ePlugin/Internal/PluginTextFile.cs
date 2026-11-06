#if TOOLS
using System;
using System.IO;
using System.Text;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The path rules and display of a text a plugin ships for the user (its license or welcome page): a file in the plugin
/// directory, a res:// path or a text set by the recipe.
/// </summary>
internal static class PluginTextFile
{
    /// <summary>The source of a text set by the recipe, which has no path; licenses add a hash of the text.</summary>
    public const string TextPrefix = "text:";

    /// <summary>The largest file that is read, 1 MiB.</summary>
    public const long MaximumSize = 1024 * 1024;

    /// <summary>
    /// A file configured in plugin.cfg relative to the plugin directory, or <paramref name="defaultFile"/> when none is.
    /// Null when the configured path leaves the plugin directory.
    /// </summary>
    public static string? ResolveFile(string slug, string? configured, string defaultFile)
    {
        configured = configured?.Trim() ?? "";
        if (configured.Length == 0) return defaultFile;
        var prefix = $"res://addons/{slug}/";
        if (configured.StartsWith(prefix, StringComparison.Ordinal)) configured = configured[prefix.Length..];
        try { return PackageFiles.Normalize(configured); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The source of a res:// path set by the recipe: relative when it is inside the plugin directory.</summary>
    public static string SourceOfPath(string slug, string resourcePath)
    {
        var prefix = $"res://addons/{slug}/";
        if (!resourcePath.StartsWith(prefix, StringComparison.Ordinal)) return resourcePath;
        try { return PackageFiles.Normalize(resourcePath[prefix.Length..]); }
        catch (InvalidDataException) { return resourcePath; }
    }

    /// <summary>Where the text is shown to come from.</summary>
    public static string Display(string slug, string source) =>
        source.StartsWith(TextPrefix, StringComparison.Ordinal) ? "Set by the plugin"
        : source.StartsWith("res://", StringComparison.Ordinal) ? source
        : $"res://addons/{slug}/{source}";
}

/// <summary>Creates the entry of a plugin text, see <see cref="PluginTextFile{T}"/>.</summary>
/// <param name="problem">Why the text cannot be shown; null when it can.</param>
internal delegate T PluginTextEntryFactory<out T>(string slug, string name, string version, string source, string text,
    string? problem);

/// <summary>
/// Reads one kind of plugin text (a license or a welcome page) into its entry type <typeparamref name="T"/>. A text that
/// cannot be read becomes an entry with an empty text and the problem.
/// </summary>
/// <param name="kind">The kind in messages, e.g. <c>license</c>.</param>
/// <param name="fileKey">The plugin.cfg key that configures the file, e.g. <c>license_file</c>.</param>
internal sealed class PluginTextFile<T>(string kind, string fileKey, PluginTextEntryFactory<T> create)
{
    /// <summary>An entry with the plugin's name (its slug when it has none) and version trimmed.</summary>
    public T Entry(string slug, string name, string version, string source, string text, string? problem = null) =>
        create(slug, string.IsNullOrWhiteSpace(name) ? slug : name.Trim(), version.Trim(), source, text, problem);

    /// <summary>
    /// Reads the file configured in plugin.cfg (or <paramref name="defaultFile"/>) from <paramref name="pluginDirectory"/>,
    /// an absolute path.
    /// </summary>
    public T FromConfigured(string slug, string name, string version, string? configured, string defaultFile,
        string pluginDirectory)
    {
        var file = PluginTextFile.ResolveFile(slug, configured, defaultFile);
        if (file is null)
            return Entry(slug, name, version, configured?.Trim() ?? "", "",
                $"{fileKey} \"{configured?.Trim()}\" must be a relative path inside the plugin directory.");
        try { return FromFile(slug, name, version, file, PackageFiles.Inside(pluginDirectory, file)); }
        catch (InvalidDataException ex) { return Entry(slug, name, version, file, "", ex.Message); }
    }

    /// <summary>Reads a file at an absolute path; <paramref name="source"/> is what the entry records.</summary>
    public T FromFile(string slug, string name, string version, string source, string path)
    {
        var display = PluginTextFile.Display(slug, source);
        try
        {
            if (!File.Exists(path)) return Entry(slug, name, version, source, "", $"The {kind} file {display} is missing.");
            if (new FileInfo(path).Length > PluginTextFile.MaximumSize)
                return Entry(slug, name, version, source, "", $"The {kind} file {display} is larger than {PluginTextFile.MaximumSize / (1024 * 1024)} MiB.");
            return Entry(slug, name, version, source, File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Entry(slug, name, version, source, "", $"Cannot read the {kind} file {display}: {ex.Message}");
        }
    }
}
#endif
