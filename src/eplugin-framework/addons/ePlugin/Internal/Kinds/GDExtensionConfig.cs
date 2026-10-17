#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>One entry of a <c>.gdextension</c> file's <c>[libraries]</c> or <c>[dependencies]</c> section.</summary>
/// <param name="Feature">The feature tags of the entry, e.g. <c>windows.debug.x86_64</c>.</param>
/// <param name="Path">The path as written: a res:// path or one relative to the <c>.gdextension</c> file.</param>
internal sealed record GDExtensionLibrary(string Feature, string Path);

/// <summary>
/// What ePlugin reads from a <c>.gdextension</c> file: whether Godot can reload it in place and which native files it
/// loads. Pure; it reads the text like Godot's ConfigFile, tolerating values it does not need.
/// </summary>
internal sealed partial record GDExtensionConfig(bool Reloadable, IReadOnlyList<GDExtensionLibrary> Libraries, IReadOnlyList<GDExtensionLibrary> Dependencies)
{
    public const string Extension = ".gdextension";
    // A quoted string with backslash escapes; group 1 is its text without the quotes.
    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant)]
    private static partial Regex Escape();
    private static readonly string[] Architectures = ["x86_64", "x86_32", "arm64", "arm32", "rv64", "ppc64", "wasm32", "universal"];

    public static bool IsExtensionFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static GDExtensionConfig Parse(string text)
    {
        var reloadable = false;
        var libraries = new List<GDExtensionLibrary>();
        var dependencies = new List<GDExtensionLibrary>();
        var section = "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim().Trim('"');
            var value = line[(equals + 1)..].Trim();
            // A dictionary or array value (the dependencies) may continue over the following lines.
            while (Depth(value) > 0 && i + 1 < lines.Length) value += "\n" + lines[++i].Trim();
            switch (section)
            {
                case "configuration" when key == "reloadable":
                    reloadable = value.Split(';')[0].Trim() == "true";
                    break;
                case "libraries":
                    if (QuotedString().Match(value) is { Success: true } path) libraries.Add(new(key, Unescape(path.Groups[1].Value)));
                    break;
                case "dependencies":
                    // { "res://bin/dep.dll" : "", "dep2.dll" : "subdir" }: the keys are the files, the values export folders.
                    var strings = QuotedString().Matches(value).Select(m => Unescape(m.Groups[1].Value)).ToArray();
                    for (var s = 0; s < strings.Length; s += 2) if (strings[s].Length > 0) dependencies.Add(new(key, strings[s]));
                    break;
            }
        }
        return new(reloadable, libraries, dependencies);
    }

    private static int Depth(string value)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted) { if (c == '\\') i++; else if (c == '"') quoted = false; continue; }
            if (c == '"') quoted = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ';' && depth == 0) break;
        }
        return depth;
    }

    private static string Unescape(string value) => Escape().Replace(value, "$1");

    /// <summary>Every readable <c>.gdextension</c> file below <paramref name="root"/>, keyed by its path relative to it ('/').</summary>
    public static IReadOnlyDictionary<string, GDExtensionConfig?> ReadAll(string root)
    {
        var result = new Dictionary<string, GDExtensionConfig?>(PackageFiles.PathComparer);
        if (!Directory.Exists(root)) return result;
        foreach (var file in PackageFiles.Files(root).Where(IsExtensionFile))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            try { result[relative] = Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result[relative] = null; }
        }
        return result;
    }

    /// <summary>
    /// Whether Godot can load the new version without an editor restart: the same <c>.gdextension</c> files as before,
    /// and every one of them reloadable in both versions. An extension that is added or removed, or one that is not
    /// reloadable, keeps running its old library until the editor restarts.
    /// </summary>
    public static bool ReloadsInPlace(IReadOnlyDictionary<string, GDExtensionConfig?> before, IReadOnlyDictionary<string, GDExtensionConfig?> after) =>
        before.Count > 0 && before.Count == after.Count &&
        before.All(b => after.TryGetValue(b.Key, out var a) && b.Value is { Reloadable: true } && a is { Reloadable: true });

    /// <summary>
    /// The path of <paramref name="library"/> relative to the plugin folder ('/'), or null when it lies outside it: a
    /// res:// path elsewhere in the project, an absolute file system path or uid:// path, or a relative one that leaves the
    /// folder. <paramref name="extensionFile"/> is the <c>.gdextension</c> file's path relative to the plugin folder.
    /// </summary>
    public static string? InsidePlugin(string slug, string extensionFile, string library)
    {
        library = library.Replace('\\', '/');
        string combined;
        var prefix = $"res://addons/{slug}/";
        if (library.StartsWith(prefix, StringComparison.Ordinal)) combined = library[prefix.Length..];
        else if (library.Contains("://", StringComparison.Ordinal) || library.StartsWith('/') || library.Contains(':')) return null;
        else
        {
            var directory = System.IO.Path.GetDirectoryName(extensionFile.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
            combined = directory.Length == 0 ? library : directory + "/" + library;
        }
        var parts = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>
    /// Whether the editor running here would load the library of <paramref name="feature"/>: its operating system, an
    /// architecture tag that is missing or this machine's, and no release-only tag.
    /// </summary>
    public static bool MatchesThisEditor(string feature)
    {
        var tags = feature.Split('.');
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : null;
        if (os is null || !tags.Contains(os, StringComparer.Ordinal)) return false;
        if (tags.Any(t => t is "release" or "template_release")) return false;
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.X86 => "x86_32", Architecture.Arm64 => "arm64", Architecture.Arm => "arm32", _ => ""
        };
        var named = tags.Where(t => Architectures.Contains(t, StringComparer.Ordinal)).ToArray();
        return named.Length == 0 || named.Contains(architecture, StringComparer.Ordinal) || (os == "macos" && named.Contains("universal", StringComparer.Ordinal));
    }
}
#endif
