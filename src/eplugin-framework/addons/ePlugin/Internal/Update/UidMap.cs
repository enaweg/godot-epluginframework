#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

internal static class UidMap
{
    private static string Visible(string path) => string.Join('/', path.Replace('\\', '/').Split('/').Select((part, i) => part.StartsWith('.') && part != "." ? part.TrimStart('.') : part));

    /// <summary>
    /// The uids below <paramref name="root"/> by their visible path. When a hidden and a shown file share one (both
    /// <c>.src/a.gd.uid</c> and <c>src/a.gd.uid</c>), the shown one wins instead of failing the update.
    /// </summary>
    public static Dictionary<string, string> Collect(string root)
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var file in PackageFiles.Files(root).Where(f => f.EndsWith(".uid", System.StringComparison.Ordinal))
                     .Select(f => (File: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
                     .OrderBy(f => Visible(f.Relative) == f.Relative ? 0 : 1).ThenBy(f => f.Relative, System.StringComparer.Ordinal))
            map.TryAdd(Visible(file.Relative), File.ReadAllText(file.File));
        return map;
    }

    public static void Restore(string root, IReadOnlyDictionary<string, string> map)
    {
        foreach (var file in PackageFiles.Files(root).Where(f => !f.EndsWith(".uid", System.StringComparison.Ordinal)).ToArray())
        {
            var key = Visible(Path.GetRelativePath(root, file) + ".uid");
            if (!File.Exists(file + ".uid") && map.TryGetValue(key, out var uid)) File.WriteAllText(file + ".uid", uid);
        }
    }
}
#endif
