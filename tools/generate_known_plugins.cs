#:property TargetFramework=net10.0
#:property PublishAot=false
// Run with: dotnet run generate_known_plugins.cs [<add-ons json>] [<KnownPlugins.Data.cs>]
// Writes ePlugin's built-in list of add-on update sites from the output of build_godot_addons.cs. Without arguments it
// reads the newest godot_addons_active_*_stars.json in the working directory and writes
// src/eplugin-framework/addons/ePlugin/Internal/Update/KnownPlugins.Data.cs next to this tool.
//
// The entries are not verified: no package is downloaded, installed or validated. Each add-on's slug is the plugin
// folder in its repository at its latest release or version tag, read from the repository's file list with git (git 2.25
// or newer on PATH, no token needed). An add-on already in the list keeps its slug, so the key stays stable.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var scriptDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Environment.CurrentDirectory;
var input = args.Length > 0 ? args[0]
    : new DirectoryInfo(Environment.CurrentDirectory).GetFiles("godot_addons_active_*_stars.json")
        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
var output = args.Length > 1 ? args[1]
    : Path.GetFullPath(Path.Combine(scriptDirectory, "../src/eplugin-framework/addons/ePlugin/Internal/Update/KnownPlugins.Data.cs"));
if (input is null || !File.Exists(input))
{
    Console.Error.WriteLine("No add-on list found. Run build_godot_addons.cs first, or pass the JSON file it wrote.");
    Environment.ExitCode = 1;
    return;
}

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var addons = JsonSerializer.Deserialize<Addon[]>(await File.ReadAllTextAsync(input), options)!
    // Several candidates can name one repository, e.g. an old name that GitHub redirects to the renamed one.
    .DistinctBy(a => a.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
if (addons.Any(a => a.UpdateUrl is null || a.LatestVersion is null))
{
    Console.Error.WriteLine($"{input} has no update_url/latest_version; regenerate it with the current build_godot_addons.cs.");
    Environment.ExitCode = 1;
    return;
}

// Slugs of the current list, by repository.
var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(output))
    foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(output), @"// (\S+?), (?:latest|verified)[^\n]*\n\s+new\(""([^""]+)"""))
        existing.TryAdd(match.Groups[1].Value, match.Groups[2].Value);

// The plugin folders of each repository at its latest version; read in parallel, git does the waiting.
var trees = new ConcurrentDictionary<Addon, Tree>();
var done = 0;
await Parallel.ForEachAsync(addons, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (addon, ct) =>
{
    trees[addon] = await ReadTree(addon, ct);
    Console.Error.Write($"\rRead {Interlocked.Increment(ref done)}/{addons.Length} repositories");
});
Console.Error.WriteLine();

// Plugins that one repository has on its own; another repository holding them too (gut, gdUnit4) only bundles them.
var unambiguous = trees.Values.Where(t => t.Plugins.Length == 1).Select(t => Name(t.Plugins[0])).ToHashSet(StringComparer.Ordinal);
var chosen = new List<(string Slug, Addon Addon, string Note)>();
var leftOut = new List<string>();
foreach (var addon in addons)
{
    var tree = trees[addon];
    if (tree.Error is not null) { leftOut.Add($"{addon.Repository}: {tree.Error}"); continue; }
    if (tree.Plugins.Length == 0) { leftOut.Add($"{addon.Repository}: no plugin.cfg"); continue; }
    var (folder, note) = Choose(addon, tree);
    var slug = folder is null || folder.Length == 0 ? null : Name(folder);
    // The current list's slug names the plugin where the repository does not, e.g. a plugin at its root.
    if (existing.TryGetValue(addon.Repository, out var kept) && slug != kept)
    {
        note = $"kept {kept} from the current list" + (slug is null ? "" : $", the repository names {slug}");
        slug = kept;
        folder = tree.Plugins.FirstOrDefault(p => Name(p) == kept) ?? (tree.Plugins.Contains("") ? "" : folder);
    }
    if (slug is null) { leftOut.Add($"{addon.Repository}: {note}"); continue; }
    if (folder is not null && tree.Files.Any(f => f.StartsWith(folder, StringComparison.Ordinal) && f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase)))
    {
        leftOut.Add($"{addon.Repository}: GDExtension plugin (ePlugin cannot update it)");
        continue;
    }
    chosen.Add((slug, addon, note));
}

// One entry per slug: the repository with the most stars keeps it.
var entries = new List<(string Slug, Addon Addon, string Note)>();
var duplicates = new List<string>();
foreach (var group in chosen.GroupBy(c => c.Slug, StringComparer.Ordinal))
{
    var ranked = group.OrderByDescending(c => c.Addon.Stars).ToArray();
    entries.Add(ranked[0]);
    duplicates.AddRange(ranked.Skip(1).Select(c => $"{c.Slug}: {c.Addon.Repository} ({c.Addon.Stars}) loses to {ranked[0].Addon.Repository} ({ranked[0].Addon.Stars})"));
}

var stars = Regex.Match(Path.GetFileName(input), @"_(\d+)_stars") is { Success: true } count ? count.Groups[1].Value : "?";
var text = new StringBuilder($$"""
    #if TOOLS
    namespace Enaweg.Plugin.Internal.Update;

    // Generated on {{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}} by tools/generate_known_plugins.cs from {{Path.GetFileName(input)}} (built by
    // tools/build_godot_addons.cs): popular add-ons with at least {{stars}} stars, a commit within the last year and a stable release
    // or version tag. Each slug is the plugin folder in the repository at that release or tag: the only top-level
    // plugin.cfg or, of several plugins, the one named like the repository; an add-on already in the list keeps its slug.
    // Add-ons without a plugin folder or with a GDExtension are left out. The packages were not installed or validated.
    internal static partial class KnownPlugins
    {
        private static KnownPlugin[] Entries() =>
        [

    """);
foreach (var (slug, addon, _) in entries.OrderBy(e => e.Slug, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Slug, StringComparer.Ordinal))
{
    text.Append($"        // {addon.Repository}, latest {(addon.UpdateSource == "tags" ? "tag" : "release")} {addon.LatestVersion}\n");
    text.Append($"        new({Cs(slug)}, {Cs(addon.Name)}, {Cs(addon.UpdateUrl!)},\n");
    text.Append($"            {Cs(addon.DocumentationUrl)}, {Cs(addon.WebsiteUrl)}, {Cs(addon.Repository)}),\n");
}
text.Append("    ];\n}\n#endif\n");
await File.WriteAllTextAsync(output, text.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine($"Wrote {entries.Count} add-ons ({entries.Count(e => e.Addon.UpdateSource == "tags")} from tags) to {output}");
Report("Chosen or kept slugs", entries.Where(e => e.Note.Length > 0).Select(e => $"{e.Addon.Repository}: {e.Slug}, {e.Note}"));
Report("Same slug as a more popular repository", duplicates);
Report("Left out", leftOut);

// The plugin folder of the add-on ("" at the repository root), or null when several plugins leave it open.
(string? Folder, string Note) Choose(Addon addon, Tree tree)
{
    var folder = tree.Plugins[0];
    var note = "";
    if (tree.Plugins.Length > 1)
    {
        // Leave out bundled plugins, then take the one named like the repository: exactly ("netfox" of netfox.extras and
        // netfox.noray), otherwise as part of the name ("clyde" of godot-clyde-dialogue).
        var repository = Plain(Regex.Replace(addon.Repository.TrimEnd('/').Split('/', ':')[^1], @"\.git$", ""));
        var own = tree.Plugins.Where(p => !unambiguous.Contains(Name(p)) && Plain(Name(p)).Length > 0).ToArray();
        var named = own.Where(p => Plain(Name(p)) == repository).ToArray();
        if (named.Length == 0)
            named = own.Where(p => repository.Contains(Plain(Name(p))) || Plain(Name(p)).Contains(repository)).ToArray();
        if (named.Length != 1) return (null, "several plugins: " + string.Join(", ", tree.Plugins));
        folder = named[0];
        note = "of " + string.Join(", ", tree.Plugins);
    }
    if (folder.Length == 0) return (folder, "plugin at the repository root; no folder names its slug");
    return (folder, note);
}

async Task<Tree> ReadTree(Addon addon, CancellationToken ct)
{
    var work = Path.Combine(Path.GetTempPath(), "eplugin-known-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
        foreach (var arguments in new[] { new[] { "init", "-q" }, ["remote", "add", "origin", addon.CloneUrl],
                     ["fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", "refs/tags/" + addon.LatestVersion] })
        {
            var (code, _, error) = await Git(work, arguments, ct);
            if (code != 0) return new([], [], $"git {arguments[0]} failed: {error.Trim()}");
        }
        var (exit, listing, failure) = await Git(work, ["ls-tree", "-r", "-z", "--name-only", "FETCH_HEAD"], ct);
        if (exit != 0) return new([], [], "git ls-tree failed: " + failure.Trim());
        var files = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        // Every plugin.cfg folder that is not inside another one; nested ones are sub-plugins of it.
        var folders = files.Where(f => f == "plugin.cfg" || f.EndsWith("/plugin.cfg", StringComparison.Ordinal))
            .Select(f => f[..^"plugin.cfg".Length]).ToArray();
        var plugins = folders.Where(f => !folders.Any(o => o != f && f.StartsWith(o, StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToArray();
        return new(plugins, files, null);
    }
    catch (OperationCanceledException) { return new([], [], "timed out"); }
    finally
    {
        // git writes its object files read-only, which Windows refuses to delete.
        try
        {
            foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(work, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static async Task<(int Code, string Output, string Error)> Git(string directory, string[] arguments, CancellationToken ct)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromMinutes(2));
    var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var error = process.StandardError.ReadToEndAsync(timeout.Token);
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(true); throw; }
    return (process.ExitCode, await output, await error);
}

static string Name(string folder) => folder.TrimEnd('/').Split('/')[^1];
static string Plain(string name) => Regex.Replace(name.ToLowerInvariant(), "[-_. ]", "").Replace("godot", "");
static string Cs(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
static void Report(string title, IEnumerable<string> lines)
{
    var items = lines.ToArray();
    if (items.Length == 0) return;
    Console.WriteLine($"{title} ({items.Length}):");
    foreach (var line in items) Console.WriteLine("  " + line);
}

/// <param name="GithubUrl">The repository in JSON written before build_godot_addons.cs read other hosts.</param>
record Addon(string Name, int Stars, string? UpdateUrl, string? UpdateSource, string? LatestVersion, string WebsiteUrl,
    string DocumentationUrl, string? RepositoryUrl = null, string? GitUrl = null, string? GithubUrl = null)
{
    public string Repository => RepositoryUrl ?? GithubUrl ?? "";
    public string CloneUrl => GitUrl ?? Repository + ".git";
}
record Tree(string[] Plugins, string[] Files, string? Error);
