#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

internal static class InterimBridge
{
    public static List<string> Restore(string slug, RecipeSnapshot old, string backup, string installed)
    {
        var prefix = $"addons/{slug}/";
        var preserved = new List<string>();
        var paths = old.Directories.Select(RecipeReconciler.VisibleDirectory)
            .Concat(old.Projects.Select(p => p.Path)).Concat(old.Autoloads.Select(a => a.Path))
            .Select(p => p.StartsWith("res://", StringComparison.Ordinal) ? p[6..] : p)
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..])
            .OrderBy(p => p.Count(c => c == '/')).ToArray();
        foreach (var relative in paths)
        {
            var target = PackageFiles.Inside(installed, relative);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            var source = PackageFiles.Inside(backup, relative);
            if (File.Exists(source))
            {
                // Project files need their neighboring sources as well.
                if (relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    var directory = Path.GetDirectoryName(relative) ?? "";
                    var sourceDirectory = string.IsNullOrEmpty(directory) ? backup : PackageFiles.Inside(backup, directory);
                    foreach (var neighbor in PackageFiles.Files(sourceDirectory))
                    {
                        var neighborRelative = Path.GetRelativePath(backup, neighbor).Replace('\\', '/');
                        var destination = PackageFiles.Inside(installed, neighborRelative);
                        if (File.Exists(destination)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(neighbor, destination, false);
                        preserved.Add(neighborRelative);
                    }
                }
                else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false); preserved.Add(relative); }
            }
            else if (Directory.Exists(source)) { PackageFiles.Copy(source, target, CancellationToken.None); preserved.Add(relative); }
            else throw new InvalidDataException("Installed recipe path is missing from its backup: " + relative);
        }
        return preserved;
    }
    public static void Remove(string installed, IEnumerable<string> preserved)
    {
        foreach (var path in preserved.OrderByDescending(p => p.Length))
        {
            var full = PackageFiles.Inside(installed, path);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
        }
    }
}
#endif
