using System.Text.Json.Nodes;
using Xunit;
using static CiReports.AnalysisReport;

namespace CiReports.Tests;

/// <summary>How the analyzers' SARIF reports become counts, the summary, annotations and the code-scanning upload.</summary>
public class AnalysisReportTests : TemporaryDirectory
{
    private readonly string _reports;

    public AnalysisReportTests() => _reports = Directory.CreateDirectory(Path.Combine(Root, "reports")).FullName;

    private JsonObject Finding(string ruleId = "CA2208", string path = "src/eplugin-framework/addons/ePlugin/Test.cs",
        string level = "warning", JsonArray? suppressions = null)
    {
        var finding = new JsonObject
        {
            ["ruleId"] = ruleId,
            ["ruleIndex"] = 0,
            ["level"] = level,
            ["message"] = new JsonObject { ["text"] = "Example diagnostic" },
            ["locations"] = new JsonArray(new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = new Uri(Path.Combine(Root, path)).AbsoluteUri },
                    ["region"] = new JsonObject { ["startLine"] = 7 },
                },
            }),
        };
        if (suppressions is not null)
            finding["suppressions"] = suppressions;
        return finding;
    }

    private static JsonArray Suppressions(string? status = null)
    {
        var suppression = new JsonObject { ["kind"] = "inSource" };
        if (status is not null)
            suppression["status"] = status;
        return [suppression];
    }

    private void WriteReport(string project, params JsonObject[] results)
    {
        var rules = new JsonArray(results.Select(result =>
            (JsonNode?)new JsonObject { ["id"] = result["ruleId"]!.DeepClone(), ["helpUri"] = "https://example.com/rules" }).ToArray());
        var payload = new JsonObject
        {
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["results"] = new JsonArray(results.Select(JsonNode? (result) => result).ToArray()),
                ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = "Compiler", ["rules"] = rules } },
            }),
        };
        File.WriteAllText(Path.Combine(_reports, $"{project}.sarif"), payload.ToJsonString());
    }

    private void EmptyReports()
    {
        foreach (var project in Projects)
            WriteReport(project);
    }

    private static JsonNode FirstLocation(JsonObject sarif) =>
        sarif["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!;

    [Fact]
    public void CountsExcludeSuppressedGeneratedAndUnownedFindings()
    {
        EmptyReports();
        WriteReport(Projects[0],
            Finding(path: "src/eplugin-framework/addons/ePlugin/A space.cs"),
            Finding(suppressions: Suppressions()),
            Finding(path: "src/eplugin-framework/addons/ePlugin/obj/Generated.cs"),
            Finding(path: "src/eplugin-framework/addons/sample_plugin/Sample.cs"),
            Finding(path: "src/eplugin-framework/addons/gdUnit4/Vendor.cs"),
            Finding(suppressions: Suppressions("rejected")));
        var (metrics, sarif) = Collect(_reports, Root);
        Assert.Equal(2, metrics.Totals["warning"]);
        Assert.Equal(new Dictionary<string, int> { ["suppressed"] = 1, ["generated"] = 1, ["outside owned source"] = 2 },
            metrics.Excluded);
        Assert.Equal("src/eplugin-framework/addons/ePlugin/A space.cs", (string?)FirstLocation(sarif)["artifactLocation"]!["uri"]);
        Assert.Equal(7, (int?)FirstLocation(sarif)["region"]!["startLine"]);
    }

    [Fact]
    public void MergingReportsRemapsRulesAndKeepsLevelsAndHelpLinks()
    {
        EmptyReports();
        WriteReport(Projects[0], Finding("CA2208", level: "error"));
        WriteReport(Projects[1], Finding("CA1806", "src/activation-progress/Native.cs"));
        var (metrics, sarif) = Collect(_reports, Root);
        var run = sarif["runs"]![0]!;
        Assert.True(metrics.ReportsComplete);
        Assert.Equal(1, metrics.Totals["error"]);
        Assert.Equal(1, metrics.Totals["warning"]);
        var rules = run["tool"]!["driver"]!["rules"]!.AsArray();
        foreach (var result in run["results"]!.AsArray())
            Assert.Equal((string?)result!["ruleId"], (string?)rules[(int)result["ruleIndex"]!]!["id"]);
        Assert.Equal("https://example.com/rules", (string?)rules[1]!["helpUri"]);
    }

    [Fact]
    public void MissingReportIsIncompleteNotACleanProject()
    {
        WriteReport(Projects[0]);
        var (metrics, _) = Collect(_reports, Root);
        var body = Summary(metrics, name => name == "RESTORE_HELPER" ? "failure" : null);
        Assert.False(metrics.ReportsComplete);
        Assert.Null(metrics.Projects[Projects[1]]);
        Assert.Contains("not produced", body);
        Assert.Contains("Analysis is incomplete", body);
        Assert.Contains("Helper dependency audit | failure", body);
    }

    [Fact]
    public void OutsideRootAndRelativeParentPathsAreExcluded()
    {
        EmptyReports();
        var outside = Finding();
        outside["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = "../outside.cs";
        var remote = Finding();
        remote["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uri"] = "https://example.com/outside.cs";
        WriteReport(Projects[0], outside, remote);
        var (metrics, sarif) = Collect(_reports, Root);
        Assert.Equal(2, metrics.Excluded["outside owned source"]);
        Assert.Empty(sarif["runs"]![0]!["results"]!.AsArray());
    }

    [Fact]
    public void AnnotationValuesCannotInjectWorkflowCommands()
    {
        Assert.Equal("message%0A::error::injected%25", CommandEscape("message\n::error::injected%"));
        Assert.Equal("file%2Ctitle%3Afake%0D", CommandEscape("file,title:fake\r", true));
    }

    [Fact]
    public void CliPublishesSummaryOutputMetricsAndErrorAnnotation()
    {
        EmptyReports();
        WriteReport(Projects[0], Finding(level: "error"));
        var outputs = Path.Combine(Root, "outputs.txt");
        var stepSummary = Path.Combine(Root, "summary.txt");
        var environment = new Dictionary<string, string>
        {
            ["GITHUB_OUTPUT"] = outputs,
            ["GITHUB_STEP_SUMMARY"] = stepSummary,
            ["ANALYSIS_RUN_URL"] = "https://github.com/example/repo/actions/runs/123",
        };
        var annotations = new StringWriter();
        Run(Arguments.Parse(["--root", Root, "--reports", _reports], ["root", "reports"]), environment.GetValueOrDefault,
            annotations);
        Assert.Contains("reports_complete=true", File.ReadAllText(outputs));
        Assert.Contains("actions/runs/123", File.ReadAllText(stepSummary));
        Assert.Contains("::error file=src/eplugin-framework/addons/ePlugin/Test.cs,line=7,title=CA2208::", annotations.ToString());
        var metrics = JsonNode.Parse(File.ReadAllText(Path.Combine(_reports, "metrics.json")))!;
        Assert.Equal("""{"error":1,"warning":0,"note":0}""", metrics["totals"]!.ToJsonString());
    }
}
