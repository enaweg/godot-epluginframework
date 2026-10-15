using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CiReports;

/// <summary>Covered and coverable lines of one framework file.</summary>
internal readonly record struct FileCoverage(int Covered, int Coverable);

/// <summary>A line coverage measurement: what coverage.json, and latest.json on the coverage branch, hold.</summary>
internal sealed record Measurement(string Commit, string Date, int Covered, int Coverable,
    SortedDictionary<string, FileCoverage> Files);

/// <summary>Links the pull request comment and the coverage branch point to; empty values are left out.</summary>
internal sealed record Links(string Label = "This run", string? Html = null, string? Run = null, string? Blob = null,
    string? Repository = null);

/// <summary>
/// Summarizes framework line coverage for pull requests and keeps its history on the coverage branch.
/// summary: merges the Cobertura reports of the test runs, compares them with the latest main measurement and the pull
///          request's changed lines, and writes summary.md (PR comment and run summary) and coverage.json.
/// history: records a main measurement (coverage.json) in a checkout of the coverage branch: latest.json, history.csv,
///          badge.svg and a README.md with a trend chart that GitHub renders.
/// </summary>
internal static partial class CoverageReport
{
    internal const string Owned = "src/eplugin-framework/addons/ePlugin/";
    internal const string Marker = "<!-- eplugin-coverage -->";
    private const string HistoryHeader = "date,commit,covered_lines,coverable_lines,line_coverage";
    private const int ChartPoints = 30;
    private const int TableRows = 30;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^@@ -\S+ \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    internal static string RepositoryPath(string filename, string root)
    {
        var value = filename.Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return value[prefix.Length..];
        var index = value.IndexOf("/" + Owned, StringComparison.Ordinal);
        return index >= 0 ? value[(index + 1)..] : value;
    }

    /// <summary>Line hits per owned file, merged over all reports (a line counts as covered when any run covered it).</summary>
    internal static Dictionary<string, Dictionary<int, bool>> ReadReports(IEnumerable<string> paths, string root)
    {
        var files = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
        foreach (var report in paths)
        {
            foreach (var type in XDocument.Load(report).Descendants("class"))
            {
                var path = RepositoryPath((string?)type.Attribute("filename") ?? "", root);
                if (!path.StartsWith(Owned, StringComparison.Ordinal))
                    continue;
                if (!files.TryGetValue(path, out var lines))
                    files[path] = lines = [];
                foreach (var line in type.Descendants("line"))
                {
                    var number = int.Parse((string)line.Attribute("number")!, Invariant);
                    var hit = int.Parse((string?)line.Attribute("hits") ?? "0", Invariant) > 0;
                    lines[number] = lines.GetValueOrDefault(number) || hit;
                }
            }
        }
        return files;
    }

    internal static Measurement Measure(IReadOnlyDictionary<string, Dictionary<int, bool>> files, string commit, string date)
    {
        var counts = new SortedDictionary<string, FileCoverage>(StringComparer.Ordinal);
        foreach (var (path, lines) in files)
        {
            if (lines.Count > 0)
                counts[path] = new FileCoverage(lines.Values.Count(hit => hit), lines.Count);
        }
        return new Measurement(commit, date, counts.Values.Sum(c => c.Covered), counts.Values.Sum(c => c.Coverable), counts);
    }

    internal static double Percent(int covered, int coverable) => coverable != 0 ? 100.0 * covered / coverable : 100.0;

    private static double Percent(FileCoverage coverage) => Percent(coverage.Covered, coverage.Coverable);

    /// <summary>Added or modified lines per file of a `git diff --unified=0`.</summary>
    internal static Dictionary<string, HashSet<int>> ChangedLines(string diff)
    {
        var changes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? path = null;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var target = line[4..].Trim();
                path = target.StartsWith("b/", StringComparison.Ordinal) ? target[2..] : null;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal) && path is not null)
            {
                var match = HunkHeader().Match(line);
                if (!match.Success)
                    continue;
                var start = int.Parse(match.Groups[1].Value, Invariant);
                var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, Invariant) : 1;
                if (!changes.TryGetValue(path, out var numbers))
                    changes[path] = numbers = [];
                numbers.UnionWith(Enumerable.Range(start, count));
            }
        }
        return changes;
    }

    internal static List<string> Ranges(IEnumerable<int> numbers)
    {
        var parts = new List<string>();
        var sorted = numbers.Order().ToList();
        for (var index = 0; index < sorted.Count;)
        {
            var start = sorted[index];
            var end = start;
            while (++index < sorted.Count && sorted[index] == end + 1)
                end = sorted[index];
            parts.Add(start == end ? Number(start) : $"{Number(start)}-{Number(end)}");
        }
        return parts;
    }

    private static string Short(string path) => path.StartsWith(Owned, StringComparison.Ordinal) ? path[Owned.Length..] : path;

    private static string ShortCommit(string? commit) => (commit ?? "").Length > 7 ? commit![..7] : commit ?? "";

    private static string Count(int number, string noun, string? plural = null) =>
        $"{Number(number)} {(number == 1 ? noun : plural ?? noun + "s")}";

    private static string Number(int value) => value.ToString(Invariant);

    private static string Fixed(double value, int decimals) => value.ToString("F" + decimals, Invariant);

    private static string Signed(double value) => (double.IsNegative(value) ? "" : "+") + Fixed(value, 2) + " pp";

    internal static string Summary(Measurement current, Measurement? baseline,
        IReadOnlyDictionary<string, Dictionary<int, bool>> files, IReadOnlyDictionary<string, HashSet<int>>? changes,
        Links links)
    {
        var total = Percent(current.Covered, current.Coverable);
        var lines = new List<string> { Marker, "### Code coverage", "" };
        lines.AddRange(["| | Line coverage | Covered lines |", "|:--|--:|--:|"]);
        lines.Add($"| {links.Label} | **{Fixed(total, 2)}%** | {Number(current.Covered)} of {Number(current.Coverable)} |");
        if (baseline is not null)
        {
            var baseTotal = Percent(baseline.Covered, baseline.Coverable);
            lines.Add($"| `main` ({ShortCommit(baseline.Commit)}) | {Fixed(baseTotal, 2)}% | " +
                $"{Number(baseline.Covered)} of {Number(baseline.Coverable)} |");
            lines.Add($"| Change | **{Signed(total - baseTotal)}** | |");
        }
        lines.Add("");
        if (baseline is null)
            lines.AddRange(["No `main` measurement on the `coverage` branch yet, so there is nothing to compare with.", ""]);

        if (changes is not null)
            lines.AddRange(ChangedLinesSection(files, changes, links));
        if (baseline is not null)
            lines.AddRange(ChangedFilesSection(current, baseline));

        lines.AddRange(FileTable(current, $"All files ({Number(current.Files.Count)})"));
        var footer = "Line coverage of `src/eplugin-framework/addons/ePlugin` by the gdUnit4 tests of all SDK runs.";
        if (!string.IsNullOrEmpty(links.Html))
            footer += $" [Browse uncovered lines]({links.Html}) (HTML report).";
        if (!string.IsNullOrEmpty(links.Run))
            footer += $" [Workflow run]({links.Run}).";
        lines.Add(footer);
        return string.Join("\n", lines) + "\n";
    }

    private static List<string> ChangedLinesSection(IReadOnlyDictionary<string, Dictionary<int, bool>> files,
        IReadOnlyDictionary<string, HashSet<int>> changes, Links links)
    {
        int covered = 0, coverable = 0;
        var uncovered = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var (path, numbers) in changes)
        {
            if (!files.TryGetValue(path, out var hits))
                continue;
            foreach (var number in numbers)
            {
                if (!hits.TryGetValue(number, out var hit))
                    continue;
                coverable++;
                if (hit)
                {
                    covered++;
                    continue;
                }
                if (!uncovered.TryGetValue(path, out var list))
                    uncovered[path] = list = [];
                list.Add(number);
            }
        }
        var lines = new List<string>
        {
            coverable > 0
                ? $"**Changed lines:** {Number(covered)} of {Number(coverable)} coverable lines covered " +
                  $"({Fixed(Percent(covered, coverable), 1)}%)."
                : "**Changed lines:** this pull request changes no coverable framework lines.",
            "",
        };
        if (uncovered.Count == 0)
            return lines;
        lines.AddRange([$"<details><summary>Uncovered changed lines ({Count(uncovered.Count, "file")})</summary>", ""]);
        lines.AddRange(["| File | Lines |", "|:--|:--|"]);
        foreach (var (path, numbers) in uncovered)
        {
            var parts = Ranges(numbers);
            var anchor = "L" + parts[0].Replace("-", "-L", StringComparison.Ordinal);
            var name = string.IsNullOrEmpty(links.Blob)
                ? $"`{Short(path)}`"
                : $"[`{Short(path)}`]({links.Blob}/{path}#{anchor})";
            lines.Add($"| {name} | {string.Join(", ", parts)} |");
        }
        lines.AddRange(["", "</details>", ""]);
        return lines;
    }

    private static List<string> ChangedFilesSection(Measurement current, Measurement baseline)
    {
        var differences = new List<(string Path, double? Before, double? Now, double Change)>();
        foreach (var path in current.Files.Keys.Union(baseline.Files.Keys).Order(StringComparer.Ordinal))
        {
            FileCoverage? now = current.Files.TryGetValue(path, out var a) ? a : null;
            FileCoverage? before = baseline.Files.TryGetValue(path, out var b) ? b : null;
            if (now == before)
                continue;
            double? nowValue = now is { } n ? Percent(n) : null;
            double? beforeValue = before is { } p ? Percent(p) : null;
            var change = (nowValue ?? 0.0) - (beforeValue ?? 0.0);
            if (now is not null && before is not null && Math.Abs(change) < 0.005)
                continue;
            differences.Add((path, beforeValue, nowValue, change));
        }
        if (differences.Count == 0)
            return [];
        var lines = new List<string>
        {
            $"<details><summary>Files with changed coverage ({Number(differences.Count)})</summary>", "",
            "| File | `main` | Now | Change |", "|:--|--:|--:|--:|",
        };
        foreach (var (path, before, now, change) in differences.OrderBy(d => d.Change).Take(TableRows))
        {
            var beforeText = before is { } bv ? Fixed(bv, 1) + "%" : "new";
            var nowText = now is { } nv ? Fixed(nv, 1) + "%" : "removed";
            var changeText = before is not null && now is not null ? Signed(change) : "";
            lines.Add($"| `{Short(path)}` | {beforeText} | {nowText} | {changeText} |");
        }
        if (differences.Count > TableRows)
            lines.Add($"| … {Number(differences.Count - TableRows)} more | | | |");
        lines.AddRange(["", "</details>", ""]);
        return lines;
    }

    private static List<string> FileTable(Measurement measurement, string title)
    {
        var lines = new List<string>
        {
            $"<details><summary>{title}</summary>", "", "| File | Line coverage | Covered lines |", "|:--|--:|--:|",
        };
        foreach (var (path, coverage) in measurement.Files.OrderBy(file => Percent(file.Value)))
        {
            lines.Add($"| `{Short(path)}` | {Fixed(Percent(coverage), 1)}% | " +
                $"{Number(coverage.Covered)} of {Number(coverage.Coverable)} |");
        }
        lines.AddRange(["", "</details>", ""]);
        return lines;
    }

    internal static string Badge(double value)
    {
        (double Limit, string Color)[] colors =
            [(90, "#4c1"), (80, "#97ca00"), (70, "#a4a61d"), (60, "#dfb317"), (50, "#fe7d37"), (0, "#e05d44")];
        var color = colors.First(c => value >= c.Limit).Color;
        var text = Fixed(value, 1) + "%";
        return
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"108\" height=\"20\" role=\"img\" " +
            $"aria-label=\"coverage: {text}\"><title>coverage: {text}</title>" +
            "<linearGradient id=\"s\" x2=\"0\" y2=\"100%\"><stop offset=\"0\" stop-color=\"#bbb\" stop-opacity=\".1\"/>" +
            "<stop offset=\"1\" stop-opacity=\".1\"/></linearGradient>" +
            "<clipPath id=\"r\"><rect width=\"108\" height=\"20\" rx=\"3\" fill=\"#fff\"/></clipPath>" +
            "<g clip-path=\"url(#r)\"><rect width=\"61\" height=\"20\" fill=\"#555\"/><rect x=\"61\" width=\"47\" height=\"20\" " +
            $"fill=\"{color}\"/><rect width=\"108\" height=\"20\" fill=\"url(#s)\"/></g>" +
            "<g fill=\"#fff\" text-anchor=\"middle\" font-family=\"Verdana,Geneva,DejaVu Sans,sans-serif\" font-size=\"11\">" +
            "<text x=\"30.5\" y=\"14\">coverage</text>" +
            $"<text x=\"84.5\" y=\"14\">{text}</text></g></svg>\n";
    }

    /// <summary>The rows of history.csv, each by column name. Its values never contain commas or quotes.</summary>
    internal static List<Dictionary<string, string>> ReadHistory(string path)
    {
        if (!File.Exists(path))
            return [];
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0)
            return [];
        var header = lines[0].Split(',');
        return lines.Skip(1)
            .Select(line => header.Zip(line.Split(',')).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal))
            .ToList();
    }

    private static void WriteHistory(string path, IEnumerable<Dictionary<string, string>> rows)
    {
        var fields = HistoryHeader.Split(',');
        var text = new StringBuilder(HistoryHeader).Append('\n');
        foreach (var row in rows)
            text.AppendJoin(',', fields.Select(field => row.GetValueOrDefault(field, ""))).Append('\n');
        File.WriteAllText(path, text.ToString());
    }

    internal static string HistoryReadme(IReadOnlyList<Dictionary<string, string>> rows, Measurement latest, Links links)
    {
        var total = Percent(latest.Covered, latest.Coverable);
        var commitUrl = string.IsNullOrEmpty(links.Repository) ? null : $"{links.Repository}/commit/";
        string CommitLink(string commit) => commitUrl is null ? ShortCommit(commit) : $"[{ShortCommit(commit)}]({commitUrl}{commit})";

        var lines = new List<string>
        {
            "# Code coverage history",
            "",
            "Line coverage of `src/eplugin-framework/addons/ePlugin` on `main`, recorded by the CI-PR workflow after " +
            "every push. This branch is generated; do not edit it.",
            "",
            "![Line coverage](badge.svg)",
            "",
            $"**{Fixed(total, 2)}%** ({Number(latest.Covered)} of {Number(latest.Coverable)} lines) at " +
            $"{CommitLink(latest.Commit)}, {Date(latest.Date)}.",
            "",
        };
        var recent = rows.Skip(Math.Max(0, rows.Count - ChartPoints)).ToList();
        var values = recent.Select(LineCoverage).ToList();
        var low = Math.Max(0, (int)Math.Floor(values.Min()) - 2);
        var high = Math.Min(100, (int)Math.Ceiling(values.Max()) + 2);
        var labels = string.Join(", ", recent.Select(row => $"\"{ShortCommit(row["commit"])}\""));
        lines.AddRange([
            "```mermaid",
            "xychart-beta",
            $"    title \"Line coverage on main (last {Count(recent.Count, "push", "pushes")})\"",
            $"    x-axis [{labels}]",
            $"    y-axis \"Line coverage (%)\" {Number(low)} --> {Number(high)}",
            $"    line [{string.Join(", ", values.Select(value => Fixed(value, 2)))}]",
            "```",
            "",
            "| Date | Commit | Line coverage | Change |",
            "|:--|:--|--:|--:|",
        ]);
        var newest = rows.Reverse().ToList();
        for (var index = 0; index < Math.Min(newest.Count, TableRows); index++)
        {
            var row = newest[index];
            var value = LineCoverage(row);
            var change = index + 1 < newest.Count ? Signed(value - LineCoverage(newest[index + 1])) : "";
            lines.Add($"| {Date(row["date"])} | {CommitLink(row["commit"])} | {Fixed(value, 2)}% | {change} |");
        }
        lines.AddRange(["", "All measurements: [history.csv](history.csv).", ""]);
        lines.AddRange(FileTable(latest, $"Files at {ShortCommit(latest.Commit)} ({Number(latest.Files.Count)})"));
        return string.Join("\n", lines);
    }

    private static double LineCoverage(Dictionary<string, string> row) => double.Parse(row["line_coverage"], Invariant);

    private static string Date(string date) => date.Length > 10 ? date[..10] : date;

    internal static string ToJson(Measurement measurement)
    {
        var files = new JsonObject();
        foreach (var (path, coverage) in measurement.Files)
            files[path] = new JsonArray(coverage.Covered, coverage.Coverable);
        var json = new JsonObject
        {
            ["commit"] = measurement.Commit,
            ["date"] = measurement.Date,
            ["covered"] = measurement.Covered,
            ["coverable"] = measurement.Coverable,
            ["files"] = files,
        };
        return json.ToJsonString(JsonOptions) + "\n";
    }

    internal static Measurement FromJson(string text)
    {
        var json = JsonNode.Parse(text) as JsonObject ?? throw new ReportException("error: a measurement is not a JSON object");
        var files = new SortedDictionary<string, FileCoverage>(StringComparer.Ordinal);
        foreach (var (path, value) in json["files"] as JsonObject ?? [])
            files[path] = new FileCoverage(value![0]!.GetValue<int>(), value[1]!.GetValue<int>());
        return new Measurement((string?)json["commit"] ?? "", (string?)json["date"] ?? "", (int)json["covered"]!,
            (int)json["coverable"]!, files);
    }

    internal static int RunSummary(Arguments arguments, TextWriter output)
    {
        var root = Path.GetFullPath(arguments.Optional("root") ?? ".");
        var files = ReadReports(arguments.All("report"), root);
        if (files.Count == 0)
            throw new ReportException("error: the coverage reports contain no framework files");
        var commit = arguments.Optional("commit") ?? "";
        var current = Measure(files, commit, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant));
        Measurement? baseline = null;
        if (arguments.Optional("baseline") is { } baselinePath && new FileInfo(baselinePath) is { Exists: true, Length: > 0 })
            baseline = FromJson(File.ReadAllText(baselinePath, Encoding.UTF8));
        Dictionary<string, HashSet<int>>? changes = null;
        if (arguments.Optional("diff") is { } diff)
            changes = ChangedLines(File.ReadAllText(diff, Encoding.UTF8));
        var repository = arguments.Optional("repository-url");
        var links = new Links(arguments.Optional("label") ?? "This run", arguments.Optional("html-url"), arguments.Optional("run-url"),
            !string.IsNullOrEmpty(repository) && commit.Length > 0 ? $"{repository}/blob/{commit}" : null);
        var directory = Directory.CreateDirectory(arguments.Required("output-dir")).FullName;
        File.WriteAllText(Path.Combine(directory, "coverage.json"), ToJson(current));
        File.WriteAllText(Path.Combine(directory, "summary.md"), Summary(current, baseline, files, changes, links));
        output.WriteLine($"Line coverage: {Fixed(Percent(current.Covered, current.Coverable), 2)}% " +
            $"({Number(current.Covered)} of {Number(current.Coverable)} lines)");
        return 0;
    }

    internal static int RunHistory(Arguments arguments)
    {
        var latest = FromJson(File.ReadAllText(arguments.Required("coverage"), Encoding.UTF8));
        var branch = arguments.Required("branch-dir");
        var history = Path.Combine(branch, "history.csv");
        var rows = ReadHistory(history).Where(row => row.GetValueOrDefault("commit") != latest.Commit).ToList();
        var value = Percent(latest.Covered, latest.Coverable);
        rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["date"] = latest.Date,
            ["commit"] = latest.Commit,
            ["covered_lines"] = Number(latest.Covered),
            ["coverable_lines"] = Number(latest.Coverable),
            ["line_coverage"] = Fixed(value, 2),
        });
        WriteHistory(history, rows);
        File.WriteAllText(Path.Combine(branch, "latest.json"), ToJson(latest));
        File.WriteAllText(Path.Combine(branch, "badge.svg"), Badge(value));
        File.WriteAllText(Path.Combine(branch, "README.md"),
            HistoryReadme(rows, latest, new Links(Repository: arguments.Optional("repository-url"))));
        return 0;
    }
}
