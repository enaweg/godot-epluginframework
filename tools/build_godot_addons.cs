#:property TargetFramework=net10.0
#:property PublishAot=false
// Run with: dotnet run build_godot_addons.cs
// Then write ePlugin's built-in list from its output with: dotnet run generate_known_plugins.cs
// Requires .NET 10 SDK and GITHUB_TOKEN. Only public repositories are read, but GitHub's GraphQL API answers no
// unauthenticated requests and its REST API allows 60 per hour, too few for several hundred repositories. A token
// without any scopes (classic) or with read-only access to public repositories (fine-grained) is enough.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string source = "https://raw.githubusercontent.com/sci-comp/godot-stars/main/README.md";
const int minStars = 20;
const int maxItems = 500;
const int maxRetries = 3;
// Only add-ons with a commit on their default branch within the last two years count as active.
var cutoff = DateTimeOffset.UtcNow.AddYears(-2);
// A version tag as ePlugin's updater reads it: an optional "v", then "1.2" or "1.2.3" with optional semantic
// prerelease/build suffixes (see SemVer.TryParse). Only versions without a prerelease are offered by default.
var versionTag = new Regex(@"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
// godot-stars categories that hold games, project templates or assets rather than add-ons.
var skippedCategories = new HashSet<string>(["Demos", "Shader", "Shaders", "Templates", "Projects", "Materials"],
    StringComparer.OrdinalIgnoreCase);

// cmd keeps the quotes of `set GITHUB_TOKEN="..."` in the value, and a pasted token can carry spaces.
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim().Trim('"', '\'').Trim();
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set GITHUB_TOKEN to a GitHub personal access token. It needs no scopes: GitHub's API " +
        "requires a token for GraphQL and rate-limits anonymous requests, even for public repositories.");
    Environment.ExitCode = 1;
    return;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("godot-active-addon-exporter/1.0");

async Task<string> RequestAsync(string url, object? body = null)
{
    var uri = new Uri(url);
    for (var attempt = 0; ; attempt++)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        // The token is only needed by the GitHub API; the README is public.
        if (uri.Host == "api.github.com")
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return content;
        if (response.StatusCode == HttpStatusCode.Unauthorized && uri.Host == "api.github.com")
            throw new UnauthorizedAccessException("GitHub rejected GITHUB_TOKEN (401 Bad credentials): it is mistyped, " +
                "expired, revoked, or a fine-grained token still awaiting approval. Check it with " +
                "`curl -H \"Authorization: Bearer <token>\" https://api.github.com/user`.");
        if (attempt < maxRetries && RetryDelay(response, attempt) is { } delay)
        {
            Console.Error.WriteLine($"{(int)response.StatusCode} from {uri.Host}, retrying in {delay.TotalSeconds:0} s");
            await Task.Delay(delay);
            continue;
        }
        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} from {url}: {content.Trim()}", null, response.StatusCode);
    }
}

// How long to wait before retrying a failed request, or null when retrying cannot help, e.g. for a token that lacks
// access. GitHub answers rate limits with 403 or 429 and says when to retry.
static TimeSpan? RetryDelay(HttpResponseMessage response, int attempt)
{
    var backoff = TimeSpan.FromSeconds(3 * Math.Pow(2, attempt));
    if (response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        return backoff;
    if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        return null;
    if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        return retryAfter;
    if (Header(response, "x-ratelimit-remaining") == "0")
    {
        // The primary rate limit resets at a fixed time; waiting longer than a few minutes is not worth it here.
        if (!long.TryParse(Header(response, "x-ratelimit-reset"), out var reset))
            return null;
        var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        return wait <= TimeSpan.FromMinutes(5) ? (wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1)) : null;
    }
    // 429 is always a rate limit; a plain 403 is a permission problem that retrying does not fix.
    return response.StatusCode == HttpStatusCode.TooManyRequests ? backoff : null;
}

static string? Header(HttpResponseMessage response, string name) =>
    response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

// Check the token before any work; /rate_limit does not count against the limit.
try { await RequestAsync("https://api.github.com/rate_limit"); }
catch (UnauthorizedAccessException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

// One candidate per GitHub slug, with the title shown in the Godot Asset Library ranking. Extra add-ons skip the filters.
var candidates = new Dictionary<string, (string Slug, string Name, bool Extra, string? PluginSlug)>(StringComparer.OrdinalIgnoreCase);
var markdown = await RequestAsync(source);
string? category = null;
var listingPattern = new Regex(@"^\|\s*\[([^]]+)\]\((https://github\.com/[^)/]+/[^)/]+)\)\s*\|\s*([\d,]+)\s*\|", RegexOptions.Compiled);
foreach (var line in markdown.Split('\n'))
{
    if (line.StartsWith("### ", StringComparison.Ordinal))
        category = line[4..].Trim();
    if (category is null || skippedCategories.Contains(category)) continue;

    var match = listingPattern.Match(line);
    // The ranking's star counts are older, so add-ons that have grown since are kept for the current count to decide.
    if (!match.Success || !int.TryParse(match.Groups[3].Value.Replace(",", ""), out var oldStars) || oldStars < minStars / 2)
        continue;
    var slug = match.Groups[2].Value["https://github.com/".Length..].TrimEnd('/');
    candidates[slug] = (slug, match.Groups[1].Value, false, null);
}

// A file-based app runs from a temporary build folder, so the script's own folder comes from AppContext.
var scriptDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string;

// Add-ons that are always listed, whatever their stars or activity: extra_addons.txt beside this file, one per line as
// "slug;url" or "url": the git repository URL (GitHub, GitLab or any other git host; a tag or release page works too)
// and optionally the add-on's folder in res://addons, which generate_known_plugins.cs then uses. # starts a comment.
// GitHub repositories are read with the others below; the rest by GitLab's API when the host has one, and by git.
var others = new List<(string Repository, string? PluginSlug)>();
var extrasPath = Path.Combine(scriptDirectory ?? Environment.CurrentDirectory, "extra_addons.txt");
if (File.Exists(extrasPath))
{
    foreach (var raw in await File.ReadAllLinesAsync(extrasPath))
    {
        var line = raw.Split('#', 2)[0].Trim();
        if (line.Length == 0) continue;
        var parts = line.Split(';', 2);
        var pluginSlug = parts.Length == 2 && parts[0].Trim() is { Length: > 0 } named ? named : null;
        if (pluginSlug is not null && (pluginSlug.IndexOfAny(['/', '\\', ':']) >= 0 || pluginSlug.Any(char.IsWhiteSpace) || pluginSlug is "." or ".."))
        {
            Console.Error.WriteLine($"extra_addons.txt: '{pluginSlug}' is not a folder name; ignoring line '{line}'.");
            continue;
        }
        var repository = Regex.Replace(parts[^1].Trim().TrimEnd('/'), @"(/-)?/(tags|releases)$", "");
        var github = Regex.Match(repository, @"^https://github\.com/([^/]+)/([^/]+?)(\.git)?$", RegexOptions.IgnoreCase);
        if (github.Success)
        {
            var slug = github.Groups[1].Value + "/" + github.Groups[2].Value;
            candidates[slug] = (slug, candidates.TryGetValue(slug, out var known) ? known.Name : github.Groups[2].Value, true, pluginSlug);
        }
        else others.Add((repository, pluginSlug));
    }
}

// Optional local seed of further add-ons, in the format of the output. Place it beside this file or in the working
// directory.
var seedPath = new[] { scriptDirectory, Environment.CurrentDirectory }
    .Where(directory => !string.IsNullOrEmpty(directory))
    .Select(directory => Path.Combine(directory!, "godot_addons_100.json"))
    .FirstOrDefault(File.Exists);
if (seedPath is not null)
{
    using var seed = JsonDocument.Parse(await File.ReadAllTextAsync(seedPath));
    foreach (var item in seed.RootElement.EnumerateArray())
    {
        if (!item.TryGetProperty("website_url", out var web) || web.ValueKind != JsonValueKind.String) continue;
        var match = Regex.Match(web.GetString() ?? "", @"^https://github\.com/([^/]+/[^/#]+)");
        if (!match.Success) continue;
        var slug = match.Groups[1].Value;
        if (!candidates.ContainsKey(slug))
            candidates[slug] = (slug, item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()! : slug, false, null);
    }
}

static IEnumerable<JsonElement> Nodes(JsonElement repo, string connection) =>
    repo.TryGetProperty(connection, out var value) && value.ValueKind == JsonValueKind.Object &&
    value.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
        ? nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object) : [];

// The highest stable version among the tag names, as it is written (e.g. "v1.2"), or null without one.
string? Newest(IEnumerable<string?> tags) => tags
    .Select(t => (Tag: t, Match: versionTag.Match(t ?? "")))
    .Where(t => t.Match.Success && !t.Match.Groups[4].Success)
    .Select(t => (t.Tag, Version: (Number(t.Match.Groups[1]), Number(t.Match.Groups[2]), Number(t.Match.Groups[3]))))
    .OrderByDescending(t => t.Version).Select(t => t.Tag).FirstOrDefault();
static long Number(Group group) => group.Success && long.TryParse(group.Value, out var value) ? value : 0;
// The ZIP assets, as the updater recognizes them: by name or by the URL's path.
static ReleaseAsset[] Zips(IEnumerable<ReleaseAsset> assets) => assets.Where(a => a.Url.Length > 0 &&
    (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
     Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).ToArray();
static string? Utc(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

// An extra add-on outside GitHub. GitLab's API (gitlab.com or self-hosted) gives its stars, activity and releases; any
// other host only its version tags, read with git. Null when it has no stable release or version tag.
async Task<Addon?> OtherAddon(string repository, string? pluginSlug)
{
    var https = Uri.TryCreate(repository, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    var web = https ? Regex.Replace(repository, @"\.git$", "") : repository;
    var git = https ? web + ".git" : repository;
    var name = Regex.Replace(web.Split('/', ':')[^1], @"\.git$", "");
    var stars = 0;
    DateTimeOffset? activity = null;
    string? release = null;
    ReleaseAsset[]? assets = null;
    var gitlab = false;
    if (https)
    {
        var api = $"https://{uri!.Authority}/api/v4/projects/{Uri.EscapeDataString(new Uri(web).AbsolutePath.Trim('/'))}";
        using var project = await TryJson(api);
        if (project?.RootElement is { ValueKind: JsonValueKind.Object } info && info.TryGetProperty("id", out _))
        {
            gitlab = true;
            if (info.TryGetProperty("star_count", out var count) && count.TryGetInt32(out var starCount)) stars = starCount;
            if (info.TryGetProperty("last_activity_at", out var last) && last.TryGetDateTimeOffset(out var lastActivity)) activity = lastActivity;
            if (info.TryGetProperty("name", out var title) && title.GetString() is { Length: > 0 } text) name = text;
            // Upcoming releases are not published yet; the updater skips them too.
            using var releases = await TryJson(api + "/releases?per_page=30");
            if (releases?.RootElement is { ValueKind: JsonValueKind.Array } list)
            {
                var published = list.EnumerateArray()
                    .Where(r => !(r.TryGetProperty("upcoming_release", out var upcoming) && upcoming.ValueKind == JsonValueKind.True)).ToArray();
                release = Newest(published.Select(r => r.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null));
                // The release's links; the updater falls back to the source archive, which the repository stands for.
                assets = Zips(published.Where(r => r.TryGetProperty("tag_name", out var tagName) && tagName.GetString() == release)
                    .SelectMany(r => r.TryGetProperty("assets", out var all) && all.TryGetProperty("links", out var links) &&
                                     links.ValueKind == JsonValueKind.Array ? links.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .Select(l => new ReleaseAsset(l.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        (l.TryGetProperty("direct_asset_url", out var direct) ? direct.GetString() : null) ??
                        (l.TryGetProperty("url", out var link) ? link.GetString() : null) ?? "")));
            }
        }
    }
    var tag = release is null ? Newest(await RemoteTags(git)) : null;
    if (release is null && tag is null) return null;
    // The updater reads GitLab's release and tag pages; another host by its .git URL, whose tags are the versions.
    if (!gitlab && !git.EndsWith(".git", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Extra add-on {repository}: the updater reads git repositories by a URL ending in .git; give that URL.");
        return null;
    }
    var updateUrl = release is not null ? web + "/-/releases" : gitlab ? web + "/-/tags" : git;
    return new Addon(name, stars, Utc(activity), updateUrl, release is not null ? "releases" : "tags", (release ?? tag)!,
        web, web, web, git, true, pluginSlug, release is null ? null : assets ?? []);
}

async Task<JsonDocument?> TryJson(string url)
{
    try
    {
        using var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()) : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { return null; }
}

// The tag names of a git repository; none when it cannot be read.
static async Task<IEnumerable<string>> RemoteTags(string repository)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in new[] { "ls-remote", "--tags", "--refs", "--", repository }) start.ArgumentList.Add(argument);
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var error = process.StandardError.ReadToEndAsync(timeout.Token);
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return []; }
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"git ls-remote {repository} failed: {(await error).Trim().Split('\n').Last()}");
        return [];
    }
    return (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t').Last().Trim()).Where(r => r.StartsWith("refs/tags/", StringComparison.Ordinal))
        .Select(r => r["refs/tags/".Length..]).ToArray();
}

var results = new List<Addon>();
var unversioned = new List<string>();
var values = candidates.Values.ToArray();
for (var offset = 0; offset < values.Length; offset += 20)
{
    var batch = values.Skip(offset).Take(20).ToArray();
    var fields = new StringBuilder("{ ");
    for (var i = 0; i < batch.Length; i++)
    {
        var slugParts = batch[i].Slug.Split('/', 2);
        fields.Append($"r{i}: repository(owner:{JsonSerializer.Serialize(slugParts[0])}, name:{JsonSerializer.Serialize(slugParts[1])}) {{ ");
        fields.Append("nameWithOwner isArchived isDisabled isFork stargazerCount url homepageUrl description ");
        fields.Append("defaultBranchRef { target { ... on Commit { committedDate } } } ");
        // As many releases as the updater's check reads, and the newest tags for repositories without a release.
        // Each release's assets, so generate_known_plugins.cs can read the ZIP the updater downloads.
        fields.Append("releases(first: 30, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { tagName isDraft isPrerelease ");
        fields.Append("releaseAssets(first: 20) { nodes { name downloadUrl } } } } ");
        fields.Append("refs(refPrefix: \"refs/tags/\", first: 100, orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) { nodes { name } } } ");
    }
    fields.Append('}');

    using var json = JsonDocument.Parse(await RequestAsync("https://api.github.com/graphql", new { query = fields.ToString() }));
    var root = json.RootElement;
    if (root.TryGetProperty("errors", out var errors))
        Console.Error.WriteLine($"GitHub API warnings: {errors}");
    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        throw new InvalidOperationException("GitHub GraphQL returned no repository data.");

    for (var i = 0; i < batch.Length; i++)
    {
        var extra = batch[i].Extra;
        if (!data.TryGetProperty($"r{i}", out var repo) || repo.ValueKind != JsonValueKind.Object)
        {
            if (extra) Console.Error.WriteLine($"Extra add-on https://github.com/{batch[i].Slug} was not found on GitHub.");
            continue;
        }
        if (!extra && (repo.GetProperty("isArchived").GetBoolean() || repo.GetProperty("isDisabled").GetBoolean()
            || repo.GetProperty("isFork").GetBoolean())) continue;
        var stars = repo.GetProperty("stargazerCount").GetInt32();
        if (!extra && stars < minStars) continue;
        // committedDate keeps the committer's time zone, so compare and store it in UTC.
        DateTimeOffset? committed = repo.TryGetProperty("defaultBranchRef", out var branch) && branch.ValueKind == JsonValueKind.Object
            && branch.TryGetProperty("target", out var target) && target.TryGetProperty("committedDate", out var dateValue)
            && DateTimeOffset.TryParse(dateValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        if (!extra && (committed is null || committed < cutoff)) continue;
        var url = repo.GetProperty("url").GetString()!.TrimEnd('/');
        // A published stable release (whose ZIP the updater downloads) wins; otherwise the repository's version tags,
        // which the updater installs from git through the tag page URL. Without either there is nothing to update to.
        var release = Newest(Nodes(repo, "releases")
            .Where(r => !r.GetProperty("isDraft").GetBoolean() && !r.GetProperty("isPrerelease").GetBoolean())
            .Select(r => r.GetProperty("tagName").GetString()));
        var tag = release is null ? Newest(Nodes(repo, "refs").Select(r => r.GetProperty("name").GetString())) : null;
        if (release is null && tag is null)
        {
            unversioned.Add(url);
            continue;
        }
        var homepage = repo.TryGetProperty("homepageUrl", out var hp) && hp.ValueKind == JsonValueKind.String
            ? hp.GetString() : null;
        var assets = release is null ? null : Zips(Nodes(repo, "releases").Where(r => r.GetProperty("tagName").GetString() == release)
            .SelectMany(r => Nodes(r, "releaseAssets"))
            .Select(a => new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("downloadUrl").GetString() ?? "")));
        results.Add(new Addon(batch[i].Name, stars, Utc(committed),
            release is not null ? url + "/releases" : url + "/tags", release is not null ? "releases" : "tags", (release ?? tag)!,
            string.IsNullOrWhiteSpace(homepage) ? url : homepage, url + "#readme", url, url + ".git", extra, batch[i].PluginSlug, assets));
    }
    Console.Error.WriteLine($"Checked {Math.Min(offset + 20, values.Length)}/{values.Length} candidates");
}

// Extra add-ons on other hosts.
foreach (var (repository, pluginSlug) in others)
{
    if (await OtherAddon(repository, pluginSlug) is { } addon) results.Add(addon);
    else unversioned.Add(repository);
}

// Several candidates can name one repository, e.g. an old name that GitHub redirects to the renamed one. The extra
// add-ons are always listed, also beyond the maximum.
var unique = results.DistinctBy(x => x.RepositoryUrl, StringComparer.OrdinalIgnoreCase).ToArray();
var output = unique.Where(x => !x.Extra).OrderByDescending(x => x.Stars).Take(maxItems).Concat(unique.Where(x => x.Extra))
    .DistinctBy(x => x.RepositoryUrl, StringComparer.OrdinalIgnoreCase)
    .OrderByDescending(x => x.Stars).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
var dest = Path.Combine(Environment.CurrentDirectory, $"godot_addons_active_{minStars}_stars.json");
await File.WriteAllTextAsync(dest, JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}) + "\n");
Console.WriteLine($"Saved {output.Length} qualifying add-ons to {dest} " +
    $"({output.Count(a => a.UpdateSource == "releases")} with releases, {output.Count(a => a.UpdateSource == "tags")} with version tags only)");
if (unversioned.Count > 0)
    Console.WriteLine($"Left out {unversioned.Count} add-ons without a stable release or version tag: {string.Join(", ", unversioned)}");
Console.WriteLine("Candidate discovery uses an older ranking; the result may omit recently published add-ons. " +
    "List those in extra_addons.txt.");

/// <param name="LastCommitAt">The last commit on the default branch, or GitLab's last activity; null when unknown.</param>
/// <param name="UpdateUrl">The releases page, or the tag page (a .git URL off GitHub and GitLab) without a release.</param>
/// <param name="UpdateSource">"releases" or "tags": where <paramref name="LatestVersion"/> was found.</param>
/// <param name="LatestVersion">The tag name of the newest stable release or version tag.</param>
/// <param name="RepositoryUrl">The repository's web page, or its git URL when it has none.</param>
/// <param name="GitUrl">The URL to clone the repository from.</param>
/// <param name="Extra">Listed in extra_addons.txt, so it skips the star and activity filters.</param>
/// <param name="Slug">The add-on's folder in res://addons as extra_addons.txt gives it; null to let the generator find it.</param>
/// <param name="ReleaseAssets">The ZIP assets of the latest release; null without a release.</param>
record Addon(string Name, int Stars, string? LastCommitAt, string UpdateUrl, string UpdateSource, string LatestVersion,
    string WebsiteUrl, string DocumentationUrl, string RepositoryUrl, string GitUrl, bool Extra, string? Slug = null,
    ReleaseAsset[]? ReleaseAssets = null);
record ReleaseAsset(string Name, string Url);
