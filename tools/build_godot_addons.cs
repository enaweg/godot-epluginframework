#:property TargetFramework=net10.0
#:property PublishAot=false
// Run with: dotnet run build_godot_addons.cs
// Then write ePlugin's built-in list from its output with: dotnet run generate_known_plugins.cs
// Add-ons are found on GitHub (the godot-stars ranking), GitLab and Codeberg (their searches for Godot projects), read from
// all three hosts at the same time.
// Requires .NET 10 SDK and a GitHub token. Only public repositories are read, but GitHub's GraphQL API answers no
// unauthenticated requests and its REST API allows 60 per hour, too few for several hundred repositories. A token
// without any scopes (classic) or with read-only access to public repositories (fine-grained) is enough. GitLab and
// Codeberg are read without a token too; with one, GitLab also tells which projects are forks.
// Each token comes from an environment variable or, when that is not set, from the host's CLI if it is installed and
// logged in, so none has to be set by hand:
// - GitHub: GITHUB_TOKEN or GH_TOKEN, else `gh auth token`;
// - GitLab: GITLAB_TOKEN or GITLAB_ACCESS_TOKEN, else glab's login (`glab auth login`);
// - Codeberg: CODEBERG_TOKEN, else a codeberg.org login of tea, the Gitea and Forgejo CLI (`tea login add`).
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string source = "https://raw.githubusercontent.com/sci-comp/godot-stars/main/README.md";
const int minStars = 20;
// GitLab and Codeberg have far fewer users than GitHub, so their add-ons have fewer stars.
const int minStarsElsewhere = 5;
const int maxItems = 500;
const int maxRetries = 3;
// Only add-ons with a commit on their default branch within the last two years count as active.
var cutoff = DateTimeOffset.UtcNow.AddYears(-2);
// A version tag as ePlugin's updater reads it: an optional "v", then "1.2", "1.2.3" or "1.2.3.4", optionally followed by any
// prerelease suffix after a '-' and build metadata after a '+' (see SemVer.TryParse). Prereleases are versions like any
// other, as they are for the updater.
var versionTag = new Regex(@"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?)?(?:-([^\s\p{Cc}+]+))?(?:\+[^\s\p{Cc}]+)?$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
// godot-stars categories that hold games, project templates or assets rather than add-ons.
var skippedCategories = new HashSet<string>(["Demos", "Shader", "Shaders", "Templates", "Projects", "Materials"],
    StringComparer.OrdinalIgnoreCase);
// GitLab and Codeberg have no such categories, and their searches mostly find games. A game is a repository with a game
// topic but no add-on topic.
var gameTopics = new HashSet<string>(["game", "games", "videogame", "video-game", "godot-game", "game-jam", "gamejam", "jam"],
    StringComparer.OrdinalIgnoreCase);
var addonTopics = new HashSet<string>(["plugin", "addon", "add-on", "godot-plugin", "godot-addon", "editor-plugin", "library",
    "gdextension", "tool", "tools"], StringComparer.OrdinalIgnoreCase);

// The token for each API host, and where it came from. A token is only sent to its own host.
var tokens = new ConcurrentDictionary<string, (string Token, string Source)>(StringComparer.OrdinalIgnoreCase);
if ((FromEnvironment("GITHUB_TOKEN", "GH_TOKEN") ?? GhToken("github.com")) is { } githubLogin) tokens["api.github.com"] = githubLogin;
if ((FromEnvironment("GITLAB_TOKEN", "GITLAB_ACCESS_TOKEN") ?? GlabToken("gitlab.com")) is { } gitlabLogin) tokens["gitlab.com"] = gitlabLogin;
if ((FromEnvironment("CODEBERG_TOKEN") ?? TeaToken("codeberg.org")) is { } codebergLogin) tokens["codeberg.org"] = codebergLogin;
if (!tokens.ContainsKey("api.github.com"))
{
    Console.Error.WriteLine("No GitHub token: log in with `gh auth login`, or set GITHUB_TOKEN to a GitHub personal access " +
        "token. It needs no scopes: GitHub's API requires a token for GraphQL and rate-limits anonymous requests, even for " +
        "public repositories.");
    Environment.ExitCode = 1;
    return;
}
Console.Error.WriteLine("Tokens: " + string.Join(", ", new[] { ("GitHub", "api.github.com"), ("GitLab", "gitlab.com"), ("Codeberg", "codeberg.org") }
    .Select(h => $"{h.Item1} {(tokens.TryGetValue(h.Item2, out var login) ? "from " + login.Source : "none")}")));

// cmd keeps the quotes of `set GITHUB_TOKEN="..."` in the value, and a pasted token can carry spaces.
static string? Clean(string? value) => value?.Trim().Trim('"', '\'').Trim() is { Length: > 0 } token ? token : null;

static (string Token, string Source)? FromEnvironment(params string[] variables)
{
    foreach (var variable in variables)
        if (Clean(Environment.GetEnvironmentVariable(variable)) is { } token) return (token, variable);
    return null;
}

static (string Token, string Source)? GhToken(string host) =>
    Cli("gh", "auth", "token", "--hostname", host) is { } gh && Clean(gh.Output) is { } token ? (token, "gh auth token") : null;

// glab has no command that prints only the token, but its status shows it, also one kept in the system keyring. It
// writes the status to stderr.
static (string Token, string Source)? GlabToken(string host)
{
    if (Cli("glab", "auth", "status", "--show-token", "--hostname", host) is not { } status) return null;
    var token = Regex.Matches(status.Output + "\n" + status.Error, @"\bToken\b[^:\r\n]*:\s*(\S+)")
        .Select(m => m.Groups[1].Value).LastOrDefault(t => t.Trim('*').Length > 0);
    return token is null ? null : (token, "glab auth status");
}

// tea keeps its logins with their tokens in tea/config.yml, in the configuration folder of its OS.
static (string Token, string Source)? TeaToken(string host)
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var paths = new[] { Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Path.Combine(home, ".config"),
            Path.Combine(home, "Library", "Application Support"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }
        .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "tea", "config.yml")).Distinct();
    foreach (var path in paths.Where(File.Exists))
    {
        // The logins are a list of flat maps (name, url, token, ...); a top-level key ends the list.
        var logins = new List<Dictionary<string, string>>();
        Dictionary<string, string>? login = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '-') login = null;
                var entry = Regex.Match(line, @"^\s*(-\s+)?(\w+):\s*(.*?)\s*$");
                if (!entry.Success) continue;
                if (entry.Groups[1].Success) logins.Add(login = []);
                if (login is not null) login[entry.Groups[2].Value] = entry.Groups[3].Value.Trim('"', '\'');
            }
        }
        catch (IOException) { continue; }
        catch (UnauthorizedAccessException) { continue; }
        var match = logins.FirstOrDefault(l => l.TryGetValue("url", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) && l.TryGetValue("token", out var token) && Clean(token) is not null);
        if (match is not null)
            return (Clean(match["token"])!, $"tea login {(match.TryGetValue("name", out var name) ? name : host)}");
    }
    return null;
}

// What a CLI wrote to stdout and stderr; null when it is not installed, fails (e.g. is not logged in) or hangs.
static (string Output, string Error)? Cli(string program, params string[] arguments)
{
    var start = new ProcessStartInfo(program)
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    try
    {
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            process.Kill(true);
            return null;
        }
        return process.ExitCode == 0 ? (output.Result, error.Result) : null;
    }
    catch (Win32Exception) { return null; }
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("godot-active-addon-exporter/1.0");

// Sends a request with its host's token: GitHub's to its API, GitLab's and Codeberg's to their /api/ paths, none to other
// hosts (the README is public). A GitLab or Codeberg token the host rejects is dropped with a warning and the request
// sent again without it, since both hosts are read without a token too.
async Task<HttpResponseMessage> SendAsync(Uri uri, object? body)
{
    while (true)
    {
        var github = uri.Host == "api.github.com";
        (string Token, string Source)? login = (github || uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)) &&
            tokens.TryGetValue(uri.Host, out var found) ? found : null;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (github)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (login is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Value.Token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Unauthorized || login is null || github)
            return response;
        response.Dispose();
        if (tokens.TryRemove(uri.Host, out _))
            Console.Error.WriteLine($"{uri.Host} rejected the token from {login.Value.Source} (401); reading it without a token.");
    }
}

async Task<string> RequestAsync(string url, object? body = null)
{
    var uri = new Uri(url);
    for (var attempt = 0; ; attempt++)
    {
        using var response = await SendAsync(uri, body);
        var content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return content;
        if (response.StatusCode == HttpStatusCode.Unauthorized && uri.Host == "api.github.com")
            throw new UnauthorizedAccessException($"GitHub rejected the token from {tokens["api.github.com"].Source} (401 Bad " +
                "credentials): it is mistyped, expired, revoked, or a fine-grained token still awaiting approval. Check it with " +
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

// The highest version among the tag names, as it is written (e.g. "v1.2"), or null without one.
string? Newest(IEnumerable<string?> tags) => tags
    .Select(t => (Tag: t, Match: versionTag.Match(t ?? "")))
    .Where(t => t.Match.Success)
    .OrderByDescending(t => t.Match, Comparer<Match>.Create(CompareVersions)).Select(t => t.Tag).FirstOrDefault();
static long Number(Group group) => group.Success && long.TryParse(group.Value, out var value) ? value : 0;
// Orders two versionTag matches like the updater's SemVer.CompareTo: by their numbers, then a prerelease before the same
// numbers without one, and prereleases by their dot-separated parts (numbers before text).
static int CompareVersions(Match left, Match right)
{
    for (var i = 1; i <= 4; i++)
    {
        var result = Number(left.Groups[i]).CompareTo(Number(right.Groups[i]));
        if (result != 0) return result;
    }
    if (!left.Groups[5].Success || !right.Groups[5].Success)
        return left.Groups[5].Success == right.Groups[5].Success ? 0 : left.Groups[5].Success ? -1 : 1;
    var leftParts = left.Groups[5].Value.Split('.');
    var rightParts = right.Groups[5].Value.Split('.');
    for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
    {
        var leftNumeric = BigInteger.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
        var rightNumeric = BigInteger.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
        var result = leftNumeric && rightNumeric ? l.CompareTo(r)
            : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(leftParts[i], rightParts[i]);
        if (result != 0) return result;
    }
    return leftParts.Length.CompareTo(rightParts.Length);
}
// The ZIP assets, as the updater recognizes them: by name or by the URL's path.
static ReleaseAsset[] Zips(IEnumerable<ReleaseAsset> assets) => assets.Where(a => a.Url.Length > 0 &&
    (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
     Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).ToArray();
static string? Utc(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

// Repositories that have neither a release nor a version tag, on any host.
var unversioned = new ConcurrentBag<string>();

// An add-on outside GitHub: an extra one, or one that GitLab's or Codeberg's search found. GitLab's API (gitlab.com or
// self-hosted) and Forgejo's (Codeberg or self-hosted, also Gitea) give its stars, activity and releases; any other host
// only its version tags, read with git. Null when it has no release or version tag.
async Task<Addon?> OtherAddon(string repository, string? pluginSlug, bool extra)
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
    var forgejo = false;
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
        else if (new Uri(web).AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 } owner, { Length: > 0 } path])
        {
            var forgejoApi = $"https://{uri!.Authority}/api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(path)}";
            using var forgejoInfo = await TryJson(forgejoApi);
            if (forgejoInfo?.RootElement is { ValueKind: JsonValueKind.Object } repo && repo.TryGetProperty("stars_count", out var count))
            {
                forgejo = true;
                if (count.TryGetInt32(out var starCount)) stars = starCount;
                if (repo.TryGetProperty("updated_at", out var updated) && updated.TryGetDateTimeOffset(out var updatedAt)) activity = updatedAt;
                if (repo.TryGetProperty("name", out var title) && title.GetString() is { Length: > 0 } text) name = text;
                // Drafts are not published; the updater skips them too. Forgejo returns at most 50 releases per page.
                using var releases = await TryJson(forgejoApi + "/releases?limit=50");
                if (releases?.RootElement is { ValueKind: JsonValueKind.Array } list)
                {
                    var published = list.EnumerateArray()
                        .Where(r => !(r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)).ToArray();
                    release = Newest(published.Select(r => r.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null));
                    assets = Zips(published.Where(r => r.TryGetProperty("tag_name", out var tagName) && tagName.GetString() == release)
                        .SelectMany(r => r.TryGetProperty("assets", out var all) && all.ValueKind == JsonValueKind.Array
                            ? all.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .Select(a => new ReleaseAsset(a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            a.TryGetProperty("browser_download_url", out var download) ? download.GetString() ?? "" : "")));
                }
            }
        }
    }
    var tag = release is null ? Newest(await RemoteTags(git)) : null;
    if (release is null && tag is null) return null;
    // The updater reads the release and tag pages of GitLab and Forgejo; another host by its .git URL, whose tags are the
    // versions.
    if (!gitlab && !forgejo && !git.EndsWith(".git", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Extra add-on {repository}: the updater reads git repositories by a URL ending in .git; give that URL.");
        return null;
    }
    var updateUrl = gitlab ? web + (release is not null ? "/-/releases" : "/-/tags")
        : forgejo ? web + (release is not null ? "/releases" : "/tags") : git;
    return new Addon(name, stars, Utc(activity), updateUrl, release is not null ? "releases" : "tags", (release ?? tag)!,
        web, web, web, git, extra, pluginSlug, release is null ? null : assets ?? [], !extra);
}

// Godot projects on gitlab.com with enough stars and recent activity, found by its topics and by its search of names and
// descriptions. GitLab returns them by stars, so reading stops below the minimum. Without a token GitLab does not say
// which projects are forks, but it leaves out archived ones. With one it would also return the user's private and internal
// projects, so only public ones are asked for.
async Task<string[]> GitLabSearch()
{
    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var query in new[] { "topic=godot", "topic=Godot%20Engine", "topic=godotengine", "topic=godot-engine", "search=godot" })
    {
        for (var page = 1; ; page++)
        {
            using var json = JsonDocument.Parse(await RequestAsync(
                $"https://gitlab.com/api/v4/projects?{query}&archived=false&visibility=public&order_by=star_count&sort=desc&per_page=100&page={page}"));
            var projects = json.RootElement.EnumerateArray().ToArray();
            foreach (var project in projects)
            {
                if (project.TryGetProperty("forked_from_project", out var fork) && fork.ValueKind == JsonValueKind.Object) continue;
                if (Searched(project, "star_count", "last_activity_at", "topics", "path") && project.GetProperty("web_url").GetString() is { } url)
                    found.Add(url.TrimEnd('/'));
            }
            if (projects.Length < 100 || projects[^1].GetProperty("star_count").GetInt32() < minStarsElsewhere) break;
        }
    }
    return found.ToArray();
}

// Godot repositories on Codeberg, found like GitLabSearch's: by their topics and by the search of names and descriptions.
// With a token the search also finds the user's private repositories, which are left out like forks.
async Task<string[]> CodebergSearch()
{
    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var queries = new[] { "godot", "godot-engine", "godotengine", "godot4", "godot-plugin", "godot-addon", "gdscript", "gdextension" }
        .Select(topic => $"q={topic}&topic=true").Append("q=godot&includeDesc=true");
    foreach (var query in queries)
    {
        for (var page = 1; ; page++)
        {
            using var json = JsonDocument.Parse(await RequestAsync(
                $"https://codeberg.org/api/v1/repos/search?{query}&sort=stars&order=desc&limit=50&page={page}"));
            var repositories = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
            foreach (var repository in repositories)
            {
                if (new[] { "archived", "fork", "mirror", "template", "private", "internal" }.Any(flag =>
                        repository.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True)) continue;
                if (Searched(repository, "stars_count", "updated_at", "topics", "name") && repository.GetProperty("html_url").GetString() is { } url)
                    found.Add(url.TrimEnd('/'));
            }
            if (repositories.Length < 50 || repositories[^1].GetProperty("stars_count").GetInt32() < minStarsElsewhere) break;
        }
    }
    return found.ToArray();
}

// Whether a repository a search found can be an active add-on: enough stars, recent activity, not a game and not the
// engine. Repositories named "godot" are the engine and its forks.
bool Searched(JsonElement repository, string starsProperty, string activityProperty, string topicsProperty, string nameProperty)
{
    var topics = repository.TryGetProperty(topicsProperty, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(t => t.GetString() ?? "").ToArray() : [];
    return repository.TryGetProperty(starsProperty, out var stars) && stars.GetInt32() >= minStarsElsewhere &&
        repository.TryGetProperty(activityProperty, out var activity) && activity.TryGetDateTimeOffset(out var last) && last >= cutoff &&
        !(topics.Any(gameTopics.Contains) && !topics.Any(addonTopics.Contains)) &&
        !string.Equals(repository.TryGetProperty(nameProperty, out var name) ? name.GetString() : null, "godot", StringComparison.OrdinalIgnoreCase);
}

// The add-ons among repositories outside GitHub, read a few at a time.
async Task<Addon[]> OtherAddons(IEnumerable<(string Repository, string? PluginSlug)> repositories, bool extra)
{
    var addons = new ConcurrentBag<Addon>();
    await Parallel.ForEachAsync(repositories, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (repository, _) =>
    {
        if (await OtherAddon(repository.Repository, repository.PluginSlug, extra) is { } addon) addons.Add(addon);
        else unversioned.Add(repository.Repository);
    });
    return addons.ToArray();
}

// The add-ons a search found on a host. Extra add-ons are left out here and read with the other extra add-ons, which
// keeps the slug extra_addons.txt gives them.
async Task<Addon[]> SearchedAddons(string host, Task<string[]> search)
{
    var extras = others.Select(o => Regex.Replace(o.Repository, @"\.git$", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var repositories = (await search).Where(r => !extras.Contains(r)).ToArray();
    var addons = await OtherAddons(repositories.Select(r => (r, (string?)null)), false);
    Console.Error.WriteLine($"Checked {repositories.Length} candidates on {host}, {addons.Length} with a release or version tag");
    return addons;
}

async Task<JsonDocument?> TryJson(string url)
{
    try
    {
        using var response = await SendAsync(new Uri(url), null);
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

// The hosts are read at the same time. GitHub's batches run one after another, as GitHub asks of API clients, next to
// GitLab's and Codeberg's searches and the extra add-ons on other hosts.
var found = await Task.WhenAll(GitHubAddons(), SearchedAddons("GitLab", GitLabSearch()), SearchedAddons("Codeberg", CodebergSearch()),
    OtherAddons(others, true));
var addons = found.SelectMany(hostAddons => hostAddons).ToArray();

// The candidates from GitHub's ranking and the extra add-ons on GitHub.
async Task<Addon[]> GitHubAddons()
{
    var results = new List<Addon>();
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
            fields.Append("releases(first: 30, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { tagName isDraft ");
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
            // A published release (whose ZIP the updater downloads), prereleases included, wins; otherwise the repository's version tags,
            // which the updater installs from git through the tag page URL. Without either there is nothing to update to.
            var release = Newest(Nodes(repo, "releases")
                .Where(r => !r.GetProperty("isDraft").GetBoolean())
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
        Console.Error.WriteLine($"Checked {Math.Min(offset + 20, values.Length)}/{values.Length} candidates on GitHub");
    }
    return results.ToArray();
}

// Several candidates can name one repository, e.g. an old name that GitHub redirects to the renamed one. The maximum
// applies to GitHub's ranking; the extra add-ons and the few found on GitLab and Codeberg are always listed.
var unique = addons.DistinctBy(x => x.RepositoryUrl, StringComparer.OrdinalIgnoreCase).ToArray();
static bool Ranked(Addon addon) => !addon.Extra && addon.RepositoryUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);
var output = unique.Where(Ranked).OrderByDescending(x => x.Stars).Take(maxItems).Concat(unique.Where(x => !Ranked(x)))
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
    Console.WriteLine($"Left out {unversioned.Count} add-ons without a release or version tag: {string.Join(", ", unversioned.Order())}");
Console.WriteLine("GitHub's candidates come from an older ranking; the result may omit recently published add-ons. " +
    "List those in extra_addons.txt.");

/// <param name="LastCommitAt">The last commit on the default branch, or GitLab's last activity or Forgejo's last update;
/// null when unknown.</param>
/// <param name="UpdateUrl">The releases page, or the tag page (a .git URL off GitHub, GitLab and Forgejo) without a
/// release.</param>
/// <param name="UpdateSource">"releases" or "tags": where <paramref name="LatestVersion"/> was found.</param>
/// <param name="LatestVersion">The tag name of the newest release or version tag, prereleases included.</param>
/// <param name="RepositoryUrl">The repository's web page, or its git URL when it has none.</param>
/// <param name="GitUrl">The URL to clone the repository from.</param>
/// <param name="Extra">Listed in extra_addons.txt, so it skips the star and activity filters and the maximum.</param>
/// <param name="Slug">The add-on's folder in res://addons as extra_addons.txt gives it; null to let the generator find it.</param>
/// <param name="ReleaseAssets">The ZIP assets of the latest release; null without a release.</param>
/// <param name="Searched">Found by GitLab's or Codeberg's search rather than in a curated ranking, so it may be a game or
/// project that bundles other add-ons; the generator then requires a plugin folder named like the repository.</param>
record Addon(string Name, int Stars, string? LastCommitAt, string UpdateUrl, string UpdateSource, string LatestVersion,
    string WebsiteUrl, string DocumentationUrl, string RepositoryUrl, string GitUrl, bool Extra, string? Slug = null,
    ReleaseAsset[]? ReleaseAssets = null, bool Searched = false);
record ReleaseAsset(string Name, string Url);
