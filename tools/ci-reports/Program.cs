using CiReports;

try
{
    return args switch
    {
        ["coverage", "summary", .. var rest] => CoverageReport.RunSummary(Arguments.Parse(rest,
            ["report", "root", "baseline", "diff", "commit", "label", "repository-url", "html-url", "run-url", "output-dir"],
            repeatable: ["report"]), Console.Out),
        ["coverage", "history", .. var rest] => CoverageReport.RunHistory(Arguments.Parse(rest,
            ["coverage", "branch-dir", "repository-url"])),
        ["analysis", .. var rest] => AnalysisReport.Run(Arguments.Parse(rest, ["root", "reports"]),
            Environment.GetEnvironmentVariable, Console.Out),
        _ => throw new ReportException(
            "usage: coverage summary --report <cobertura.xml>... --output-dir <dir> [--root <dir>] [--baseline <latest.json>]\n" +
            "         [--diff <pr.diff>] [--commit <sha>] [--label <text>] [--repository-url <url>] [--html-url <url>] [--run-url <url>]\n" +
            "       coverage history --coverage <coverage.json> --branch-dir <dir> [--repository-url <url>]\n" +
            "       analysis [--root <dir>] [--reports <dir>]", 2),
    };
}
catch (ReportException exception)
{
    Console.Error.WriteLine(exception.Message);
    return exception.ExitCode;
}
