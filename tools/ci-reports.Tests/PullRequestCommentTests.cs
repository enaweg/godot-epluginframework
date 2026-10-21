using Xunit;
using static CiReports.PullRequestComment;

namespace CiReports.Tests;

/// <summary>How the job results and the reports become the pull request's single CI comment.</summary>
public class PullRequestCommentTests : TemporaryDirectory
{
    private static readonly (string, string)[] Jobs = [("Test build", "success"), ("Tests", "failure"), ("Code coverage", "skipped")];

    [Fact]
    public void CommentStartsWithItsMarkerAndListsTheJobResults()
    {
        var text = Compose(Jobs, [], "c0ffee1234", "https://example.com/run");
        Assert.StartsWith(Marker + "\n### CI results\n", text);
        Assert.Contains("| Test build | success |\n| Tests | **failure** |\n| Code coverage | skipped |\n", text);
        Assert.Contains("Commit c0ffee1. [Workflow run](https://example.com/run).", text);
    }

    [Fact]
    public void SectionsFollowTheNoteInTheOrderGiven()
    {
        var text = Compose([("Test build", "success")], ["### ePlugin test build\r\n\r\nlink\r\n\r\n", "### Static analysis\n\ncounts"],
            note: "The other jobs are still running.");
        Assert.EndsWith("The other jobs are still running.\n\n### ePlugin test build\n\nlink\n\n### Static analysis\n\ncounts\n", text);
    }

    [Fact]
    public void EmptyValuesAreLeftOut()
    {
        var text = Compose([("Tests", " ")], ["", "  \n"]);
        Assert.Equal(Marker + "\n### CI results\n\n| Job | Result |\n| --- | --- |\n| Tests | unknown |\n", text);
    }

    [Fact]
    public void TooLongCommentsDropCollapsedTablesFromTheEnd()
    {
        var first = "### First\n\n<details><summary>Small</summary>\n\nkept\n\n</details>";
        var second = "### Second\n\n<details><summary>A</summary>\n\n" + new string('a', 400) + "\n\n</details>\n\n" +
            "<details><summary>B</summary>\n\n" + new string('b', 400) + "\n\n</details>\n\nfooter";
        var text = Compose(Jobs, [first, second], maxLength: 900);
        Assert.True(text.Length <= 900, $"{text.Length} characters");
        Assert.Contains(new string('a', 400), text);
        Assert.DoesNotContain("bbbb", text);
        Assert.Contains("kept", text);
        Assert.Contains("footer", text);
        Assert.EndsWith("the workflow run summary has all of them.\n", text);
    }

    [Fact]
    public void CommentsThatStillDoNotFitAreCutAtALine()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"line {index}"));
        var text = Compose(Jobs, [lines], maxLength: 400);
        Assert.True(text.Length <= 400, $"{text.Length} characters");
        Assert.StartsWith(Marker, text);
        Assert.Contains("line 0\n", text);
        Assert.Matches(@"line \d+\n\nSome details were left out", text);
        Assert.Single(text.Split("Some details were left out").Skip(1));
    }

    [Fact]
    public void JobsAreLabelResultPairs()
    {
        Assert.Equal([("Godot 4.4 = 4.6", "success")], ParseJobs(["Godot 4.4 = 4.6=success"]));
        Assert.Equal(2, Assert.Throws<ReportException>(() => ParseJobs(["success"])).ExitCode);
    }

    [Fact]
    public void RunSkipsMissingSectionsAndWritesTheComment()
    {
        var section = Path.Combine(Root, "build.md");
        File.WriteAllText(section, "### ePlugin test build\n\nlink\n");
        var output = Path.Combine(Root, "out", "comment.md");
        var arguments = Arguments.Parse(["--output", output, "--job", "Test build=success", "--section", section,
            "--section", Path.Combine(Root, "missing.md")], ["output", "job", "section"], ["job", "section"]);
        using var log = new StringWriter();
        Assert.Equal(0, Run(arguments, log));
        var text = File.ReadAllText(output);
        Assert.StartsWith(Marker, text);
        Assert.EndsWith("### ePlugin test build\n\nlink\n", text);
        Assert.Contains("1 of 2 sections", log.ToString());
    }
}
