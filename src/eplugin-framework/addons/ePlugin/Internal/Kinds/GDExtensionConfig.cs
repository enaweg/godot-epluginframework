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
    /// <summary>The <c>compatibility_minimum</c> of <c>[configuration]</c> as written, null without one.</summary>
    public string? CompatibilityMinimum { get; init; }
    /// <summary>The <c>compatibility_maximum</c> of <c>[configuration]</c> as written, null without one.</summary>
    public string? CompatibilityMaximum { get; init; }
    /// <summary>The <c>[icons]</c> entries: the class name as feature, the icon path as path.</summary>
    public IReadOnlyList<GDExtensionLibrary> Icons { get; init; } = [];

    private static (int Major, int Minor, int Patch)? _editorVersion;

    /// <summary>
    /// The version of the editor running here (major, minor, patch), asked from Godot when first needed, which has to be on
    /// the editor thread: staging asks for it before it validates packages on a thread-pool thread. Settable so tests,
    /// also those without a Godot runtime, check the version limits of <see cref="CompatibilityProblem"/> against a known
    /// version; <see cref="ResetEditorVersion"/> asks Godot again.
    /// </summary>
    internal static (int Major, int Minor, int Patch) EditorVersion
    {
        get => _editorVersion ??= ReadEditorVersion();
        set => _editorVersion = value;
    }

    internal static void ResetEditorVersion() => _editorVersion = null;

    /// <summary>Whether <see cref="EditorVersion"/> was asked or set since the assembly was loaded.</summary>
    internal static bool EditorVersionKnown => _editorVersion is not null;

    private static (int, int, int) ReadEditorVersion()
    {
        var info = Godot.Engine.GetVersionInfo();
        return (info["major"].AsInt32(), info["minor"].AsInt32(), info["patch"].AsInt32());
    }

    private static readonly string[] BuildTags = ["debug", "release", "editor", "template", "template_debug", "template_release", "double", "single"];
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
        string? minimum = null, maximum = null;
        var libraries = new List<GDExtensionLibrary>();
        var dependencies = new List<GDExtensionLibrary>();
        var icons = new List<GDExtensionLibrary>();
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
                case "configuration" when key == "compatibility_minimum":
                    minimum = Text(value);
                    break;
                case "configuration" when key == "compatibility_maximum":
                    maximum = Text(value);
                    break;
                case "libraries":
                    if (QuotedString().Match(value) is { Success: true } path) libraries.Add(new(key, Unescape(path.Groups[1].Value)));
                    break;
                case "icons":
                    if (QuotedString().Match(value) is { Success: true } icon) icons.Add(new(key, Unescape(icon.Groups[1].Value)));
                    break;
                case "dependencies":
                    // { "res://bin/dep.dll" : "", "dep2.dll" : "subdir" }: the keys are the files, the values export folders.
                    var strings = QuotedString().Matches(value).Select(m => Unescape(m.Groups[1].Value)).ToArray();
                    for (var s = 0; s < strings.Length; s += 2) if (strings[s].Length > 0) dependencies.Add(new(key, strings[s]));
                    break;
            }
        }
        return new(reloadable, libraries, dependencies) { CompatibilityMinimum = minimum, CompatibilityMaximum = maximum, Icons = icons };
    }

    // A quoted string's text, or an unquoted value (such as 4.1 written as a number) without its comment.
    private static string Text(string value) =>
        QuotedString().Match(value) is { Success: true } quoted ? Unescape(quoted.Groups[1].Value) : value.Split(';')[0].Trim();

    /// <summary>
    /// Why the editor running here refuses to load the extension because of its version limits, or null when it loads
    /// it. Like Godot: <c>compatibility_minimum</c> is required and at least 4.1, missing parts of it count as 0 and missing
    /// parts of <c>compatibility_maximum</c> as any version.
    /// </summary>
    public string? CompatibilityProblem()
    {
        if (string.IsNullOrWhiteSpace(CompatibilityMinimum)) return "it names no compatibility_minimum, which Godot requires";
        var minimum = Parts(CompatibilityMinimum, 0);
        if (minimum[0] < 4 || minimum[0] == 4 && minimum[1] == 0) return $"its compatibility_minimum {CompatibilityMinimum} is older than 4.1, which Godot does not load";
        var editor = EditorVersion;
        var current = new[] { editor.Major, editor.Minor, editor.Patch };
        var text = $"{editor.Major}.{editor.Minor}.{editor.Patch}";
        if (Compare(current, minimum) < 0) return $"it needs Godot {CompatibilityMinimum} or newer, and this editor is {text}";
        if (!string.IsNullOrWhiteSpace(CompatibilityMaximum) && Compare(current, Parts(CompatibilityMaximum, int.MaxValue)) > 0)
            return $"it supports Godot up to {CompatibilityMaximum}, and this editor is {text}";
        return null;

        static int[] Parts(string version, int missing)
        {
            var parts = version.Trim().Split('.');
            return Enumerable.Range(0, 3).Select(i => i < parts.Length && int.TryParse(parts[i], out var part) && part >= 0 ? part : missing).ToArray();
        }
        static int Compare(int[] left, int[] right)
        {
            for (var i = 0; i < 3; i++) if (left[i] != right[i]) return left[i].CompareTo(right[i]);
            return 0;
        }
    }

    /// <summary>
    /// The platforms <paramref name="before"/> loads a library on that <paramref name="after"/> no longer does, as messages
    /// such as "linux.x86_64" or "a release library for windows.x86_64". A platform is an operating system with an
    /// optional architecture; one without an architecture covers every architecture, and a universal macOS library is
    /// covered by any macOS one. A release library is any library not limited to debug or editor builds.
    /// </summary>
    public static IReadOnlyList<string> DroppedPlatforms(GDExtensionConfig before, GDExtensionConfig after)
    {
        var dropped = new List<string>();
        var old = before.Libraries.Select(l => Platform(l.Feature)).Where(p => p.Os is not null).ToArray();
        var next = after.Libraries.Select(l => Platform(l.Feature)).Where(p => p.Os is not null).ToArray();
        foreach (var group in old.GroupBy(p => (p.Os, p.Architecture)))
        {
            var name = group.Key.Architecture is null ? group.Key.Os! : $"{group.Key.Os}.{group.Key.Architecture}";
            var covering = next.Where(p => Covers(p, group.Key.Os!, group.Key.Architecture)).ToArray();
            if (covering.Length == 0) dropped.Add(name);
            else if (group.Any(p => p.Release) && !covering.Any(p => p.Release)) dropped.Add("a release library for " + name);
        }
        return dropped;

        static bool Covers((string? Os, string? Architecture, bool Release) platform, string os, string? architecture) =>
            platform.Os == os && (platform.Architecture is null || platform.Architecture == architecture ||
                                  architecture is null || os == "macos" && architecture == "universal");
    }

    /// <summary>The platform of a library entry, such as "linux.x86_64" or "macos"; null when it names no operating system.</summary>
    public static string? PlatformName(string feature) =>
        Platform(feature) is { Os: { } os } platform ? platform.Architecture is null ? os : $"{os}.{platform.Architecture}" : null;

    /// <summary>The operating system and architecture a library entry is for, and whether release builds load it.</summary>
    private static (string? Os, string? Architecture, bool Release) Platform(string feature)
    {
        var tags = feature.Split('.');
        var os = tags.FirstOrDefault(t => !Architectures.Contains(t, StringComparer.Ordinal) && !BuildTags.Contains(t, StringComparer.Ordinal));
        var architecture = tags.FirstOrDefault(t => Architectures.Contains(t, StringComparer.Ordinal));
        var release = !tags.Any(t => t is "debug" or "editor" or "template_debug");
        return (os, architecture, release);
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
    /// The library the editor running here loads, or null when none of <paramref name="libraries"/> is for it. Like Godot:
    /// every tag of an entry has to be a feature of this editor, and of those entries the one with the most tags is
    /// loaded, the first of equals. Godot does not fall back to another entry when that one's file is missing.
    /// </summary>
    public static GDExtensionLibrary? LibraryForThisEditor(IEnumerable<GDExtensionLibrary> libraries)
    {
        GDExtensionLibrary? best = null;
        var bestTags = 0;
        foreach (var library in libraries)
        {
            var tags = library.Feature.Split('.').Select(t => t.Trim()).ToArray();
            if (tags.Length > bestTags && tags.All(IsEditorFeature)) (best, bestTags) = (library, tags.Length);
        }
        return best;
    }

    /// <summary>
    /// Whether Godot's editor running here has the feature <paramref name="tag"/>: its operating system, the editor
    /// process's architecture (which differs from the machine's when it runs emulated, an x86_64 editor on Windows on
    /// ARM), and the tags of a debug editor build. Export templates (template, template_debug, template_release, release)
    /// do not run in the editor, and neither do custom features, which an editor does not have unless a project sets them.
    /// </summary>
    private static bool IsEditorFeature(string tag)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : null;
        if (os is not null && tag == os || os == "linux" && tag == "linuxbsd") return true;
        if (tag is "pc" or "editor" or "debug" or "threads") return true;
        // Godot's real_t is double in a double-precision build, and so is GodotSharp's
        var precision = typeof(Godot.Vector2).GetField(nameof(Godot.Vector2.X))?.FieldType == typeof(double) ? "double" : "single";
        if (tag == precision) return true;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.X86 => "x86_32", Architecture.Arm64 => "arm64", Architecture.Arm => "arm32", _ => ""
        };
        if (tag == architecture || tag == (Environment.Is64BitProcess ? "64" : "32")) return true;
        if (tag == "x86" && architecture.StartsWith("x86", StringComparison.Ordinal) || tag == "arm" && architecture.StartsWith("arm", StringComparison.Ordinal)) return true;
        return os == "macos" && tag == "universal";
    }
}
#endif
