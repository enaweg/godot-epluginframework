using System.Text;
using System.Text.RegularExpressions;

namespace CiReports;

/// <summary>
/// The single CI comment of a pull request: a table with the result of each job, then the reports the jobs wrote (test
/// build, static analysis, code coverage) as sections. The package job posts it early with only the download link; the
/// pr-comment job of CI-PR replaces it once all jobs have finished. The marker on its first line finds it again.
/// </summary>
internal static partial class PullRequestComment
{
    internal const string Marker = "<!-- eplugin-ci -->";

    /// <summary>GitHub rejects longer comment bodies.</summary>
    internal const int MaxLength = 65536;

    private const string Shortened =
        "Some details were left out to stay within GitHub's comment size limit; the workflow run summary has all of them.";

    [GeneratedRegex(@"<details>.*?</details>\n*", RegexOptions.Singleline)]
    private static partial Regex Details();

    internal static string Compose(IReadOnlyList<(string Job, string Result)> jobs, IReadOnlyList<string> sections,
        string? commit = null, string? runUrl = null, string? note = null, int maxLength = MaxLength)
    {
        var parts = sections.Select(section => section.Replace("\r\n", "\n", StringComparison.Ordinal).Trim())
            .Where(section => section.Length > 0).ToList();
        var body = Body(jobs, parts, commit, runUrl, note, false);
        // The large tables are collapsed; drop them from the last section on until the comment fits.
        while (body.Length > maxLength && LastDetails(parts) is var (index, match) && index >= 0)
        {
            parts[index] = parts[index].Remove(match.Index, match.Length).TrimEnd();
            body = Body(jobs, parts, commit, runUrl, note, true);
        }
        if (body.Length > maxLength)
        {
            var end = "\n\n" + Shortened + "\n";
            var cut = body.LastIndexOf('\n', Math.Max(0, maxLength - end.Length - 1));
            body = body[..Math.Max(cut, Marker.Length)].TrimEnd() + end;
        }
        return body;
    }

    private static (int Index, Match Match) LastDetails(List<string> parts)
    {
        for (var index = parts.Count - 1; index >= 0; index--)
        {
            var matches = Details().Matches(parts[index]);
            if (matches.Count > 0)
                return (index, matches[^1]);
        }
        return (-1, Match.Empty);
    }

    private static string Body(IReadOnlyList<(string Job, string Result)> jobs, IReadOnlyList<string> parts,
        string? commit, string? runUrl, string? note, bool shortened)
    {
        var lines = new List<string> { Marker, "### CI results", "" };
        if (jobs.Count > 0)
        {
            lines.AddRange(["| Job | Result |", "| --- | --- |"]);
            foreach (var (job, result) in jobs)
                lines.Add($"| {AnalysisReport.Markdown(job)} | {Result(result)} |");
            lines.Add("");
        }
        var origin = new List<string>();
        if (!string.IsNullOrEmpty(commit))
            origin.Add($"Commit {commit[..Math.Min(7, commit.Length)]}.");
        if (!string.IsNullOrEmpty(runUrl))
            origin.Add($"[Workflow run]({runUrl}).");
        if (origin.Count > 0)
            lines.AddRange([string.Join(" ", origin), ""]);
        if (!string.IsNullOrEmpty(note))
            lines.AddRange([note, ""]);
        foreach (var part in parts)
            lines.AddRange([part, ""]);
        if (shortened)
            lines.AddRange([Shortened, ""]);
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>A job result as GitHub reports it (success, failure, cancelled, skipped); failures stand out.</summary>
    private static string Result(string result)
    {
        var value = AnalysisReport.Markdown(result.Trim());
        return value switch
        {
            "" => "unknown",
            "failure" => "**failure**",
            _ => value,
        };
    }

    /// <summary>`--job label=result` pairs, in the order given.</summary>
    internal static List<(string Job, string Result)> ParseJobs(IEnumerable<string> values)
    {
        var jobs = new List<(string, string)>();
        foreach (var value in values)
        {
            var separator = value.LastIndexOf('=');
            if (separator <= 0)
                throw new ReportException($"error: --job {value} is not label=result", 2);
            jobs.Add((value[..separator].Trim(), value[(separator + 1)..]));
        }
        return jobs;
    }

    internal static int Run(Arguments arguments, TextWriter output)
    {
        var jobs = ParseJobs(arguments.All("job"));
        // A job that failed early writes no report; its result in the table says why it is missing.
        var sections = arguments.All("section").Where(File.Exists).Select(path => File.ReadAllText(path, Encoding.UTF8)).ToList();
        var body = Compose(jobs, sections, arguments.Optional("commit"), arguments.Optional("run-url"), arguments.Optional("note"));
        var path = Path.GetFullPath(arguments.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
        output.WriteLine($"Pull request comment: {sections.Count} of {arguments.All("section").Count} sections, {body.Length} characters");
        return 0;
    }
}
