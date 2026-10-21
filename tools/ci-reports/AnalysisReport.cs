using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CiReports;

/// <summary>Findings per level of the analyzed projects (null for a report that was not produced) and why others were left out.</summary>
internal sealed class AnalysisMetrics
{
    internal static readonly string[] Levels = ["error", "warning", "note"];

    internal Dictionary<string, Dictionary<string, int>?> Projects { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Excluded { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Rules { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Totals { get; } = new(StringComparer.Ordinal);
    internal bool ReportsComplete { get; set; }

    internal JsonObject ToJson()
    {
        static JsonObject Counts(Dictionary<string, int> counts) =>
            new(counts.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

        return new JsonObject
        {
            ["projects"] = new JsonObject(Projects.Select(pair =>
                KeyValuePair.Create(pair.Key, pair.Value is null ? null : (JsonNode)Counts(pair.Value)))),
            ["excluded"] = Counts(Excluded),
            ["rules"] = Counts(Rules),
            ["totals"] = Counts(Totals),
            ["reports_complete"] = ReportsComplete,
        };
    }
}

/// <summary>Publishes owned-code Roslyn findings as GitHub summaries, annotations and SARIF.</summary>
internal static class AnalysisReport
{
    internal static readonly string[] Projects = ["EPluginFramework", "ActivationProgress", "ActivationProgress.Tests"];

    private static readonly string[] OwnedPaths =
    [
        "src/eplugin-framework/addons/ePlugin/",
        "src/eplugin-framework/tests/",
        "src/activation-progress/",
        "src/activation-progress.Tests/",
    ];

    private static readonly (string Variable, string Label)[] Stages =
    [
        ("RESTORE_FRAMEWORK", "Framework dependency audit"),
        ("RESTORE_HELPER", "Helper dependency audit"),
        ("BUILD_FRAMEWORK", "Framework analysis build"),
        ("BUILD_HELPER", "Helper analysis build"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The path of a local file URI or path relative to <paramref name="root"/>, or null outside of it.</summary>
    internal static string? RepositoryPath(string uri, string root)
    {
        string value;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !Path.IsPathRooted(uri))
        {
            if (parsed.Scheme != Uri.UriSchemeFile || !string.IsNullOrEmpty(parsed.Host))
                return null;
            value = parsed.LocalPath;
        }
        else
        {
            value = Uri.UnescapeDataString(uri);
        }
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            return null;
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal static string Markdown(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    internal static string CommandEscape(string value, bool propertyValue = false)
    {
        value = value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
        return propertyValue
            ? value.Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal)
            : value;
    }

    private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    internal static (AnalysisMetrics Metrics, JsonObject Sarif) Collect(string reports, string root)
    {
        var metrics = new AnalysisMetrics();
        var results = new JsonArray();
        var rules = new JsonArray();
        var ruleIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in Projects)
        {
            var report = Path.Combine(reports, $"{project}.sarif");
            if (!File.Exists(report))
            {
                metrics.Projects[project] = null;
                continue;
            }
            var counts = AnalysisMetrics.Levels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);
            var data = JsonNode.Parse(File.ReadAllText(report, Encoding.UTF8))!;
            foreach (var run in data["runs"]!.AsArray())
            {
                var descriptors = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                foreach (var rule in run!["tool"]!["driver"]!["rules"]?.AsArray() ?? [])
                    descriptors[(string)rule!["id"]!] = rule;
                foreach (var original in run["results"]?.AsArray() ?? [])
                {
                    if ((original!["suppressions"]?.AsArray() ?? []).Any(s => (string?)s!["status"] != "rejected"))
                    {
                        Increment(metrics.Excluded, "suppressed");
                        continue;
                    }
                    var locations = original["locations"]?.AsArray() ?? [];
                    var physical = locations.Count > 0 ? locations[0]!["physicalLocation"] : null;
                    var path = RepositoryPath((string?)physical?["artifactLocation"]?["uri"] ?? "", root);
                    if (path is null || !OwnedPaths.Any(owned => path.StartsWith(owned, StringComparison.Ordinal)))
                    {
                        Increment(metrics.Excluded, "outside owned source");
                        continue;
                    }
                    if (path.Split('/').Any(part => part is "obj" or "bin" or ".godot")
                        || path.EndsWith(".generated.cs", StringComparison.Ordinal) || path.EndsWith(".g.cs", StringComparison.Ordinal))
                    {
                        Increment(metrics.Excluded, "generated");
                        continue;
                    }
                    var ruleId = (string)original["ruleId"]!;
                    var descriptor = descriptors.GetValueOrDefault(ruleId) ?? new JsonObject { ["id"] = ruleId };
                    var level = (string?)original["level"]
                        ?? (string?)descriptor["defaultConfiguration"]?["level"] ?? "warning";
                    if (level == "none")
                        continue;
                    Increment(counts, level);
                    Increment(metrics.Rules, ruleId);
                    if (!ruleIndexes.ContainsKey(ruleId))
                    {
                        ruleIndexes[ruleId] = rules.Count;
                        rules.Add(descriptor.DeepClone());
                    }
                    // Keep location and rule metadata while removing absolute runner paths and fix payloads.
                    var location = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = path } };
                    if (physical?["region"] is { } region)
                        location["region"] = region.DeepClone();
                    results.Add(new JsonObject
                    {
                        ["ruleId"] = ruleId,
                        ["ruleIndex"] = ruleIndexes[ruleId],
                        ["level"] = level,
                        ["message"] = original["message"]!.DeepClone(),
                        ["locations"] = new JsonArray(new JsonObject { ["physicalLocation"] = location }),
                        ["properties"] = new JsonObject { ["project"] = project },
                    });
                }
            }
            metrics.Projects[project] = counts;
        }
        foreach (var level in AnalysisMetrics.Levels)
            metrics.Totals[level] = metrics.Projects.Values.Sum(counts => counts?.GetValueOrDefault(level) ?? 0);
        metrics.ReportsComplete = metrics.Projects.Values.All(counts => counts is not null);
        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = ".NET SDK analyzers", ["rules"] = rules } },
                ["automationDetails"] = new JsonObject { ["id"] = "dotnet/" },
                ["columnKind"] = "utf16CodeUnits",
                ["results"] = results,
            }),
        };
        return (metrics, sarif);
    }

    internal static string Summary(AnalysisMetrics metrics, Func<string, string?> environment)
    {
        var lines = new List<string> { "### Static analysis", "" };
        if (environment("ANALYSIS_RUN_URL") is { Length: > 0 } runUrl)
            lines.AddRange([$"[Workflow run and downloadable reports]({runUrl})", ""]);
        lines.AddRange(["| Check | Status |", "| --- | --- |"]);
        foreach (var (variable, label) in Stages)
            lines.Add($"| {label} | {Markdown(environment(variable) ?? "not run")} |");
        lines.AddRange(["", "| Project | Errors | Warnings | Notes |", "| --- | ---: | ---: | ---: |"]);
        foreach (var (project, counts) in metrics.Projects)
        {
            var values = counts is null
                ? "not produced | — | —"
                : string.Join(" | ", AnalysisMetrics.Levels.Select(level => counts.GetValueOrDefault(level)));
            lines.Add($"| {project} | {values} |");
        }
        lines.Add("| **Reported total** | " +
            string.Join(" | ", AnalysisMetrics.Levels.Select(level => metrics.Totals.GetValueOrDefault(level))) + " |");
        lines.AddRange(["", "Counts cover owned source and tests, excluding suppressed, generated, example and vendored diagnostics.",
            "These are current totals, not counts of new issues. Existing warnings remain informational.", ""]);
        if (!metrics.ReportsComplete)
            lines.AddRange(["**Analysis is incomplete.** Missing reports are not zero findings; code-scanning upload is skipped.", ""]);
        if (metrics.Rules.Count > 0)
        {
            lines.AddRange(["| Most frequent rules | Findings |", "| --- | ---: |"]);
            foreach (var (ruleId, count) in metrics.Rules.OrderByDescending(rule => rule.Value).Take(10))
                lines.Add($"| {Markdown(ruleId)} | {count} |");
            lines.Add("");
        }
        lines.Add("CodeQL reports security findings separately in the PR's code-scanning checks and the repository Security view.");
        return string.Join("\n", lines) + "\n";
    }

    internal static int Run(Arguments arguments, Func<string, string?> environment, TextWriter output)
    {
        var root = Path.GetFullPath(arguments.Optional("root") ?? Environment.CurrentDirectory);
        var reports = Path.GetFullPath(arguments.Optional("reports") ?? Path.Combine("artifacts", "static-analysis"));
        var (metrics, sarif) = Collect(reports, root);
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "metrics.json"), metrics.ToJson().ToJsonString(JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(reports, "github.sarif"), sarif.ToJsonString(JsonOptions) + "\n");
        var body = Summary(metrics, environment);
        File.WriteAllText(Path.Combine(reports, "summary.md"), body);
        if (environment("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
            File.AppendAllText(stepSummary, body);
        if (environment("GITHUB_OUTPUT") is { Length: > 0 } outputs)
            File.AppendAllText(outputs, $"reports_complete={(metrics.ReportsComplete ? "true" : "false")}\n");
        // Bound the log annotations; the complete findings remain in SARIF and the counts above.
        var findings = sarif["runs"]![0]!["results"]!.AsArray().OrderBy(result => (string)result!["level"]! != "error");
        foreach (var result in findings.Take(10))
        {
            var physical = result!["locations"]![0]!["physicalLocation"]!;
            var path = CommandEscape((string)physical["artifactLocation"]!["uri"]!, true);
            var line = (int?)physical["region"]?["startLine"] ?? 1;
            var title = CommandEscape((string)result["ruleId"]!, true);
            var message = CommandEscape((string?)result["message"]?["text"] ?? "");
            var level = (string)result["level"]!;
            var severity = level is "error" or "warning" ? level : "notice";
            output.WriteLine($"::{severity} file={path},line={line},title={title}::{message}");
        }
        return 0;
    }
}
