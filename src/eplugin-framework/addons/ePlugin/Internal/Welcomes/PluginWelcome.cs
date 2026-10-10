#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Text;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Welcomes;

/// <summary>A plugin's welcome page, shown once after the plugin is installed.</summary>
/// <param name="Source">
/// Where the page comes from: a file relative to the plugin directory, a res:// path set by the recipe, or
/// <see cref="PluginLicense.TextPrefix"/> for a text set by the recipe.
/// </param>
/// <param name="Text">The welcome text, which may contain BBCode.</param>
/// <param name="Problem">Why the text cannot be shown, e.g. a missing file; null when it can.</param>
internal sealed record WelcomeEntry(string Slug, string Name, string Version, string Source, string Text,
    string? Problem = null);

/// <summary>
/// Reads a plugin's welcome page. In order: the page set by the recipe, the <c>welcome_file</c> of plugin.cfg, and the
/// README of the plugin directory. A plugin without any of these has no welcome page.
/// </summary>
internal static class PluginWelcome
{
    public const string FileKey = "welcome_file";
    /// <summary>The README files used when nothing else is set, in this order. Their case does not matter.</summary>
    public static readonly string[] DefaultFiles = ["README.md", "README.txt", "README"];
    public const long MaximumSize = 1024 * 1024;

    /// <summary>
    /// Reads the welcome_file named by plugin.cfg, or the README, from <paramref name="pluginDirectory"/>, an absolute
    /// path. Null when nothing is configured and there is no README.
    /// </summary>
    public static WelcomeEntry? FromConfig(string slug, string name, string version, string? configuredFile, string pluginDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredFile))
        {
            var file = PluginLicense.ResolveFile(slug, configuredFile);
            if (file is null)
                return Entry(slug, name, version, configuredFile.Trim(), "",
                    $"{FileKey} \"{configuredFile.Trim()}\" must be a relative path inside the plugin directory.");
            try { return FromFile(slug, name, version, file, PackageFiles.Inside(pluginDirectory, file)); }
            catch (InvalidDataException ex) { return Entry(slug, name, version, file, "", ex.Message); }
        }

        var files = Directory.Exists(pluginDirectory) ? Directory.GetFiles(pluginDirectory) : [];
        foreach (var candidate in DefaultFiles)
        {
            var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return FromFile(slug, name, version, Path.GetFileName(match), match);
        }

        return null;
    }

    /// <summary>Reads a welcome file at an absolute path.</summary>
    public static WelcomeEntry FromFile(string slug, string name, string version, string source, string path)
    {
        var display = PluginLicense.Display(slug, source);
        try
        {
            if (!File.Exists(path)) return Entry(slug, name, version, source, "", $"The welcome file {display} is missing.");
            if (new FileInfo(path).Length > MaximumSize)
                return Entry(slug, name, version, source, "", $"The welcome file {display} is larger than {MaximumSize / 1024} KiB.");
            return Entry(slug, name, version, source, File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Entry(slug, name, version, source, "", $"Cannot read the welcome file {display}: {ex.Message}");
        }
    }

    /// <summary>A welcome text set by the recipe.</summary>
    public static WelcomeEntry FromText(string slug, string name, string version, string text) =>
        Entry(slug, name, version, PluginLicense.TextPrefix, text);

    private static WelcomeEntry Entry(string slug, string name, string version, string source, string text, string? problem = null) =>
        new(slug, string.IsNullOrWhiteSpace(name) ? slug : name.Trim(), version.Trim(), source, text, problem);
}
#endif
