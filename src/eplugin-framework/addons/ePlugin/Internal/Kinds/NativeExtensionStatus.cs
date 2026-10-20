#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// One <c>.gdextension</c> file of an installed plugin as the editor runs it: what it says, whether Godot loaded it, and
/// whether the library it names for this editor's platform is there. Pure apart from <see cref="Read"/>; the ePlugin
/// Manager shows it in the details and marks the plugin as needing attention when <see cref="Problem"/> is set.
/// </summary>
/// <param name="File">The path of the <c>.gdextension</c> file relative to the plugin folder ('/').</param>
/// <param name="Config">What it says; null when it cannot be read.</param>
/// <param name="Loaded">Godot has loaded the extension.</param>
/// <param name="LibraryPresent">The library it names for this editor's platform exists.</param>
internal sealed record NativeExtensionStatus(string File, GDExtensionConfig? Config, bool Loaded, bool LibraryPresent)
{
    /// <summary>The extension names a library for the platform of this editor.</summary>
    public bool ForThisPlatform => Config?.Libraries.Any(l => GDExtensionConfig.MatchesThisEditor(l.Feature)) == true;

    /// <summary>The platforms it ships libraries for, such as "linux.x86_64" or "macos".</summary>
    public IReadOnlyList<string> Platforms => Config?.Libraries.Select(l => GDExtensionConfig.PlatformName(l.Feature)).OfType<string>()
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];

    /// <summary>
    /// Why Godot does not run the extension although it should, or null. An extension without a library for this platform
    /// is no problem: it is meant for other platforms.
    /// </summary>
    public string? Problem =>
        Config is null ? "The file cannot be read."
        : Config.CompatibilityProblem() is { } version ? $"Godot does not load it: {version}."
        : !ForThisPlatform || Loaded ? null
        : !LibraryPresent ? "Its library for this platform is missing."
        : "Godot has not loaded it. Restart the editor; if that does not help, the output log says why.";

    /// <summary>
    /// The <c>.gdextension</c> files of the add-on <paramref name="slug"/> in the project at <paramref name="projectRoot"/>
    /// (a file system path), leaving out hidden folders, whose extensions Godot does not load (see
    /// <see cref="ShowHideHelper"/>). <paramref name="isLoaded"/> tells whether Godot loaded the one at a file system path.
    /// </summary>
    public static IReadOnlyList<NativeExtensionStatus> Read(string projectRoot, string slug, Func<string, bool> isLoaded)
    {
        var directory = Path.Combine(projectRoot, "addons", slug);
        IReadOnlyDictionary<string, GDExtensionConfig?> configs;
        // a folder that cannot be listed shows no extensions rather than failing the whole plugin list
        try { configs = GDExtensionConfig.ReadAll(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return configs
            .Where(e => !e.Key.Split('/').Any(part => part.StartsWith('.')))
            .Select(e => new NativeExtensionStatus(e.Key, e.Value, isLoaded(Path.Combine(directory, e.Key)),
                e.Value?.Libraries.Where(l => GDExtensionConfig.MatchesThisEditor(l.Feature))
                    .Any(l => Exists(projectRoot, directory, e.Key, l.Path)) == true))
            .OrderBy(e => e.File, StringComparer.Ordinal).ToArray();
    }

    // A res:// path, an absolute one, or one relative to the .gdextension file.
    private static bool Exists(string projectRoot, string directory, string file, string library)
    {
        try
        {
            library = library.Replace('\\', '/');
            var path = library.StartsWith("res://", StringComparison.Ordinal) ? Path.Combine(projectRoot, library["res://".Length..])
                : Path.IsPathRooted(library) ? library
                : Path.Combine(directory, Path.GetDirectoryName(file) ?? "", library);
            return System.IO.File.Exists(path) || Directory.Exists(path);
        }
        catch (ArgumentException) { return false; }
    }
}
#endif
