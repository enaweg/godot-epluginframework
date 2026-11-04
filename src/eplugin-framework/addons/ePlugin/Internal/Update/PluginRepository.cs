#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>How a plugin folder is under git version control, apart from the project's own repository.</summary>
internal enum RepositoryKind
{
    /// <summary>A plain folder: only the project's repository, if any, holds it.</summary>
    None,
    /// <summary>The folder is a git submodule of the repository around it: updated from its remote's version tags.</summary>
    Submodule,
    /// <summary>The folder has its own <c>.git</c> but is no submodule: a cloned working copy or a worktree.</summary>
    Clone,
    /// <summary>The folder lies inside another repository below the project root, e.g. an <c>addons</c> submodule.</summary>
    InsideRepository,
    /// <summary>A repository (a <c>.git</c> file or folder) lies somewhere below the plugin folder.</summary>
    ContainsRepository
}

/// <summary>
/// The git repository a plugin folder belongs to, read from the files alone (nothing runs git). A package update
/// replaces the whole folder, which would delete a <c>.git</c> in it and change another repository's working copy, so
/// every kind but <see cref="RepositoryKind.None"/> is refused for packages (<see cref="PackageRefusal"/>); a submodule
/// is updated through git instead (<see cref="SubmoduleSource"/>).
/// </summary>
/// <param name="Directory">The plugin folder, a full file system path.</param>
/// <param name="Repository">
/// The repository in question: the submodule's superproject, the clone itself, the repository around the folder or the
/// <c>.git</c> inside it; null for <see cref="RepositoryKind.None"/>.
/// </param>
/// <param name="RemoteUrl">
/// Of a submodule: the URL of its <c>origin</c> remote, else the one <c>.gitmodules</c> records; null when neither is set.
/// </param>
internal sealed record PluginRepository(RepositoryKind Kind, string Directory, string? Repository = null, string? RemoteUrl = null)
{
    public const string GitEntry = ".git";

    /// <summary>The update site of a submodule: its remote, whose version tags are the versions it can be updated to.</summary>
    public string? SubmoduleSite => Kind == RepositoryKind.Submodule && !string.IsNullOrWhiteSpace(RemoteUrl) ? RemoteUrl : null;

    /// <summary>
    /// Tells how <paramref name="pluginDirectory"/> is under version control. <paramref name="projectRoot"/> bounds the
    /// search for a repository around the folder; without it, only the folder itself and what it contains are looked at.
    /// </summary>
    public static PluginRepository Detect(string pluginDirectory, string? projectRoot)
    {
        var directory = Full(pluginDirectory);
        if (!System.IO.Directory.Exists(directory)) return new(RepositoryKind.None, directory);
        if (HasGitEntry(directory))
        {
            var superproject = Around(Parent(directory), null);
            if (superproject is not null && Submodules(superproject).FirstOrDefault(s => SamePath(Path.Combine(superproject, s.Path), directory)) is { } entry)
                return new(RepositoryKind.Submodule, directory, superproject, OriginUrl(directory) ?? entry.Url);
            return new(RepositoryKind.Clone, directory, directory);
        }
        if (projectRoot is not null && Around(Parent(directory), Full(projectRoot)) is { } around)
            return new(RepositoryKind.InsideRepository, directory, around);
        if (Nested(directory) is { } nested) return new(RepositoryKind.ContainsRepository, directory, nested);
        return new(RepositoryKind.None, directory);
    }

    /// <summary>
    /// Why a package must not replace the folder, for a refusal; null for <see cref="RepositoryKind.None"/>.
    /// <paramref name="name"/> names the folder, e.g. <c>res://addons/my_plugin</c>.
    /// </summary>
    public string? PackageRefusal(string name) => Kind switch
    {
        RepositoryKind.Submodule => $"{name} is a git submodule, which ePlugin updates from the version tags of its remote rather than from " +
                                    "packages, so that its repository is kept. Check for updates again, or update it with git.",
        RepositoryKind.Clone => $"{name} is a git working copy with its own repository, which replacing its files would delete. " +
                                "Update it with your version control (git pull or git checkout in that folder).",
        RepositoryKind.InsideRepository => $"{name} is part of the git repository {Repository}, whose working copy ePlugin does not change. " +
                                           "Update it with your version control.",
        RepositoryKind.ContainsRepository => $"{name} contains the git repository {Repository}, which replacing its files would delete. " +
                                             "Update it with your version control.",
        _ => null
    };

    /// <summary>Whether <paramref name="directory"/> itself or any folder below it holds a <c>.git</c>.</summary>
    public static bool HoldsRepository(string directory) => System.IO.Directory.Exists(directory) && (HasGitEntry(directory) || Nested(directory) is not null);

    /// <summary>The first <c>.git</c> below <paramref name="directory"/>, not looking into links or repositories.</summary>
    public static string? Nested(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string[] children;
            try { children = System.IO.Directory.GetDirectories(pending.Pop()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (Path.GetFileName(child) == GitEntry) return child;
                if (HasGitEntry(child)) return Path.Combine(child, GitEntry);
                pending.Push(child);
            }
        }
        return null;
    }

    private static bool HasGitEntry(string directory)
    {
        var git = Path.Combine(directory, GitEntry);
        return File.Exists(git) || System.IO.Directory.Exists(git);
    }

    /// <summary>
    /// The nearest folder from <paramref name="start"/> upwards that holds a <c>.git</c>: up to the file system root, or
    /// only strictly below <paramref name="below"/>.
    /// </summary>
    private static string? Around(string? start, string? below)
    {
        for (var folder = start; folder is not null; folder = Parent(folder))
        {
            if (below is not null && !IsBelow(folder, below)) return null;
            if (HasGitEntry(folder)) return folder;
        }
        return null;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string? Parent(string path) => Path.GetDirectoryName(path) is { Length: > 0 } parent && parent != path ? parent : null;
    private static bool SamePath(string left, string right) => string.Equals(Full(left), Full(right), PackageFiles.PathComparison);
    private static bool IsBelow(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root, PackageFiles.PathComparison) && path[root.Length] is '/' or '\\';

    internal sealed record SubmoduleEntry(string Name, string Path, string? Url);

    /// <summary>The submodules <c>.gitmodules</c> of <paramref name="superproject"/> registers; none when it cannot be read.</summary>
    internal static IReadOnlyList<SubmoduleEntry> Submodules(string superproject)
    {
        var file = Path.Combine(superproject, ".gitmodules");
        if (!File.Exists(file)) return [];
        try
        {
            return GitConfig.Parse(File.ReadAllText(file))
                .Where(s => s.Name == "submodule" && s.Subsection is not null && s.Values.ContainsKey("path"))
                .Select(s => new SubmoduleEntry(s.Subsection!, s.Values["path"], s.Values.GetValueOrDefault("url")))
                .Where(s => s.Path.Length > 0 && !System.IO.Path.IsPathRooted(s.Path) && !s.Path.Split('/', '\\').Contains(".."))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The <c>origin</c> URL of the repository whose working copy <paramref name="directory"/> is; null when unknown.</summary>
    internal static string? OriginUrl(string directory)
    {
        try
        {
            if (GitDirectory(directory) is not { } git || !File.Exists(Path.Combine(git, "config"))) return null;
            return GitConfig.Parse(File.ReadAllText(Path.Combine(git, "config")))
                .LastOrDefault(s => s.Name == "remote" && s.Subsection == "origin" && s.Values.ContainsKey("url"))?.Values["url"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The git directory of a working copy: its <c>.git</c> folder, or where its <c>.git</c> file points.</summary>
    internal static string? GitDirectory(string directory)
    {
        var git = Path.Combine(directory, GitEntry);
        if (System.IO.Directory.Exists(git)) return git;
        if (!File.Exists(git)) return null;
        var line = File.ReadLines(git).FirstOrDefault()?.Trim() ?? "";
        if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        var target = line["gitdir:".Length..].Trim();
        return target.Length == 0 ? null : Path.GetFullPath(Path.Combine(directory, target));
    }
}

/// <summary>
/// Reads the git configuration format of <c>.gitmodules</c> and a repository's <c>config</c>: sections with an optional
/// quoted subsection, <c>key = value</c> lines with quoted parts and escapes, and comments. Includes are not followed.
/// </summary>
internal static class GitConfig
{
    /// <param name="Name">The section's name in lower case, such as <c>submodule</c>.</param>
    internal sealed record Section(string Name, string? Subsection, Dictionary<string, string> Values);

    public static IReadOnlyList<Section> Parse(string text)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var end = line.LastIndexOf(']');
                if (end < 0) { current = null; continue; }
                var header = line[1..end].Trim();
                var space = header.IndexOfAny([' ', '\t']);
                string? subsection = null;
                var name = header;
                if (space > 0)
                {
                    name = header[..space];
                    var quoted = header[space..].Trim();
                    subsection = quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"'
                        ? quoted[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : quoted;
                }
                else if (header.IndexOf('.', StringComparison.Ordinal) is var dot and > 0)
                {
                    // The old [section.subsection] form.
                    name = header[..dot];
                    subsection = header[(dot + 1)..];
                }
                current = new(name.ToLowerInvariant(), subsection, new(StringComparer.OrdinalIgnoreCase));
                sections.Add(current);
                continue;
            }
            if (current is null) continue;
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? line : line[..equals]).Trim();
            if (key.Length == 0) continue;
            current.Values[key] = equals < 0 ? "true" : Value(line[(equals + 1)..]);
        }
        return sections;
    }

    private static string Value(string text)
    {
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '#' or ';') break;
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                value.Append(next switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => next });
                continue;
            }
            value.Append(c);
        }
        return value.ToString().Trim();
    }
}
#endif
