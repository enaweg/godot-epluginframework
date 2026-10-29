#if TOOLS
using System;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The framework's files that only the editor uses: its scripts, dialog scenes and icons, and the project's plugin state
/// and update sites. Exported games would carry them otherwise, because the framework is in the project, and an export
/// preset exports every resource by default. <see cref="ExportFilter"/> leaves them out of every export.
/// </summary>
internal static class ExportExclusions
{
    /// <summary>
    /// The paths to leave out, in the form of an export preset's <i>Filters to exclude files/folders from project</i>
    /// (relative to <c>res://</c>, <c>*</c> for any characters), as the documentation tells to enter them by hand.
    /// </summary>
    public static readonly string[] Patterns = ["addons/ePlugin/*", "addons/eplugin-state.json", "addons/eplugin-update-sites.json"];

    /// <summary>The exclude filter that leaves out the same files.</summary>
    public static string Filter => string.Join(", ", Patterns);

    /// <param name="path">A project file, as a <c>res://</c> path.</param>
    public static bool Excludes(string path)
    {
        // Godot matches export filters ignoring case
        var relative = path.StartsWith("res://", StringComparison.Ordinal) ? path["res://".Length..] : path;
        return Patterns.Any(pattern => pattern.EndsWith('*')
            ? relative.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : relative.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
#endif
