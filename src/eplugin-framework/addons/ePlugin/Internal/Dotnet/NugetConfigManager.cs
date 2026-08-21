#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Enaweg.Plugin.Logging;
using Godot;

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
internal static class NugetConfigManager
{
    private const string ManagedKeyPrefix = "ePlugin-";
    private const string UsedByCommentPrefix = "ePlugin-managed used-by=\"";

    public static void RegisterSource(string pluginSlug, string rawSource, ILogger? logger)
    {
        try
        {
            var normalized = Normalize(rawSource);
            if (normalized is null)
            {
                logger?.Log(
                    $"NuGet source '{rawSource}' is outside the project and cannot be tracked in nuget.config.");
                return;
            }

            var key = ComputeKey(normalized);
            var configPath = GetConfigPath();
            var doc = File.Exists(configPath) ? XDocument.Load(configPath) : new XDocument(new XElement("configuration"));
            var root = doc.Root;
            if (root is null)
            {
                logger?.Error("nuget.config is malformed (missing root <configuration> element), skipping.");
                return;
            }

            var packageSources = root.Element("packageSources");
            if (packageSources is not null)
            {
                var conflicting = packageSources.Elements("add").FirstOrDefault(e =>
                    ValuesMatch((string?)e.Attribute("value"), normalized) && (string?)e.Attribute("key") != key);
                if (conflicting is not null)
                {
                    // an unrelated, user-managed source already provides this exact value; leave it alone.
                    return;
                }
            }
            else
            {
                packageSources = new XElement("packageSources");
                root.Add(packageSources);
            }

            var existingAdd = packageSources.Elements("add").FirstOrDefault(e => (string?)e.Attribute("key") == key);
            if (existingAdd is null)
            {
                packageSources.Add(
                    new XComment($" {UsedByCommentPrefix}{pluginSlug}\" "),
                    new XElement("add", new XAttribute("key", key), new XAttribute("value", normalized)));
            }
            else
            {
                var usedBy = ParseUsedBy(existingAdd.PreviousNode as XComment).Append(pluginSlug)
                    .Distinct(StringComparer.Ordinal).ToArray();
                SetUsedByComment(existingAdd, usedBy);
            }

            Save(doc, configPath);
        }
        catch (Exception ex)
        {
            logger?.Error($"Failed to update nuget.config for source '{rawSource}': {ex.Message}");
        }
    }

    public static void UnregisterSource(string pluginSlug, string rawSource, ILogger? logger)
    {
        try
        {
            var normalized = Normalize(rawSource);
            if (normalized is null)
            {
                return;
            }

            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
            {
                return;
            }

            var doc = XDocument.Load(configPath);
            var root = doc.Root;
            var packageSources = root?.Element("packageSources");
            var key = ComputeKey(normalized);
            var add = packageSources?.Elements("add").FirstOrDefault(e => (string?)e.Attribute("key") == key);
            if (root is null || add is null)
            {
                return;
            }

            var usedBy = ParseUsedBy(add.PreviousNode as XComment).Where(s => s != pluginSlug).ToArray();
            if (usedBy.Length == 0)
            {
                (add.PreviousNode as XComment)?.Remove();
                add.Remove();
            }
            else
            {
                SetUsedByComment(add, usedBy);
            }

            if (packageSources is { HasElements: false })
            {
                packageSources.Remove();
            }

            if (!root.Nodes().Any())
            {
                File.Delete(configPath);
                logger?.Log("Removed nuget.config (no managed NuGet sources remain).");
                return;
            }

            Save(doc, configPath);
        }
        catch (Exception ex)
        {
            logger?.Error($"Failed to update nuget.config while removing source '{rawSource}': {ex.Message}");
        }
    }

    private static void SetUsedByComment(XElement add, string[] usedBy)
    {
        var text = $" {UsedByCommentPrefix}{string.Join(",", usedBy)}\" ";
        if (add.PreviousNode is XComment comment)
        {
            comment.Value = text;
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
            !text.EndsWith("\"", StringComparison.Ordinal))
        {
            return [];
        }

        var inner = text.Substring(UsedByCommentPrefix.Length, text.Length - UsedByCommentPrefix.Length - 1);
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Classifies and normalizes a recipe's raw NuGet source into the value that should be
    /// persisted in nuget.config: verbatim for http(s) feeds, or a project-root-relative path
    /// (portable across machines/checkouts) for local directories. Returns <see langword="null"/>
    /// when a local source cannot be expressed relative to the project.
    /// </summary>
    private static string? Normalize(string rawSource)
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

        var projectRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        var fullSourcePath = Path.GetFullPath(ProjectSettings.GlobalizePath(rawSource));

        var relative = Path.GetRelativePath(projectRoot, fullSourcePath);
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

    private static string GetConfigPath()
    {
        return Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "nuget.config"));
    }

    private static void Save(XDocument doc, string configPath)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(configPath, settings);
        doc.Save(writer);
    }
}
#endif
