#:property TargetFramework=net10.0
#:property PublishAot=false
// Run with: dotnet run build_godot_addons.cs
// Requires GITHUB_TOKEN (public-repository access) and .NET 10 SDK.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string source = "https://raw.githubusercontent.com/sci-comp/godot-stars/main/README.md";
const int minStars = 50;
const int maxItems = 500;
const int maxRetries = 3;
// Only add-ons with a commit on their default branch within the last year count as active.
var cutoff = DateTimeOffset.UtcNow.AddYears(-1);
// A version tag as ePlugin's updater reads it: an optional "v", then "1.2" or "1.2.3" with optional semantic
// prerelease/build suffixes (see SemVer.TryParse). Only versions without a prerelease are offered by default.
var versionTag = new Regex(@"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
// godot-stars categories that hold games, project templates or assets rather than add-ons.
var skippedCategories = new HashSet<string>(["Demos", "Shader", "Shaders", "Templates", "Projects", "Materials"],
    StringComparer.OrdinalIgnoreCase);

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set GITHUB_TOKEN to a GitHub personal access token.");
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

// One candidate per GitHub slug, with the title shown in the Godot Asset Library ranking.
var candidates = new Dictionary<string, (string Slug, string Name)>(StringComparer.OrdinalIgnoreCase);
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
    candidates[slug] = (slug, match.Groups[1].Value);
}

// Optional local seed of further add-ons, in the format of the output. Place it beside this file or in the working
// directory. A file-based app runs from a temporary build folder, so the script's own folder comes from AppContext.
var scriptDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string;
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
                ? name.GetString()! : slug);
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
        fields.Append("releases(first: 30, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { tagName isDraft isPrerelease } } ");
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
        if (!data.TryGetProperty($"r{i}", out var repo) || repo.ValueKind != JsonValueKind.Object) continue;
        if (repo.GetProperty("isArchived").GetBoolean() || repo.GetProperty("isDisabled").GetBoolean()
            || repo.GetProperty("isFork").GetBoolean()) continue;
        var stars = repo.GetProperty("stargazerCount").GetInt32();
        if (stars < minStars) continue;
        if (!repo.TryGetProperty("defaultBranchRef", out var branch) || branch.ValueKind != JsonValueKind.Object
            || !branch.TryGetProperty("target", out var target)
            || !target.TryGetProperty("committedDate", out var dateValue)) continue;
        // committedDate keeps the committer's time zone, so compare and store it in UTC.
        if (!DateTimeOffset.TryParse(dateValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var committed)
            || committed < cutoff) continue;
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
        results.Add(new Addon(batch[i].Name, stars, committed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            release is not null ? url + "/releases" : url + "/tags", release is not null ? "releases" : "tags", (release ?? tag)!,
            string.IsNullOrWhiteSpace(homepage) ? url : homepage, url + "#readme", url));
    }
    Console.Error.WriteLine($"Checked {Math.Min(offset + 20, values.Length)}/{values.Length} candidates");
}

var output = results.OrderByDescending(x => x.Stars).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
    .Take(maxItems).ToArray();
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
Console.WriteLine("Candidate discovery uses an older ranking; the result may omit recently published add-ons.");

/// <param name="UpdateUrl">The releases page, or the tag page when the add-on publishes no release.</param>
/// <param name="UpdateSource">"releases" or "tags": where <paramref name="LatestVersion"/> was found.</param>
/// <param name="LatestVersion">The tag name of the newest stable release or version tag.</param>
record Addon(string Name, int Stars, string LastCommitAt, string UpdateUrl, string UpdateSource, string LatestVersion,
    string WebsiteUrl, string DocumentationUrl, string GithubUrl);
