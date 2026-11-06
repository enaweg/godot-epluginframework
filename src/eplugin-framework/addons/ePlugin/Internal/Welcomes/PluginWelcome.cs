#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Licenses;

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
    public const long MaximumSize = PluginTextFile.MaximumSize;

    private static readonly PluginTextFile<WelcomeEntry> Files =
        new("welcome", FileKey, (slug, name, version, source, text, problem) => new(slug, name, version, source, text, problem));

    /// <summary>
    /// Reads the welcome_file named by plugin.cfg, or the README, from <paramref name="pluginDirectory"/>, an absolute
    /// path. Null when nothing is configured and there is no README.
    /// </summary>
    public static WelcomeEntry? FromConfig(string slug, string name, string version, string? configuredFile, string pluginDirectory)
    {
        // the README is searched below rather than taken as the default file, because its case does not matter
        if (!string.IsNullOrWhiteSpace(configuredFile))
            return Files.FromConfigured(slug, name, version, configuredFile, DefaultFiles[0], pluginDirectory);

        var files = Directory.Exists(pluginDirectory) ? Directory.GetFiles(pluginDirectory) : [];
        foreach (var candidate in DefaultFiles)
        {
            var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return FromFile(slug, name, version, Path.GetFileName(match), match);
        }

        return null;
    }

    /// <summary>Reads a welcome file at an absolute path.</summary>
    public static WelcomeEntry FromFile(string slug, string name, string version, string source, string path) =>
        Files.FromFile(slug, name, version, source, path);

    /// <summary>A welcome text set by the recipe.</summary>
    public static WelcomeEntry FromText(string slug, string name, string version, string text) =>
        Files.Entry(slug, name, version, PluginLicense.TextPrefix, text);
}
#endif
