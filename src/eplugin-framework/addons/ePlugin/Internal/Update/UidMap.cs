#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

internal static class UidMap
{
    private static string Visible(string path) => string.Join('/', path.Replace('\\', '/').Split('/').Select((part, i) => part.StartsWith('.') && part != "." ? part.TrimStart('.') : part));
    public static Dictionary<string, string> Collect(string root) => PackageFiles.Files(root).Where(f => f.EndsWith(".uid", System.StringComparison.Ordinal))
        .ToDictionary(f => Visible(Path.GetRelativePath(root, f)), File.ReadAllText, System.StringComparer.Ordinal);
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
