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
        var prefix = $"res://addons/{slug}/";
        var preserved = new List<string>();
        var paths = old.Directories.Select(RecipeReconciler.VisibleDirectory)
            .Concat(old.Projects.Select(p => p.Path)).Concat(old.Autoloads.Select(a => a.Path))
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
                    var directory = Path.GetDirectoryName(relative);
                    if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("A missing root-level managed project cannot be bridged safely.");
                    var destination = PackageFiles.Inside(installed, directory);
                    if (Directory.Exists(destination)) throw new InvalidDataException("New project directory lacks the old managed project; interim bridge would overwrite new files.");
                    PackageFiles.Copy(Path.GetDirectoryName(source)!, destination, CancellationToken.None);
                    preserved.Add(directory);
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
