#if TOOLS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Enaweg.Plugin.Internal.Update;

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
    public const string TextPrefix = "text:";
    public const long MaximumSize = 1024 * 1024;

    /// <summary>Accepts the INI value <c>true</c> as well as the string <c>"true"</c>.</summary>
    public static bool IsTrue(string? value) => value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The license file of plugin.cfg relative to the plugin directory, or <see cref="DefaultFile"/>. Null when the
    /// configured path leaves the plugin directory.
    /// </summary>
    public static string? ResolveFile(string slug, string? configured)
    {
        configured = configured?.Trim() ?? "";
        if (configured.Length == 0) return DefaultFile;
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

    /// <summary>Where the license is shown to come from.</summary>
    public static string Display(string slug, string source) =>
        source.StartsWith(TextPrefix, StringComparison.Ordinal) ? "Set by the plugin"
        : source.StartsWith("res://", StringComparison.Ordinal) ? source
        : $"res://addons/{slug}/{source}";

    /// <summary>Reads the license file named by plugin.cfg from <paramref name="pluginDirectory"/>, an absolute path.</summary>
    public static LicenseEntry FromConfig(string slug, string name, string version, string? configuredFile, string pluginDirectory)
    {
        var file = ResolveFile(slug, configuredFile);
        if (file is null)
            return Entry(slug, name, version, configuredFile?.Trim() ?? "", "",
                $"{FileKey} \"{configuredFile?.Trim()}\" must be a relative path inside the plugin directory.");
        try { return FromFile(slug, name, version, file, PackageFiles.Inside(pluginDirectory, file)); }
        catch (InvalidDataException ex) { return Entry(slug, name, version, file, "", ex.Message); }
    }

    /// <summary>Reads a license file at an absolute path.</summary>
    public static LicenseEntry FromFile(string slug, string name, string version, string source, string path)
    {
        try
        {
            if (!File.Exists(path)) return Entry(slug, name, version, source, "", $"The license file {Display(slug, source)} is missing.");
            if (new FileInfo(path).Length > MaximumSize)
                return Entry(slug, name, version, source, "", $"The license file {Display(slug, source)} is larger than {MaximumSize / 1024} KiB.");
            return Entry(slug, name, version, source, File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Entry(slug, name, version, source, "", $"Cannot read the license file {Display(slug, source)}: {ex.Message}");
        }
    }

    /// <summary>A license text set by the recipe. It has no path, so its contents identify it.</summary>
    public static LicenseEntry FromText(string slug, string name, string version, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))[..16].ToLowerInvariant();
        return Entry(slug, name, version, TextPrefix + hash, text);
    }

    private static LicenseEntry Entry(string slug, string name, string version, string source, string text, string? problem = null) =>
        new(slug, string.IsNullOrWhiteSpace(name) ? slug : name.Trim(), version.Trim(), source, text, problem);
}
#endif
