#if TOOLS
using System;
using System.Security.Cryptography;
using System.Text;

namespace Enaweg.Plugin.Internal.Licenses;

/// <summary>A plugin's license, to accept before the plugin is enabled or updated, or just to read.</summary>
/// <param name="Source">
/// Where the license comes from, which is what acceptance is tracked by: a file relative to the plugin directory, a
/// res:// path set by the recipe, or <see cref="PluginLicense.TextPrefix"/> and a hash of a text set by the recipe.
/// </param>
/// <param name="Text">The license text, which may contain BBCode.</param>
/// <param name="Problem">Why the license text cannot be shown, e.g. a missing file; null when it can.</param>
internal sealed record LicenseEntry(string Slug, string Name, string Version, string Source, string Text,
    string? Problem = null);

/// <summary>A plugin's license as the ePlugin Manager shows it.</summary>
/// <param name="Required">plugin.cfg sets <c>license_required=true</c>.</param>
/// <param name="Accepted">What was accepted for the plugin, which may be an earlier license.</param>
internal sealed record LicenseInfo(LicenseEntry Entry, bool Required, AcceptedLicense? Accepted)
{
    public bool IsAccepted => Accepted is not null && Accepted.License == Entry.Source;
}

/// <summary>
/// Reads a plugin's license. In order: the license set by the recipe, the <c>license_file</c> of plugin.cfg, and the
/// <c>LICENSE</c> file of the plugin directory. <c>license_required=true</c> in plugin.cfg asks for it to be accepted.
/// </summary>
internal static class PluginLicense
{
    public const string RequiredKey = "license_required";
    public const string FileKey = "license_file";
    public const string DefaultFile = "LICENSE";
    public const string TextPrefix = PluginTextFile.TextPrefix;
    public const long MaximumSize = PluginTextFile.MaximumSize;

    /// <summary>What can be done about a license that has to be accepted but cannot be read; follows its problem.</summary>
    public const string UnreadableAdvice =
        "A license that cannot be read cannot be accepted, so the plugin stays disabled. The plugin's author has to ship it " +
        "inside the plugin directory, at most 1 MiB, where plugin.cfg (license_file, else LICENSE) or the recipe names it. If the " +
        "file was removed by accident, reinstalling or updating the plugin restores it.";

    private static readonly PluginTextFile<LicenseEntry> Files =
        new("license", FileKey, (slug, name, version, source, text, problem) => new(slug, name, version, source, text, problem));

    /// <summary>Accepts the INI value <c>true</c> as well as the string <c>"true"</c>.</summary>
    public static bool IsTrue(string? value) => value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The license file of plugin.cfg relative to the plugin directory, or <see cref="DefaultFile"/>. Null when the
    /// configured path leaves the plugin directory.
    /// </summary>
    public static string? ResolveFile(string slug, string? configured) => PluginTextFile.ResolveFile(slug, configured, DefaultFile);

    /// <inheritdoc cref="PluginTextFile.SourceOfPath"/>
    public static string SourceOfPath(string slug, string resourcePath) => PluginTextFile.SourceOfPath(slug, resourcePath);

    /// <summary>Where a license or welcome page is shown to come from.</summary>
    public static string Display(string slug, string source) => PluginTextFile.Display(slug, source);

    /// <summary>Reads the license file named by plugin.cfg from <paramref name="pluginDirectory"/>, an absolute path.</summary>
    public static LicenseEntry FromConfig(string slug, string name, string version, string? configuredFile, string pluginDirectory) =>
        Files.FromConfigured(slug, name, version, configuredFile, DefaultFile, pluginDirectory);

    /// <summary>Reads a license file at an absolute path.</summary>
    public static LicenseEntry FromFile(string slug, string name, string version, string source, string path) =>
        Files.FromFile(slug, name, version, source, path);

    /// <summary>A license text set by the recipe. It has no path, so its contents identify it.</summary>
    public static LicenseEntry FromText(string slug, string name, string version, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))[..16].ToLowerInvariant();
        return Files.Entry(slug, name, version, TextPrefix + hash, text);
    }
}
#endif
