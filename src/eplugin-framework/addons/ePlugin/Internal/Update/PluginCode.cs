#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>Where plugin code lives apart from <c>addons/</c>, so <see cref="BuildDiagnostics"/> does not take it for project code.</summary>
internal static class PluginCode
{
    private const string ResPrefix = "res://";

    /// <summary>
    /// Directories outside <c>addons/</c> whose code is plugin code: the targets of linked addons and the folders of the
    /// projects the update's recipes declare (old, applied and new). Each ends with a separator. A project in the project
    /// directory, or above it, is left out: it would make all of the project's code plugin code.
    /// </summary>
    public static IReadOnlyList<string> Roots(string projectRoot, UpdateJournal journal)
    {
        var roots = new List<string>();
        var addons = Path.Combine(projectRoot, "addons");
        // A linked addon may be reported with the path it points to.
        if (Directory.Exists(addons))
            foreach (var directory in new DirectoryInfo(addons).EnumerateDirectories())
            {
                try { if (directory.LinkTarget is not null && directory.ResolveLinkTarget(true) is { } target) roots.Add(target.FullName); }
                catch (IOException) { }
            }
        foreach (var recipe in journal.Recipes.Values)
            foreach (var project in new[] { recipe.Old, recipe.Applied, recipe.Target }.Where(r => r is not null).SelectMany(r => r!.Projects))
            {
                var relative = project.Path.StartsWith(ResPrefix, StringComparison.Ordinal) ? project.Path[ResPrefix.Length..] : project.Path;
                if (Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectRoot, relative))) is { } directory) roots.Add(directory);
            }
        var root = WithSeparator(projectRoot);
        return roots.Select(WithSeparator).Where(r => !root.StartsWith(r, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string WithSeparator(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
}
#endif
