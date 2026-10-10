#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Enaweg.Plugin.Internal.Update;

internal static class PluginIni
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            var end = LineEnd(text, start);
            position = end + 1;
            var trimmed = text[start..end].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1]; continue; }
            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                if (section != "plugin") continue;
                throw new InvalidDataException("Invalid plugin.cfg assignment.");
            }
            var key = trimmed[..equals].Trim();
            var raw = trimmed[(equals + 1)..].Trim();
            string value;
            if (raw.StartsWith('"'))
            {
                // A string may continue over the following lines (e.g. a long description). Only a comment may follow
                // it; reject trailing executable expressions.
                var after = text.IndexOf('"', text.IndexOf('=', start));
                value = ReadString(text, ref after);
                end = LineEnd(text, after);
                var rest = text[after..end].Trim();
                if (rest.Length > 0 && !rest.StartsWith(';')) throw new InvalidDataException("Unexpected text after plugin.cfg string: " + key);
                position = end + 1;
            }
            else value = raw.Split(';')[0].Trim();
            if (section != "plugin") continue;
            if (!values.TryAdd(key, value)) throw new InvalidDataException("Duplicate plugin.cfg key: " + key);
        }
        return values;
    }

    private static int LineEnd(string text, int start) => text.IndexOf('\n', start) is var end and >= 0 ? end : text.Length;

    /// <summary>
    /// Reads the quoted string at <paramref name="position"/> like Godot's VariantParser: it may span lines, knows the
    /// escapes \b \t \n \f \r \uXXXX and \UXXXXXX, and any other escaped character stands for itself.
    /// </summary>
    private static string ReadString(string text, ref int position)
    {
        var value = new StringBuilder();
        for (position++; position < text.Length; position++)
        {
            var c = text[position];
            if (c == '"') { position++; return value.ToString(); }
            if (c != '\\') { value.Append(c); continue; }
            if (++position == text.Length) break;
            switch (text[position])
            {
                case 'b': value.Append('\b'); break;
                case 't': value.Append('\t'); break;
                case 'n': value.Append('\n'); break;
                case 'f': value.Append('\f'); break;
                case 'r': value.Append('\r'); break;
                case 'u' or 'U':
                    var digits = text[position] == 'u' ? 4 : 6;
                    if (position + digits >= text.Length || !int.TryParse(text.AsSpan(position + 1, digits), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out var code) || code > 0x10FFFF)
                        throw new InvalidDataException("Invalid unicode escape in plugin.cfg string.");
                    // Godot writes characters outside the BMP as two \u surrogates; each is appended as it is.
                    value.Append(code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32(code));
                    position += digits;
                    break;
                default: value.Append(text[position]); break;
            }
        }
        throw new InvalidDataException("Unterminated plugin.cfg string.");
    }
}
internal static class PackageFiles
{
    public const int MaximumFiles = 20000;
    public const long MaximumTotal = 512L * 1024 * 1024;
    public const long MaximumFile = 256L * 1024 * 1024;
    /// <summary>How the local file system compares paths: case-insensitively on Windows only.</summary>
    public static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
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
    public static bool Skip(string path) => path.Split('/').Any(p => (p.StartsWith(".git", StringComparison.OrdinalIgnoreCase) && !p.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)) || p is "__MACOSX" or ".DS_Store" or "Thumbs.db");
    public static string Inside(string root, string relative)
    {
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Normalize(relative)));
        if (!full.StartsWith(prefix, PathComparison)) throw new InvalidDataException("Package path leaves addon root.");
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
            if (System.IO.Path.GetFileName(file).Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Packages cannot contain .gitmodules.");
            var size = new FileInfo(file).Length;
            if (++count > MaximumFiles || size > MaximumFile || (bytes += size) > MaximumTotal) throw new IOException("Package exceeds file or size limits.");
            var target = Inside(destination, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }
}
#endif
