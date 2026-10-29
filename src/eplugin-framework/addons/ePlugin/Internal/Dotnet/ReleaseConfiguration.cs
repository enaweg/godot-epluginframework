#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Builds a project a recipe adds in its <c>Release</c> configuration when the game is exported for release. Godot exports
/// with <c>dotnet publish &lt;project&gt; -c ExportRelease</c>, and a referenced project inherits that configuration name,
/// but <c>Microsoft.NET.Sdk</c> optimizes only a configuration named <c>Release</c>: a plain library would ship
/// unoptimized. The project reference therefore asks for <c>Release</c> in <c>ExportRelease</c>, and so does the solution
/// (<c>dotnet sln add</c> maps <c>ExportRelease</c> to a project's <c>Debug</c>), which wins when the solution is built.
/// Projects that optimize <c>ExportRelease</c> themselves, such as <c>Godot.NET.Sdk</c> ones, are left alone.
/// </summary>
internal static partial class ReleaseConfiguration
{
    public const string Export = "ExportRelease";
    public const string Release = "Release";

    // A project of a .sln; group 1 is its path, group 2 its GUID.
    [GeneratedRegex(@"^\s*Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""\s*,\s*""(\{[^}]+\})""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SolutionProject();

    // The project configuration a .sln maps ExportRelease to, in a {guid}.ExportRelease|<platform>.ActiveCfg or .Build.0 line.
    [GeneratedRegex(@"^(?<lead>[ \t]*(?<guid>\{[^}\r\n]+\})\.ExportRelease\|[^.\r\n]+\.(?:ActiveCfg|Build\.0)[ \t]*=[ \t]*)(?<configuration>[^|\r\n]+)(?<platform>\|[^\r\n]*)(?=\r?$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExportReleaseMapping();

    /// <summary>The arguments of <c>dotnet msbuild</c> that print whether a project optimizes <see cref="Export"/>.</summary>
    public static string[] ProbeArguments(string project) => [project, "-getProperty:Optimize", $"-p:Configuration={Export}"];

    /// <summary>Whether the output of <see cref="ProbeArguments"/> says the project optimizes <see cref="Export"/> itself.</summary>
    public static bool Optimizes(string[] output) =>
        output.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Makes the main project's reference to <paramref name="referencedProject"/> build it as <see cref="Release"/> in
    /// <see cref="Export"/>, unless the reference already chooses a configuration. False when the project cannot be read
    /// or written, or has no such reference.
    /// </summary>
    /// <param name="referencedProject">Absolute path of the referenced project file.</param>
    public static bool MapReference(string projectFile, string referencedProject)
    {
        try
        {
            if (!File.Exists(projectFile)) return false;
            var file = XmlFile.Read(projectFile);
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
            var references = file.Document.Descendants().Where(e => e.Name.LocalName == "ProjectReference" &&
                (string?)e.Attribute("Include") is { } include && ProjectReferences.SamePath(directory, include, referencedProject)).ToArray();
            if (references.Length == 0) return false;
            var changed = false;
            foreach (var reference in references)
            {
                if (reference.Attribute("SetConfiguration") is not null || reference.Elements().Any(e => e.Name.LocalName == "SetConfiguration")) continue;
                XmlFile.AddLines(reference, new XElement(reference.Name.Namespace + "SetConfiguration",
                    new XAttribute("Condition", $" '$(Configuration)' == '{Export}' "), $"Configuration={Release}"));
                changed = true;
            }

            if (changed) file.Write(projectFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    /// <summary>
    /// Maps the solution's <see cref="Export"/> configuration to the <see cref="Release"/> configuration of
    /// <paramref name="project"/>, in a <c>.sln</c> or <c>.slnx</c>. False when the solution cannot be read or written, or
    /// does not contain the project.
    /// </summary>
    /// <param name="project">Absolute path of the project file.</param>
    public static bool MapSolution(string solutionFile, string project)
    {
        try
        {
            if (!File.Exists(solutionFile)) return false;
            var directory = Path.GetDirectoryName(Path.GetFullPath(solutionFile))!;
            return Path.GetExtension(solutionFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? MapSlnx(solutionFile, directory, project)
                : MapSln(solutionFile, directory, project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    /// <summary>Rewrites the project's <c>{guid}.ExportRelease|&lt;platform&gt;.ActiveCfg/Build.0</c> lines.</summary>
    private static bool MapSln(string solutionFile, string directory, string project)
    {
        var bytes = File.ReadAllBytes(solutionFile);
        var byteOrderMark = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = new UTF8Encoding(false).GetString(byteOrderMark ? bytes[Encoding.UTF8.Preamble.Length..] : bytes);
        var guids = SolutionProject().Matches(text).Where(m => ProjectReferences.SamePath(directory, m.Groups[1].Value, project))
            .Select(m => m.Groups[2].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (guids.Count == 0) return false;
        // only the lines of this project; the others are written back as they are
        var mapped = ExportReleaseMapping().Replace(text, m => guids.Contains(m.Groups["guid"].Value)
            ? m.Groups["lead"].Value + Release + m.Groups["platform"].Value
            : m.Value);

        if (mapped != text)
        {
            AtomicFile.Write(solutionFile, new UTF8Encoding(byteOrderMark).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(mapped)).ToArray());
        }

        return true;
    }

    /// <summary>
    /// Sets the project's <c>BuildType</c> for <see cref="Export"/>, or adds <c>&lt;BuildType Solution="ExportRelease|*"
    /// Project="Release" /&gt;</c>, which wins over the <c>BuildType</c> for every configuration that <c>dotnet sln add</c> writes.
    /// </summary>
    private static bool MapSlnx(string solutionFile, string directory, string project)
    {
        var file = XmlFile.Read(solutionFile);
        var projects = file.Document.Descendants().Where(e => e.Name.LocalName == "Project" &&
            (string?)e.Attribute("Path") is { } path && ProjectReferences.SamePath(directory, path, project)).ToArray();
        if (projects.Length == 0) return false;
        var changed = false;
        foreach (var element in projects)
        {
            var exports = element.Elements().Where(e => e.Name.LocalName == "BuildType" && (string?)e.Attribute("Solution") is { } solution &&
                solution.Split('|')[0].Trim().Equals(Export, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exports.Length == 0)
            {
                XmlFile.AddLines(element, new XElement(element.Name.Namespace + "BuildType",
                    new XAttribute("Solution", $"{Export}|*"), new XAttribute("Project", Release)));
                changed = true;
            }

            foreach (var export in exports.Where(e => (string?)e.Attribute("Project") != Release))
            {
                export.SetAttributeValue("Project", Release);
                changed = true;
            }
        }

        if (changed) file.Write(solutionFile);
        return true;
    }
}
#endif
