using System.Text.Json.Nodes;
using Xunit;
using static CiReports.CoverageReport;

namespace CiReports.Tests;

/// <summary>How the Cobertura reports become the pull request comment, coverage.json and the coverage branch.</summary>
public class CoverageReportTests : TemporaryDirectory
{
    private string Output => Path.Combine(Root, "out");

    /// <summary>A Cobertura report of classes given as (path, {line: hits}) pairs.</summary>
    private static string Cobertura(string root, params (string Path, Dictionary<int, int> Lines)[] classes)
    {
        var body = string.Concat(classes.Select((type, index) =>
            $"<class name=\"C{index}\" filename=\"{root}/{type.Path}\"><methods/><lines>" +
            string.Concat(type.Lines.Select(line => $"<line number=\"{line.Key}\" hits=\"{line.Value}\"/>")) +
            "</lines></class>"));
        return $"<?xml version=\"1.0\"?><coverage><packages><package name=\"P\"><classes>{body}</classes></package></packages></coverage>";
    }

    private string Write(string name, params (string Path, Dictionary<int, int> Lines)[] classes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, Cobertura(Root, classes));
        return path;
    }

    private static Arguments Summary(params string[] args) => Arguments.Parse(args,
        ["report", "root", "baseline", "diff", "commit", "label", "repository-url", "html-url", "run-url", "output-dir"], ["report"]);

    private string Summarize(params string[] extra)
    {
        var report = Write("a.xml", (Owned + "A.cs", new() { [1] = 1, [2] = 0, [3] = 0 }), (Owned + "B.cs", new() { [5] = 2 }));
        RunSummary(Summary(["--report", report, "--root", Root, "--commit", "c0ffee1234", "--output-dir", Output, .. extra]),
            TextWriter.Null);
        return File.ReadAllText(Path.Combine(Output, "summary.md"));
    }

    [Fact]
    public void ReportsAreMergedAndLimitedToTheFramework()
    {
        var first = Write("a.xml", (Owned + "A.cs", new() { [1] = 1, [2] = 0 }), ("src/eplugin-framework/tests/T.cs", new() { [1] = 1 }));
        var second = Write("b.xml", (Owned + "A.cs", new() { [2] = 3, [3] = 0 }));
        var files = ReadReports([first, second], Root);
        Assert.Equal([Owned + "A.cs"], files.Keys);
        Assert.Equal(new Dictionary<int, bool> { [1] = true, [2] = true, [3] = false }, files[Owned + "A.cs"]);
    }

    [Fact]
    public void PathsOutsideTheRootAreFoundByTheFrameworkDirectory()
    {
        var report = Path.Combine(Root, "c.xml");
        File.WriteAllText(report, Cobertura("/home/runner/work/x", (Owned + "A.cs", new() { [1] = 1 })));
        Assert.Equal([Owned + "A.cs"], ReadReports([report], Root).Keys);
    }

    [Fact]
    public void ChangedLinesComeFromAddedHunksOnly()
    {
        var diff = string.Join("\n",
            "diff --git a/x b/x", "--- a/x", "+++ b/x", "@@ -1,2 +1,3 @@", "@@ -9 +10 @@", "@@ -20,2 +21,0 @@",
            "diff --git a/y b/y", "--- a/y", "+++ /dev/null", "@@ -1 +0,0 @@");
        var changes = ChangedLines(diff);
        Assert.Equal(["x"], changes.Keys);
        Assert.Equal([1, 2, 3, 10], changes["x"].Order());
    }

    [Fact]
    public void RangesJoinConsecutiveLines() => Assert.Equal(["1-3", "7", "9-10"], Ranges([9, 1, 2, 3, 7, 10]));

    [Fact]
    public void SummaryWithoutBaselineSaysSo()
    {
        var text = Summarize();
        Assert.StartsWith(Marker, text);
        Assert.Contains("**50.00%** | 2 of 4", text);
        Assert.Contains("nothing to compare with", text);
        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(Output, "coverage.json")))!;
        Assert.Equal("c0ffee1234", (string?)coverage["commit"]);
        Assert.Equal("""{"src/eplugin-framework/addons/ePlugin/A.cs":[1,3],"src/eplugin-framework/addons/ePlugin/B.cs":[1,1]}""",
            coverage["files"]!.ToJsonString());
    }

    [Fact]
    public void SummaryComparesWithMainPerFile()
    {
        var baseline = Path.Combine(Root, "latest.json");
        File.WriteAllText(baseline, $$$"""
            {"commit": "1234567890", "covered": 1, "coverable": 4,
             "files": {"{{{Owned}}}A.cs": [2, 3], "{{{Owned}}}Gone.cs": [1, 3]}}
            """);
        var text = Summarize("--baseline", baseline);
        Assert.Contains("| `main` (1234567) | 25.00% | 1 of 4 |", text);
        Assert.Contains("**+25.00 pp**", text);
        Assert.Contains("| `A.cs` | 66.7% | 33.3% | -33.33 pp |", text);
        Assert.Contains("| `B.cs` | new | 100.0% |  |", text);
        Assert.Contains("| `Gone.cs` | 33.3% | removed |  |", text);
    }

    [Fact]
    public void SummaryListsUncoveredChangedLines()
    {
        var diff = Path.Combine(Root, "pr.diff");
        File.WriteAllText(diff, $"+++ b/{Owned}A.cs\n@@ -1 +1,4 @@\n+++ b/README.md\n@@ -1 +1 @@\n");
        var text = Summarize("--diff", diff, "--repository-url", "https://github.com/o/r");
        Assert.Contains("**Changed lines:** 1 of 3 coverable lines covered (33.3%).", text);
        Assert.Contains($"[`A.cs`](https://github.com/o/r/blob/c0ffee1234/{Owned}A.cs#L2-L3) | 2-3 |", text);
        Assert.Contains("Uncovered changed lines (1 file)", text);
    }

    [Fact]
    public void ReportsWithoutFrameworkFilesFail()
    {
        var report = Write("a.xml", ("src/eplugin-framework/tests/T.cs", new() { [1] = 1 }));
        var exception = Assert.Throws<ReportException>(() =>
            RunSummary(Summary("--report", report, "--root", Root, "--output-dir", Output), TextWriter.Null));
        Assert.Equal(1, exception.ExitCode);
    }

    [Fact]
    public void HistoryReplacesARerunOfTheSameCommit()
    {
        var branch = Directory.CreateDirectory(Path.Combine(Root, "branch")).FullName;
        foreach (var (commit, covered) in new[] { ("aaaaaaa1", 1), ("bbbbbbb2", 2), ("bbbbbbb2", 3) })
        {
            var coverage = Path.Combine(Root, "coverage.json");
            File.WriteAllText(coverage, $$$"""
                {"commit": "{{{commit}}}", "date": "2026-10-14T10:00:00Z", "covered": {{{covered}}}, "coverable": 4,
                 "files": {"{{{Owned}}}A.cs": [{{{covered}}}, 4]}}
                """);
            RunHistory(Arguments.Parse(["--coverage", coverage, "--branch-dir", branch], ["coverage", "branch-dir", "repository-url"]));
        }
        var rows = ReadHistory(Path.Combine(branch, "history.csv"));
        Assert.Equal([("aaaaaaa1", "25.00"), ("bbbbbbb2", "75.00")], rows.Select(row => (row["commit"], row["line_coverage"])));
        var readme = File.ReadAllText(Path.Combine(branch, "README.md"));
        Assert.Contains("x-axis [\"aaaaaaa\", \"bbbbbbb\"]", readme);
        Assert.Contains("line [25.00, 75.00]", readme);
        Assert.Contains("| 75.00% | +50.00 pp |", readme);
        Assert.Contains("coverage: 75.0%", File.ReadAllText(Path.Combine(branch, "badge.svg")));
        Assert.Equal("bbbbbbb2", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(branch, "latest.json")))!["commit"]);
    }

    [Fact]
    public void HistoryKeepsTheRowsOfEarlierMeasurements()
    {
        var branch = Directory.CreateDirectory(Path.Combine(Root, "branch")).FullName;
        File.WriteAllText(Path.Combine(branch, "history.csv"),
            "date,commit,covered_lines,coverable_lines,line_coverage\n2026-10-13T09:00:00Z,aaaaaaa1,6000,8000,75.00\n");
        var coverage = Path.Combine(Root, "coverage.json");
        var files = new Dictionary<string, Dictionary<int, bool>> { [Owned + "A.cs"] = new() { [1] = true, [2] = false } };
        File.WriteAllText(coverage, ToJson(Measure(files, "bbbbbbb2", "2026-10-14T10:00:00Z")));
        RunHistory(Arguments.Parse(["--coverage", coverage, "--branch-dir", branch], ["coverage", "branch-dir", "repository-url"]));
        Assert.Equal(
            "date,commit,covered_lines,coverable_lines,line_coverage\n" +
            "2026-10-13T09:00:00Z,aaaaaaa1,6000,8000,75.00\n2026-10-14T10:00:00Z,bbbbbbb2,1,2,50.00\n",
            File.ReadAllText(Path.Combine(branch, "history.csv")));
    }
}
