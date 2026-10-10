#:property TargetFramework=net10.0
#:property PublishAot=false
// Run with: dotnet run generate_known_plugins.cs [<add-ons json>] [<KnownPlugins.Data.cs>]
// Writes ePlugin's built-in list of add-on update sites from the output of build_godot_addons.cs. Without arguments it
// reads the newest godot_addons_active_*_stars.json in the working directory and writes
// src/eplugin-framework/addons/ePlugin/Internal/Update/KnownPlugins.Data.cs next to this tool.
//
// The entries are not verified: no package is installed or validated. Each add-on's slug, its folder in res://addons, is
// taken in this order:
// 1. the slug extra_addons.txt gives it;
// 2. the plugin folder in its latest release's ZIP, chosen and read like ePlugin's updater does (only the file list);
// 3. the slug it already has in the list, so the key stays stable;
// 4. the plugin folder in its repository at its latest release or version tag, read with git (git 2.25 or newer on PATH).
// No token is needed.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const long maximumDownload = 256L * 1024 * 1024; // ePlugin's updater refuses larger downloads too

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
if (addons.All(a => a.ReleaseAssets is null))
    Console.Error.WriteLine($"{input} lists no release assets, so no release ZIP is read; regenerate it with the current build_godot_addons.cs.");

// Slugs of the current list, by repository.
var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(output))
    foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(output), @"// (\S+?), (?:latest|verified)[^\n]*\n\s+new\(""([^""]+)"""))
        existing.TryAdd(match.Groups[1].Value, match.Groups[2].Value);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("eplugin-known-plugins/1.0");

// The plugin folders of each repository and its release ZIP; read in parallel, git and the downloads do the waiting.
var trees = new ConcurrentDictionary<Addon, Tree>();
var zips = new ConcurrentDictionary<Addon, Package>();
var done = 0;
await Parallel.ForEachAsync(addons, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (addon, ct) =>
{
    var tree = trees[addon] = await ReadTree(addon, ct);
    // The updater chooses among several ZIP assets by the plugin's slug.
    var hint = addon.Slug ?? existing.GetValueOrDefault(addon.Repository) ?? (tree.Plugins.Length == 1 ? Name(tree.Plugins[0]) : null);
    if (SelectAsset(addon.ReleaseAssets ?? [], hint) is { } asset) zips[addon] = await ReadZip(asset, ct);
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
    var zip = zips.GetValueOrDefault(addon);
    var repositoryName = Regex.Replace(addon.Repository.TrimEnd('/').Split('/', ':')[^1], @"\.git$", "");
    string? slug;
    string note;
    // The files whose .gdextension would make the updater refuse the plugin.
    IEnumerable<string> payload;
    if (zip is { Error: { } refused, Refuses: true })
    {
        // The updater downloads this ZIP for every update and refuses it, whatever the slug.
        leftOut.Add($"{addon.Repository}: the release ZIP {zip.Asset} {refused}");
        continue;
    }
    var zipSlug = zip?.Root is { } root ? ZipSlug(root, repositoryName) : null;
    if (addon.Slug is { } given)
    {
        (slug, note) = (given, "from extra_addons.txt");
        payload = zip?.Root is { } zipRoot ? Under(zip.Files, zipRoot)
            : tree.Plugins.FirstOrDefault(p => Name(p) == given) is { } folder ? Under(tree.Files, folder) : [];
    }
    else if (zipSlug is not null)
    {
        slug = zipSlug;
        note = existing.TryGetValue(addon.Repository, out var listed) && listed != zipSlug
            ? $"from the release ZIP {zip!.Asset}, the current list had {listed}" : "";
        payload = Under(zip!.Files, zip.Root!);
    }
    else
    {
        if (tree.Error is not null) { leftOut.Add($"{addon.Repository}: {tree.Error}"); continue; }
        if (tree.Plugins.Length == 0) { leftOut.Add($"{addon.Repository}: no plugin.cfg"); continue; }
        var (folder, chosenNote) = Choose(addon, tree, repositoryName);
        slug = folder is null || folder.Length == 0 ? null : Name(folder);
        note = chosenNote;
        // The current list's slug names the plugin where the repository does not, e.g. a plugin at its root.
        if (existing.TryGetValue(addon.Repository, out var kept) && slug != kept)
        {
            note = $"kept {kept} from the current list" + (slug is null ? "" : $", the repository names {slug}");
            slug = kept;
            folder = tree.Plugins.FirstOrDefault(p => Name(p) == kept) ?? (tree.Plugins.Contains("") ? "" : folder);
        }
        if (zip?.Error is { } failed) note = (note.Length == 0 ? "" : note + "; ") + $"release ZIP {zip.Asset} not used: {failed}";
        if (slug is null) { leftOut.Add($"{addon.Repository}: {note}"); continue; }
        payload = folder is null ? [] : Under(tree.Files, folder);
    }
    if (payload.Any(f => f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase)))
    {
        leftOut.Add($"{addon.Repository}: GDExtension plugin (ePlugin cannot update it)");
        continue;
    }
    chosen.Add((slug, addon, note));
}

// One entry per slug: the repository with the most stars keeps it, or the one extra_addons.txt names it for.
var entries = new List<(string Slug, Addon Addon, string Note)>();
var duplicates = new List<string>();
foreach (var group in chosen.GroupBy(c => c.Slug, StringComparer.Ordinal))
{
    var ranked = group.OrderByDescending(c => c.Addon.Slug is not null).ThenByDescending(c => c.Addon.Stars).ToArray();
    entries.Add(ranked[0]);
    duplicates.AddRange(ranked.Skip(1).Select(c => $"{c.Slug}: {c.Addon.Repository} ({c.Addon.Stars}) loses to {ranked[0].Addon.Repository} ({ranked[0].Addon.Stars})"));
}

var stars = Regex.Match(Path.GetFileName(input), @"_(\d+)_stars") is { Success: true } count ? count.Groups[1].Value : "?";
var text = new StringBuilder($$"""
    #if TOOLS
    namespace Enaweg.Plugin.Internal.Update;

    // Generated on {{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}} by tools/generate_known_plugins.cs from {{Path.GetFileName(input)}} (built by
    // tools/build_godot_addons.cs): popular add-ons with at least {{stars}} stars and a commit within the last year, plus those
    // in tools/extra_addons.txt, with a stable release or version tag. Each slug is the one extra_addons.txt gives, else
    // the plugin folder in the latest release's ZIP, else the add-on's slug in the previous list, else the plugin folder in
    // the repository at that release or tag. Add-ons without a plugin folder, with a GDExtension or with a release ZIP the
    // updater refuses are left out. The packages were not installed or validated.
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
(string? Folder, string Note) Choose(Addon addon, Tree tree, string repositoryName)
{
    var folder = tree.Plugins[0];
    var note = "";
    if (tree.Plugins.Length > 1)
    {
        // Leave out bundled plugins, then take the one named like the repository: exactly ("netfox" of netfox.extras and
        // netfox.noray), otherwise as part of the name ("clyde" of godot-clyde-dialogue).
        var repository = Plain(repositoryName);
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

// The ZIP asset ePlugin's updater downloads (ReleaseAssets.Select): the only one, else the one named like the slug, else
// the one named like an add-on or plugin. Without one it downloads the source archive, which the repository stands for.
static ReleaseAsset? SelectAsset(ReleaseAsset[] assets, string? slug)
{
    var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                 Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (zips.Length == 1) return zips[0];
    var named = slug is null ? [] : zips.Where(a => a.Name.Contains(slug, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (named.Length == 1) return named[0];
    var plugin = zips.Where(a => a.Name.Contains("addon", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase)).ToArray();
    return plugin.Length == 1 ? plugin[0] : null;
}

// The file list and plugin root of a release ZIP, found like the updater's SafeZipExtractor.PluginRoot: the folder of the
// shallowest plugin.cfg, refused when several are equally shallow or one lies outside it.
async Task<Package> ReadZip(ReleaseAsset asset, CancellationToken ct)
{
    var file = Path.Combine(Path.GetTempPath(), "eplugin-known-" + Guid.NewGuid().ToString("N") + ".zip");
    try
    {
        using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode) return new(asset.Name, null, [], $"cannot be downloaded ({(int)response.StatusCode})", false);
            if (response.Content.Headers.ContentLength > maximumDownload) return new(asset.Name, null, [], "is larger than the updater downloads", true);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(file);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if ((total += read) > maximumDownload) return new(asset.Name, null, [], "is larger than the updater downloads", true);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        string[] paths;
        try
        {
            using var archive = ZipFile.OpenRead(file);
            paths = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(p => !p.EndsWith('/')).ToArray();
        }
        catch (InvalidDataException) { return new(asset.Name, null, [], "is not a readable ZIP", true); }
        var folders = paths.Where(p => p == "plugin.cfg" || p.EndsWith("/plugin.cfg", StringComparison.Ordinal))
            .Select(p => p[..^"plugin.cfg".Length]).ToArray();
        if (folders.Length == 0) return new(asset.Name, null, paths, "has no plugin.cfg; the updater refuses it", true);
        var depth = folders.Min(Depth);
        var roots = folders.Where(f => Depth(f) == depth).ToArray();
        if (roots.Length > 1 || folders.Any(f => !f.StartsWith(roots[0], StringComparison.Ordinal)))
            return new(asset.Name, null, paths, "holds several plugins (" + string.Join(", ", folders) + "); the updater refuses it", true);
        return new(asset.Name, roots[0], paths, null, false);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
    {
        return new(asset.Name, null, [], "cannot be downloaded: " + ex.Message, false);
    }
    finally { try { File.Delete(file); } catch (IOException) { } }

    static int Depth(string folder) => folder.Count(c => c == '/');
}

// The slug a ZIP's plugin root names: its folder, unless the plugin is at the ZIP's root or in a source archive's
// wrapper folder ("repo-1.2.3/"), which the updater also installs but whose name is not the slug.
static string? ZipSlug(string root, string repositoryName)
{
    if (root.Length == 0) return null;
    var name = Name(root);
    var wrapper = root.Count(c => c == '/') == 1 && (name.Equals(repositoryName, StringComparison.OrdinalIgnoreCase) ||
                                                    name.StartsWith(repositoryName + "-", StringComparison.OrdinalIgnoreCase));
    return wrapper ? null : name;
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

static IEnumerable<string> Under(IEnumerable<string> files, string folder) => files.Where(f => f.StartsWith(folder, StringComparison.Ordinal));
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

/// <param name="Slug">The slug extra_addons.txt gives the add-on, which wins over every other.</param>
/// <param name="ReleaseAssets">The ZIP assets of the latest release; null in JSON written before they were listed.</param>
/// <param name="GithubUrl">The repository in JSON written before build_godot_addons.cs read other hosts.</param>
record Addon(string Name, int Stars, string? UpdateUrl, string? UpdateSource, string? LatestVersion, string WebsiteUrl,
    string DocumentationUrl, string? RepositoryUrl = null, string? GitUrl = null, string? GithubUrl = null,
    string? Slug = null, ReleaseAsset[]? ReleaseAssets = null)
{
    public string Repository => RepositoryUrl ?? GithubUrl ?? "";
    public string CloneUrl => GitUrl ?? Repository + ".git";
}
record ReleaseAsset(string Name, string Url);
record Tree(string[] Plugins, string[] Files, string? Error);
/// <param name="Root">The plugin root in the ZIP; null when it has none the updater accepts.</param>
/// <param name="Refuses">The updater refuses this ZIP, so the add-on cannot be updated from its releases.</param>
record Package(string Asset, string? Root, string[] Files, string? Error, bool Refuses);
