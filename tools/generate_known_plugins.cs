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
// A plugin folder holds a plugin.cfg; without any, the folder of a GDExtension add-on (its .gdextension below addons/)
// counts, which the updater installs without plugin.cfg. An add-on found by GitLab's or Codeberg's search is only listed
// when its slug is named like its repository, since a search also finds games that hold other add-ons. Add-ons whose name
// says they are made for Godot 3, unless their repository at that tag shows Godot 4 (then the name loses that note), and
// slugs too generic to match a project's folder by name (GenericSlugs), are left out.
// No token is needed.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const long maximumDownload = 256L * 1024 * 1024; // ePlugin's updater refuses larger downloads too
// The list matches add-ons by their folder name only, so a project's own addons/plugin or addons/log would be offered
// someone else's add-on with that slug.
string[] genericSlugs = ["addon", "addons", "plugin", "plugins", "misc", "src", "scripts", "tools", "log", "logger", "gd_", "yaml", "guide", "repl"];

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
    // The updater chooses among several ZIP assets by the plugin's slug. A .gdextension outside addons/ names none: LimboAI
    // has gdextension/limboai.gdextension in its repository and addons/limboai/ in its release ZIP.
    var hint = addon.Slug ?? existing.GetValueOrDefault(addon.Repository) ??
        (tree.Plugins.Length == 1 && NamesSlug(tree, tree.Plugins[0]) ? Name(tree.Plugins[0]) : null);
    if (SelectAsset(addon.ReleaseAssets ?? [], hint) is { } asset) zips[addon] = await ReadZip(asset, hint, ct);
    Console.Error.Write($"\rRead {Interlocked.Increment(ref done)}/{addons.Length} repositories");
});
Console.Error.WriteLine();

// Plugins that one repository has on its own; another repository holding them too (gut, gdUnit4) only bundles them.
var unambiguous = trees.Values.Where(t => t.Plugins.Length == 1).Select(t => Name(t.Plugins[0])).ToHashSet(StringComparer.Ordinal);
var chosen = new List<(string Slug, Addon Addon, string Note)>();
// The add-ons left out: the kind of reason, which the statistics count, and the reason itself.
var leftOut = new List<(Addon Addon, string Kind, string Reason)>();
foreach (var addon in addons)
{
    var tree = trees[addon];
    // The ranking keeps an Asset Library title such as "TODO Manager (Godot 3.x)" after the add-on moved to Godot 4.
    if (ForGodot3(addon.Name) && !tree.Godot4)
    {
        leftOut.Add((addon, "made for Godot 3", $"its name {addon.Name} says it is made for Godot 3, which Godot 4 cannot load, " +
            "and its repository shows no Godot 4 project or GDExtension"));
        continue;
    }
    var zip = zips.GetValueOrDefault(addon);
    var repositoryName = Regex.Replace(addon.Repository.TrimEnd('/').Split('/', ':')[^1], @"\.git$", "");
    string? slug;
    string note;
    if (zip is { Error: { } refused, Refuses: true })
    {
        // The updater downloads this ZIP for every update and refuses it, whatever the slug.
        leftOut.Add((addon, "release ZIP refused by the updater", $"the release ZIP {zip.Asset} {refused}"));
        continue;
    }
    var zipSlug = zip?.Root is { } root ? ZipSlug(root, repositoryName) : null;
    if (addon.Slug is { } given)
    {
        (slug, note) = (given, "from extra_addons.txt");
    }
    else if (zipSlug is not null)
    {
        slug = zipSlug;
        note = existing.TryGetValue(addon.Repository, out var listed) && listed != zipSlug
            ? $"from the release ZIP {zip!.Asset}, the current list had {listed}" : "";
    }
    else
    {
        if (tree.Error is not null) { leftOut.Add((addon, "repository not readable with git", tree.Error)); continue; }
        if (tree.Plugins.Length == 0)
        {
            leftOut.Add((addon, "no plugin.cfg and no GDExtension in addons/", "no plugin.cfg and no GDExtension in addons/"));
            continue;
        }
        var (folder, chosenNote) = Choose(addon, tree, repositoryName);
        slug = folder is null || folder.Length == 0 ? null : Name(folder);
        note = chosenNote;
        // The current list's slug names the plugin where the repository does not, e.g. a plugin at its root.
        if (existing.TryGetValue(addon.Repository, out var kept) && slug != kept)
        {
            note = $"kept {kept} from the current list" + (slug is null ? "" : $", the repository names {slug}");
            slug = kept;
        }
        if (zip?.Error is { } failed) note = (note.Length == 0 ? "" : note + "; ") + $"release ZIP {zip.Asset} not used: {failed}";
        if (slug is null)
        {
            leftOut.Add((addon, folder is null ? "several plugins, none named like the repository" : "plugin at the repository root", note));
            continue;
        }
        // The updater chooses the ZIP by this slug, which may be one no hint led to before, so the list does not keep
        // changing between runs.
        if (zip is null && SelectAsset(addon.ReleaseAssets ?? [], slug) is { } asset &&
            await ReadZip(asset, slug, CancellationToken.None) is { Error: { } refusal, Refuses: true })
        {
            leftOut.Add((addon, "release ZIP refused by the updater", $"the release ZIP {asset.Name} {refusal}"));
            continue;
        }
    }
    // A search also finds games and projects that hold someone else's add-on; only a plugin named like its repository is
    // the repository's own.
    if (addon.Searched && addon.Slug is null && !NamedLike(slug, repositoryName))
    {
        leftOut.Add((addon, "found by a search, plugin not named like the repository", $"found by a search, and its plugin {slug} is " +
            "not named like the repository; list it in extra_addons.txt if it is the repository's own"));
        continue;
    }
    if (genericSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
    {
        leftOut.Add((addon, "slug too generic", $"its slug {slug} is too generic to tell the add-on from a project's own folder of that name"));
        continue;
    }
    chosen.Add((slug, addon, note));
}

// One entry per slug: the repository with the most stars keeps it, or the one extra_addons.txt names it for.
var entries = new List<(string Slug, Addon Addon, string Note)>();
var duplicates = new List<(string Slug, Addon Addon, Addon Winner)>();
foreach (var group in chosen.GroupBy(c => c.Slug, StringComparer.Ordinal))
{
    var ranked = group.OrderByDescending(c => c.Addon.Slug is not null).ThenByDescending(c => c.Addon.Stars).ToArray();
    entries.Add(ranked[0]);
    duplicates.AddRange(ranked.Skip(1).Select(c => (c.Slug, c.Addon, ranked[0].Addon)));
}

var stars = Regex.Match(Path.GetFileName(input), @"_(\d+)_stars") is { Success: true } count ? count.Groups[1].Value : "?";
var text = new StringBuilder($$"""
    #if TOOLS
    namespace Enaweg.Plugin.Internal.Update;

    // Generated on {{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}} by tools/generate_known_plugins.cs from {{Path.GetFileName(input)}} (built by
    // tools/build_godot_addons.cs): popular add-ons with at least {{stars}} stars on GitHub (fewer on GitLab and Codeberg) and a
    // commit within the last two years, plus those in tools/extra_addons.txt, with a release or version tag. Add-ons found by
    // GitLab's or Codeberg's search are listed only with a slug named like their repository. Each slug is the one
    // extra_addons.txt gives, else the plugin folder in the latest release's ZIP, else the add-on's slug in the previous list,
    // else the plugin folder in the repository at that release or tag. Add-ons without a plugin folder, with a release ZIP
    // the updater refuses, named as made for Godot 3 without a Godot 4 project or GDExtension in their repository, or with a
    // slug too generic to match by folder name are left out. The packages were not installed or validated.
    internal static partial class KnownPlugins
    {
        private static KnownPlugin[] Entries() =>
        [

    """);
foreach (var (slug, addon, _) in entries.OrderBy(e => e.Slug, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Slug, StringComparer.Ordinal))
{
    text.Append($"        // {addon.Repository}, latest {(addon.UpdateSource == "tags" ? "tag" : "release")} {addon.LatestVersion}\n");
    text.Append($"        new({Cs(slug)}, {Cs(WithoutGodot3(addon.Name))}, {Cs(addon.UpdateUrl!)},\n");
    text.Append($"            {Cs(addon.DocumentationUrl)}, {Cs(addon.WebsiteUrl)}, {Cs(addon.Repository)}),\n");
}
text.Append("    ];\n}\n#endif\n");
await File.WriteAllTextAsync(output, text.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine($"Wrote {entries.Count} add-ons ({entries.Count(e => e.Addon.UpdateSource == "tags")} from tags) to {output}");
Report("Chosen or kept slugs", entries.Where(e => e.Note.Length > 0).Select(e => $"{e.Addon.Repository}: {e.Slug}, {e.Note}"));
Report("Same slug as a more popular repository",
    duplicates.Select(d => $"{d.Slug}: {d.Addon.Repository} ({d.Addon.Stars}) loses to {d.Winner.Repository} ({d.Winner.Stars})"));
Report("Left out", leftOut.Select(l => $"{l.Addon.Repository}: {l.Reason}"));
Compare(entries, repository => leftOut.FirstOrDefault(l => SameRepository(l.Addon, repository)) is { Addon: not null } left ? left.Reason
    : duplicates.FirstOrDefault(d => SameRepository(d.Addon, repository)) is { Addon: not null } lost ? $"{lost.Slug} goes to {lost.Winner.Repository}"
    : $"not in {Path.GetFileName(input)}; see the output of build_godot_addons.cs");
Statistics(entries.Count, leftOut.Select(l => (l.Addon.Repository, l.Addon.Stars, l.Kind, l.Reason))
    .Concat(duplicates.Select(d => (d.Addon.Repository, d.Addon.Stars, "same slug as a more popular repository", $"{d.Slug} goes to {d.Winner.Repository}")))
    .ToArray());

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

// The ZIP asset ePlugin's updater downloads (ReleaseAssets.Select): the only one, else the one named exactly like the
// slug, else the one named like the slug (also with '-', '_' and '.' mixed up), else the one named like an add-on, plugin
// or GDExtension, leaving out demo, example, sample and tutorial projects among several and preferring the one named
// like an add-on of those left. Without one it downloads the source archive, which the repository stands for.
static ReleaseAsset? SelectAsset(ReleaseAsset[] assets, string? slug)
{
    var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                 Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (zips.Length == 1) return zips[0];
    static bool IsAddon(string name) => new[] { "addon", "plugin", "gdextension" }.Any(addon => name.Contains(addon, StringComparison.OrdinalIgnoreCase));
    foreach (var matches in new Func<string, bool>[]
             {
                 name => slug is not null && Plain(Path.GetFileNameWithoutExtension(name), keepGodot: true) == Plain(slug, keepGodot: true),
                 name => slug is not null && name.Contains(slug, StringComparison.OrdinalIgnoreCase),
                 name => slug is not null && Plain(name, keepGodot: true).Contains(Plain(slug, keepGodot: true), StringComparison.Ordinal),
                 IsAddon
             })
    {
        var named = zips.Where(a => matches(a.Name)).ToArray();
        if (named.Length > 1)
            named = named.Where(a => !new[] { "demo", "example", "sample", "tutorial" }.Any(extra => a.Name.Contains(extra, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (named.Length > 1 && named.Where(a => IsAddon(a.Name)).ToArray() is { Length: > 0 } addons) named = addons;
        if (named.Length == 1) return named[0];
    }
    return null;
}

// The file list and plugin root of a release ZIP, found like the updater's SafeZipExtractor.PackageRoot: the folder of the
// shallowest plugin.cfg, refused when several are equally shallow or one lies outside it. A ZIP without plugin.cfg holds
// a GDExtension add-on, whose root is the folder named like its slug that holds a .gdextension file (ExtensionRoot).
async Task<Package> ReadZip(ReleaseAsset asset, string? slug, CancellationToken ct)
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
        if (folders.Length == 0)
        {
            var extensions = ExtensionFolders(paths);
            // The updater looks for the folder named like the slug; without one known, the ZIP's only add-on folder names it.
            var named = slug is null ? extensions : extensions.Where(f => Name(f) == slug).ToArray();
            if (named.Length == 0) return new(asset.Name, null, paths, "has no plugin.cfg and no GDExtension folder" + (slug is null ? "" : $" named {slug}") + "; the updater refuses it", true);
            var shallowest = named.Where(f => Depth(f) == named.Min(Depth)).ToArray();
            if (shallowest.Length > 1) return new(asset.Name, null, paths, "holds several GDExtensions (" + string.Join(", ", shallowest) + "); the updater refuses it", true);
            return new(asset.Name, shallowest[0], paths, null, false);
        }
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
        // Every plugin.cfg folder that is not inside another one; nested ones are sub-plugins of it. Without any, the
        // add-on folders of its GDExtensions, which the updater installs without plugin.cfg.
        var folders = files.Where(f => f == "plugin.cfg" || f.EndsWith("/plugin.cfg", StringComparison.Ordinal))
            .Select(f => f[..^"plugin.cfg".Length]).ToArray();
        if (folders.Length == 0) folders = ExtensionFolders(files);
        var plugins = folders.Where(f => !folders.Any(o => o != f && f.StartsWith(o, StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToArray();
        return new(plugins, files, null, ForGodot3(addon.Name) && await ShowsGodot4(work, files, ct));
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

// Whether the repository at the fetched tag is made for Godot 4: it has a GDExtension, which only Godot 4 loads, or Godot
// projects that are all Godot 4 ones (config_version 5; Godot 3 writes 4). Only the project.godot files are fetched.
static async Task<bool> ShowsGodot4(string work, string[] files, CancellationToken ct)
{
    if (files.Any(f => f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase))) return true;
    var versions = new List<int>();
    foreach (var project in files.Where(f => f == "project.godot" || f.EndsWith("/project.godot", StringComparison.Ordinal)))
    {
        var (code, text, _) = await Git(work, ["cat-file", "-p", "FETCH_HEAD:" + project], ct);
        if (code == 0 && Regex.Match(text, @"^config_version\s*=\s*(\d+)", RegexOptions.Multiline) is { Success: true } match)
            versions.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }
    return versions.Count > 0 && versions.All(v => v >= 5);
}

// The name without a note that it is made for Godot 3, for an add-on whose repository shows it moved to Godot 4:
// "TODO Manager (Godot 3.x)" is listed as "TODO Manager".
static string WithoutGodot3(string name)
{
    if (!ForGodot3(name)) return name;
    var stripped = Regex.Replace(name, @"\s*(?:[-–:]\s*)?[(\[]?\bgodot[\s-]*3(?:\.[\dx]+)?\b[)\]]?", "", RegexOptions.IgnoreCase);
    stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim(' ', '-', '–', ':');
    return stripped.Length > 0 ? stripped : name;
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
// The input names no Godot version, but an add-on's name often does: "TODO Manager (Godot 3.x)", "WebRTC plugin - Godot 3.5".
// "Godot 3D ..." is not one of them.
static bool ForGodot3(string name) => Regex.IsMatch(name, @"\bgodot[\s-]*3\b", RegexOptions.IgnoreCase);
static int Depth(string folder) => folder.Count(c => c == '/');
// Whether a plugin folder of the repository is one the updater installs by its name: a plugin.cfg's, or a GDExtension
// add-on's below addons/.
static bool NamesSlug(Tree tree, string folder) =>
    tree.Files.Contains(folder + "plugin.cfg", StringComparer.Ordinal) || Regex.IsMatch(folder, "(^|/)addons/[^/]+/$");

// The folders of the GDExtension add-ons among the files: the folder below an addons/ folder that holds a .gdextension
// file ("x/addons/servo/bin/servo.gdextension" is in "x/addons/servo/"), else the folder of the file itself. Hidden
// folders are left out, Godot does not load extensions in them.
static string[] ExtensionFolders(IEnumerable<string> files) =>
    files.Where(f => f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase) && !f.Split('/').Any(p => p.StartsWith('.')))
        .Select(f =>
        {
            var parts = f.Split('/');
            var addons = parts.Length < 2 ? -1 : Array.LastIndexOf(parts, "addons", parts.Length - 2);
            return addons >= 0 && addons + 2 < parts.Length ? string.Join('/', parts[..(addons + 2)]) + "/"
                : parts.Length > 1 ? string.Join('/', parts[..^1]) + "/" : "";
        })
        .Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
static string Plain(string name, bool keepGodot = false) => keepGodot ? Regex.Replace(name.ToLowerInvariant(), "[-_. ]", "")
    : Regex.Replace(name.ToLowerInvariant(), "[-_. ]", "").Replace("godot", "");
// Whether a plugin folder is named like the repository, as one is part of the other ("sculptor" of godot-sculptor).
static bool NamedLike(string slug, string repositoryName) => Plain(slug) is { Length: > 0 } plugin &&
    Plain(repositoryName) is { Length: > 0 } repository && (repository.Contains(plugin) || plugin.Contains(repository));
// The add-ons that are new and those whose slug changed since the current list, and the most-starred of those no longer
// listed with why they are left out now. Warns when clearly fewer are listed than before.
void Compare(List<(string Slug, Addon Addon, string Note)> now, Func<string, string> why)
{
    var repositories = now.Select(e => e.Addon.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var added = now.Where(e => !existing.ContainsKey(e.Addon.Repository)).OrderBy(e => e.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
    // The current list has no stars; an add-on still in the input has its stars there.
    var removed = existing.Where(e => !repositories.Contains(e.Key))
        .Select(e => (Repository: e.Key, Slug: e.Value, Stars: addons.FirstOrDefault(a => SameRepository(a, e.Key))?.Stars))
        .OrderByDescending(e => e.Stars ?? -1).ThenBy(e => e.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
    var renamed = now.Where(e => existing.TryGetValue(e.Addon.Repository, out var slug) && slug != e.Slug)
        .OrderBy(e => e.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
    Console.WriteLine($"Compared with the current list: {existing.Count} before, {now.Count} now ({now.Count - existing.Count:+0;-0;0}), " +
        $"{added.Length} new, {removed.Length} no longer listed, {renamed.Length} with another slug");
    foreach (var (slug, addon, _) in added) Console.WriteLine($"  + {slug}: {addon.Repository} ({addon.Stars} stars)");
    foreach (var (repository, slug, stars) in removed.Take(10))
        Console.WriteLine($"  - {slug}: {repository}{(stars is null ? "" : $" ({stars} stars)")}: {why(repository)}");
    if (removed.Length > 10) Console.WriteLine($"  ... and {removed.Length - 10} more no longer listed");
    foreach (var (slug, addon, _) in renamed) Console.WriteLine($"  ~ {existing[addon.Repository]} is now {slug}: {addon.Repository}");
    if (now.Count < existing.Count * 0.9)
        Console.Error.WriteLine($"Warning: {existing.Count - now.Count} add-ons fewer than before. Check the reasons above, and the " +
            $"statistics of build_godot_addons.cs for {Path.GetFileName(input)}, before committing the list.");
}
static bool SameRepository(Addon addon, string repository) => string.Equals(addon.Repository, repository, StringComparison.OrdinalIgnoreCase);

// How many add-ons were added and filtered out, how many each kind of reason left out, and the most-starred ones left out,
// which are the likeliest to be add-ons the slug rules should not have left out.
static void Statistics(int added, (string Repository, int Stars, string Kind, string Reason)[] filtered)
{
    Console.WriteLine($"Statistics: {added} added, {filtered.Length} filtered out of {added + filtered.Length} add-ons");
    foreach (var kind in filtered.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {kind.Count(),5}  {kind.Key}");
    if (filtered.Length == 0) return;
    Console.WriteLine("Most-starred filtered out:");
    foreach (var (repository, stars, _, reason) in filtered.OrderByDescending(f => f.Stars)
                 .ThenBy(f => f.Repository, StringComparer.OrdinalIgnoreCase).Take(10))
        Console.WriteLine($"  {stars,6} stars  {repository}: {reason}");
}
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
/// <param name="Searched">Found by GitLab's or Codeberg's search rather than in a curated ranking.</param>
record Addon(string Name, int Stars, string? UpdateUrl, string? UpdateSource, string? LatestVersion, string WebsiteUrl,
    string DocumentationUrl, string? RepositoryUrl = null, string? GitUrl = null, string? GithubUrl = null,
    string? Slug = null, ReleaseAsset[]? ReleaseAssets = null, bool Searched = false)
{
    public string Repository => RepositoryUrl ?? GithubUrl ?? "";
    public string CloneUrl => GitUrl ?? Repository + ".git";
}
record ReleaseAsset(string Name, string Url);
/// <param name="Godot4">The add-on's name says Godot 3, but the repository shows a Godot 4 project or GDExtension.</param>
record Tree(string[] Plugins, string[] Files, string? Error, bool Godot4 = false);
/// <param name="Root">The plugin root in the ZIP; null when it has none the updater accepts.</param>
/// <param name="Refuses">The updater refuses this ZIP, so the add-on cannot be updated from its releases.</param>
record Package(string Asset, string? Root, string[] Files, string? Error, bool Refuses);
