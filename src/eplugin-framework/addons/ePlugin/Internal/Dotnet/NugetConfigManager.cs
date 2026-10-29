#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Manages ePlugin-owned NuGet package source entries in the Godot project's root
/// <c>nuget.config</c>, so external/local NuGet sources declared via a plugin recipe's
/// <c>AddNuget</c> call are discoverable by anyone who checks out the project, not just the
/// machine that first installed the plugin.
/// </summary>
/// <remarks>
/// Every entry this class creates carries a deterministic <c>ePlugin-</c>-prefixed key and a
/// preceding XML comment recording which plugin slugs currently depend on that source. Only
/// entries bearing that marker are ever added, updated, or removed here - anything else already
/// present in <c>nuget.config</c> (including a source with an identical value that a user
/// configured by hand) is left untouched. The whole file is only ever deleted when, after removing
/// the last managed entry, nothing else is left in it.
/// </remarks>
internal static partial class NugetConfigManager
{
    private const string ManagedKeyPrefix = "ePlugin-";
    private const string UsedByCommentPrefix = "ePlugin-managed used-by=\"";
    private static readonly string[] ConfigFileNames = ["nuget.config", "NuGet.config", "NuGet.Config"];

    public static bool RegisterSource(string projectRoot, string pluginSlug, string rawSource, ILogger? logger)
    {
        try
        {
            var normalized = Normalize(projectRoot, rawSource);
            if (normalized is null)
            {
                logger?.Warn(
                    $"NuGet source '{rawSource}' is outside the project and cannot be tracked in nuget.config; other checkouts of the project cannot restore its packages.");
                return true;
            }

            var key = ComputeKey(normalized);
            var configPath = GetConfigPath(projectRoot);
            var file = Load(configPath);
            var root = file.Document.Root;
            if (root is null)
            {
                logger?.Error("nuget.config is malformed (missing root <configuration> element), skipping.");
                return false;
            }

            var packageSources = root.Element("packageSources");
            if (packageSources is not null)
            {
                var conflicting = packageSources.Elements("add").FirstOrDefault(e =>
                    ValuesMatch((string?)e.Attribute("value"), normalized) && (string?)e.Attribute("key") != key);
                if (conflicting is not null)
                {
                    // an unrelated, user-managed source already provides this exact value; leave it alone.
                    return true;
                }
            }
            else
            {
                packageSources = new XElement("packageSources");
                XmlFile.AddLines(root, packageSources);
            }

            var existingAdd = packageSources.Elements("add").FirstOrDefault(e => (string?)e.Attribute("key") == key);
            if (existingAdd is null)
            {
                XmlFile.AddLines(packageSources,
                    new XComment(UsedByComment([pluginSlug])),
                    new XElement("add", new XAttribute("key", key), new XAttribute("value", normalized)));
            }
            else
            {
                var usedBy = ParseUsedBy(CommentBefore(existingAdd)).Append(pluginSlug)
                    .Distinct(StringComparer.Ordinal).ToArray();
                SetUsedByComment(existingAdd, usedBy);
            }

            file.Write(configPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Failed to update nuget.config for source '{rawSource}': {ex.Message}");
            return false;
        }
    }

    public static bool UnregisterSource(string projectRoot, string pluginSlug, string rawSource, ILogger? logger)
    {
        try
        {
            var normalized = Normalize(projectRoot, rawSource);
            if (normalized is null)
            {
                return true;
            }

            var configPath = GetConfigPath(projectRoot);
            if (!File.Exists(configPath))
            {
                return true;
            }

            var file = Load(configPath);
            var root = file.Document.Root;
            var packageSources = root?.Element("packageSources");
            var key = ComputeKey(normalized);
            var add = packageSources?.Elements("add").FirstOrDefault(e => (string?)e.Attribute("key") == key);
            if (root is null || add is null)
            {
                return true;
            }

            var usedBy = ParseUsedBy(CommentBefore(add)).Where(s => s != pluginSlug).ToArray();
            if (usedBy.Length == 0)
            {
                if (CommentBefore(add) is { } comment)
                {
                    XmlFile.RemoveLine(comment);
                }

                XmlFile.RemoveLine(add);
            }
            else
            {
                SetUsedByComment(add, usedBy);
            }

            // only when nothing else is left, comments included, e.g. a source the team commented out
            if (packageSources is not null && packageSources.Nodes().All(XmlFile.IsWhitespace))
            {
                XmlFile.RemoveLine(packageSources);
            }

            if (root.Nodes().All(XmlFile.IsWhitespace) && file.Document.Nodes().All(n => n == root || n is XText))
            {
                File.Delete(configPath);
                logger?.Log("Removed nuget.config (no managed NuGet sources remain).");
                return true;
            }

            file.Write(configPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Failed to update nuget.config while removing source '{rawSource}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether two recipe sources are tracked as the same nuget.config entry, however they are spelled
    /// (<c>res://addons/x/feed</c> and <c>addons/x/feed/</c>).
    /// </summary>
    public static bool SameSource(string projectRoot, string? a, string? b)
    {
        if (a is null || b is null || a == b)
        {
            return a == b;
        }

        return Normalize(projectRoot, a) is { } first && Normalize(projectRoot, b) is { } second && ComputeKey(first) == ComputeKey(second);
    }

    private static string UsedByComment(string[] usedBy) => $" {UsedByCommentPrefix}{string.Join(",", usedBy.Select(EncodeSlug))}\" ";

    /// <summary>
    /// A slug as the used-by comment holds it: the list is separated by commas, and an XML comment cannot contain
    /// <c>--</c> (the writer would turn it into <c>- -</c>), so those characters are percent-encoded.
    /// </summary>
    internal static string EncodeSlug(string slug)
    {
        var encoded = new StringBuilder(slug.Length);
        for (var i = 0; i < slug.Length; i++)
        {
            encoded.Append(slug[i] switch
            {
                '%' => "%25",
                ',' => "%2C",
                '"' => "%22",
                '-' when i > 0 && slug[i - 1] == '-' => "%2D",
                var c => c.ToString(),
            });
        }

        return encoded.ToString();
    }

    internal static string DecodeSlug(string text) =>
        EncodedCharacter().Replace(text, match => match.Groups[1].Value switch
        {
            "25" => "%",
            "2C" => ",",
            "22" => "\"",
            _ => "-",
        });

    // The characters EncodeSlug percent-encodes; group 1 is their code.
    [GeneratedRegex("%(25|2C|22|2D)", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedCharacter();

    private static void SetUsedByComment(XElement add, string[] usedBy)
    {
        var text = UsedByComment(usedBy);
        if (CommentBefore(add) is { } comment)
        {
            comment.Value = text;
        }
        else if (XmlFile.IndentBefore(add) is { } indent)
        {
            add.AddBeforeSelf(new XComment(text), new XText("\n" + indent));
        }
        else
        {
            add.AddBeforeSelf(new XComment(text));
        }
    }

    private static string[] ParseUsedBy(XComment? comment)
    {
        if (comment is null)
        {
            return [];
        }

        var text = comment.Value.Trim();
        if (!text.StartsWith(UsedByCommentPrefix, StringComparison.Ordinal) ||
            !text.EndsWith('"'))
        {
            return [];
        }

        var inner = text.Substring(UsedByCommentPrefix.Length, text.Length - UsedByCommentPrefix.Length - 1);
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(DecodeSlug).ToArray();
    }

    /// <summary>
    /// Classifies and normalizes a recipe's raw NuGet source into the value that should be
    /// persisted in nuget.config: verbatim for http(s) feeds, or a project-root-relative path
    /// (portable across machines/checkouts) for local directories. Returns <see langword="null"/>
    /// when a local source cannot be expressed relative to the project.
    /// </summary>
    private static string? Normalize(string projectRoot, string rawSource)
    {
        if (string.IsNullOrWhiteSpace(rawSource))
        {
            return null;
        }

        if (Uri.TryCreate(rawSource, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return rawSource;
        }

        // res:// is the project folder; user:// and other schemes never point into it.
        const string projectScheme = "res://";
        var isResource = rawSource.StartsWith(projectScheme, StringComparison.Ordinal);
        if (!isResource && rawSource.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(projectRoot);
        // A relative path is relative to the project, not to the editor's working directory.
        var fullSourcePath = Path.GetFullPath(Path.Combine(root, isResource ? rawSource[projectScheme.Length..] : rawSource));

        var relative = Path.GetRelativePath(root, fullSourcePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            // outside the project tree; cannot be represented portably.
            return null;
        }

        var normalizedRelative = relative.Replace('\\', '/');
        return normalizedRelative.StartsWith("./", StringComparison.Ordinal)
            ? normalizedRelative
            : $"./{normalizedRelative}";
    }

    private static bool ValuesMatch(string? a, string b)
    {
        return string.Equals(NormalizeSlashes(a), NormalizeSlashes(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSlashes(string? value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\\', '/').TrimEnd('/');
    }

    private static string ComputeKey(string normalizedValue)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSlashes(normalizedValue).ToLowerInvariant()));
        return ManagedKeyPrefix + Convert.ToHexString(hash)[..12];
    }

    /// <summary>
    /// The project's existing config file, else a new <c>nuget.config</c>. NuGet reads only the first of these names it
    /// finds in a folder, in this order; on a case-sensitive file system a new <c>nuget.config</c> next to a
    /// <c>NuGet.Config</c> would hide the project's own sources.
    /// </summary>
    private static string GetConfigPath(string projectRoot)
    {
        var onDisk = Directory.Exists(projectRoot) ? Directory.EnumerateFiles(projectRoot).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        return Path.GetFullPath(Path.Combine(projectRoot, ConfigFileName(onDisk, name => File.Exists(Path.Combine(projectRoot, name)))));
    }

    /// <summary>
    /// The name of the config file among the files <paramref name="onDisk"/>, spelled as on disk: the file is replaced
    /// by a rename, which on a case-insensitive file system would otherwise change the case of its name.
    /// </summary>
    internal static string ConfigFileName(string[] onDisk, Func<string, bool> exists)
    {
        var name = ConfigFileNames.FirstOrDefault(n => onDisk.Contains(n, StringComparer.Ordinal));
        if (name is not null)
        {
            return name;
        }

        // another spelling is only the config file where the file system ignores case
        return exists(ConfigFileNames[0])
            ? onDisk.FirstOrDefault(n => n.Equals(ConfigFileNames[0], StringComparison.OrdinalIgnoreCase)) ?? ConfigFileNames[0]
            : ConfigFileNames[0];
    }

    private static XmlFile Load(string configPath)
    {
        if (File.Exists(configPath)) return XmlFile.Read(configPath);
        const string empty = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n";
        return new(XDocument.Parse(empty, LoadOptions.PreserveWhitespace), Environment.NewLine, false);
    }

    /// <summary>The comment before an entry, skipping the line break between them.</summary>
    /// <summary>
    /// The used-by comment ePlugin wrote before a managed entry; null when there is none, also when another comment is
    /// there (e.g. one the team wrote after removing ePlugin's by hand), which is never changed or removed.
    /// </summary>
    private static XComment? CommentBefore(XElement add)
    {
        var node = add.PreviousNode;
        while (node is not null && XmlFile.IsWhitespace(node))
        {
            node = node.PreviousNode;
        }

        return node is XComment comment && comment.Value.Trim().StartsWith(UsedByCommentPrefix, StringComparison.Ordinal) ? comment : null;
    }
}
#endif
