#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Reads whether a project or solution still holds a reference before the dotnet CLI is asked to remove it: removing
/// something that is already gone is what an uninstall wants, but the CLI reports it as a failure. Every check answers
/// null when it cannot tell (missing or unreadable file, MSBuild properties in a path), so the caller runs the CLI as
/// before.
/// </summary>
internal static class ProjectReferences
{
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex SolutionProject = new(@"^\s*Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // NuGet ids are case-insensitive everywhere; paths only where the file system is.
    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool IsValidPackageId(string? id) => id is not null && PackageId.IsMatch(id);

    /// <summary>A non-empty path to an MSBuild project file (<c>.csproj</c>, <c>.fsproj</c>, ...).</summary>
    public static bool IsValidProjectPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        Path.GetExtension(path.Trim()).EndsWith("proj", StringComparison.OrdinalIgnoreCase);

    public static bool? ReferencesPackage(string projectFile, string id) =>
        Items(projectFile, "PackageReference")?.Any(include => include.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } found
            ? found : null;

    /// <param name="referencedProject">Absolute path of the referenced project file.</param>
    public static bool? ReferencesProject(string projectFile, string referencedProject)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        return Items(projectFile, "ProjectReference") is { } includes ? includes.Any(include => SamePath(directory, include, referencedProject)) : null;
    }

    /// <param name="project">Absolute path of the project file.</param>
    public static bool? SolutionContains(string solutionFile, string project)
    {
        try
        {
            if (!File.Exists(solutionFile)) return null;
            var directory = Path.GetDirectoryName(Path.GetFullPath(solutionFile))!;
            var paths = Path.GetExtension(solutionFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? XDocument.Load(solutionFile).Descendants().Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path")).Where(p => p is not null).Cast<string>().ToArray()
                : SolutionProject.Matches(File.ReadAllText(solutionFile)).Select(m => m.Groups[1].Value).ToArray();
            if (paths.Any(p => p.Contains("$("))) return null;
            return paths.Any(p => SamePath(directory, p, project));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    /// <summary>The Include values of an item type, or null when the file cannot be read or uses MSBuild properties there.</summary>
    private static string[]? Items(string projectFile, string itemType)
    {
        try
        {
            if (!File.Exists(projectFile)) return null;
            var includes = XDocument.Load(projectFile).Descendants().Where(e => e.Name.LocalName == itemType)
                .Select(e => (string?)e.Attribute("Include")).Where(i => i is not null)
                .SelectMany(i => i!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
            return includes.Any(i => i.Contains("$(") || i.Contains("@(")) ? null : includes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    private static bool SamePath(string baseDirectory, string relative, string absolute)
    {
        try
        {
            // MSBuild files use backslashes on every platform.
            var resolved = Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('\\', '/')));
            return string.Equals(resolved, Path.GetFullPath(absolute), PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
#endif
