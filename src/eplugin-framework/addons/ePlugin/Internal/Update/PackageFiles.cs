#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

internal static class PluginIni
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1]; continue; }
            if (section != "plugin") continue;
            var equals = trimmed.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Invalid plugin.cfg assignment.");
            var key = trimmed[..equals].Trim();
            var raw = trimmed[(equals + 1)..].Trim();
            string value;
            if (raw.StartsWith('"'))
            {
                // Godot INI strings use JSON-compatible escapes; reject trailing executable expressions.
                value = JsonSerializer.Deserialize<string>(raw) ?? "";
            }
            else value = raw.Split(';')[0].Trim();
            if (!values.TryAdd(key, value)) throw new InvalidDataException("Duplicate plugin.cfg key: " + key);
        }
        return values;
    }
}
internal static class PackageFiles
{
    public const int MaximumFiles = 20000;
    public const long MaximumTotal = 512L * 1024 * 1024;
    public const long MaximumFile = 256L * 1024 * 1024;
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)) throw new InvalidDataException("Unsafe package path.");
        var parts = path.TrimEnd('/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
            p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
            Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Unsafe package path: " + path);
        return string.Join('/', parts);
    }
    public static bool Skip(string path) => path.Split('/').Any(p => p.StartsWith(".git", StringComparison.OrdinalIgnoreCase) || p is "__MACOSX" or ".DS_Store" or "Thumbs.db");
    public static string Inside(string root, string relative)
    {
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Normalize(relative)));
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Package path leaves addon root.");
        return full;
    }
    public static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Skip(System.IO.Path.GetFileName(entry))) continue;
            if (Directory.Exists(entry)) { foreach (var child in Files(entry)) yield return child; }
            else yield return entry;
        }
    }
    public static void Copy(string source, string destination, CancellationToken ct)
    {
        long bytes = 0;
        var count = 0;
        Directory.CreateDirectory(destination);
        foreach (var file in Files(source))
        {
            ct.ThrowIfCancellationRequested();
            var size = new FileInfo(file).Length;
            if (++count > MaximumFiles || size > MaximumFile || (bytes += size) > MaximumTotal) throw new IOException("Package exceeds file or size limits.");
            var target = Inside(destination, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }
}
#endif
