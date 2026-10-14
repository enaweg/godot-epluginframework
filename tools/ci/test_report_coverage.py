"""Coverage reporting checks; run with python -m unittest discover -s tools/ci."""

import csv
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

from report_coverage import MARKER, OWNED, changed_lines, main, ranges, read_reports


def cobertura(root, classes):
    """classes: (path, {line: hits}) pairs."""
    body = "".join(
        f'<class name="C{index}" filename="{root / path}"><methods/><lines>'
        + "".join(f'<line number="{number}" hits="{hits}"/>' for number, hits in lines.items())
        + "</lines></class>"
        for index, (path, lines) in enumerate(classes)
    )
    return f'<?xml version="1.0"?><coverage><packages><package name="P"><classes>{body}</classes></package></packages></coverage>'


class CoverageReportingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.output = self.root / "out"

    def write(self, name, classes):
        path = self.root / name
        path.write_text(cobertura(self.root, classes), encoding="utf-8")
        return str(path)

    def summarize(self, *extra):
        report = self.write("a.xml", [(OWNED + "A.cs", {1: 1, 2: 0, 3: 0}), (OWNED + "B.cs", {5: 2})])
        with redirect_stdout(io.StringIO()):
            main(["summary", "--report", report, "--root", str(self.root), "--commit", "c0ffee1234",
                  "--output-dir", str(self.output), *extra])
        return (self.output / "summary.md").read_text(encoding="utf-8")

    def test_reports_are_merged_and_limited_to_the_framework(self):
        first = self.write("a.xml", [(OWNED + "A.cs", {1: 1, 2: 0}), ("src/eplugin-framework/tests/T.cs", {1: 1})])
        second = self.write("b.xml", [(OWNED + "A.cs", {2: 3, 3: 0})])
        files = read_reports([first, second], self.root)
        self.assertEqual({OWNED + "A.cs": {1: True, 2: True, 3: False}}, files)

    def test_paths_outside_the_root_are_found_by_the_framework_directory(self):
        report = self.root / "c.xml"
        report.write_text(cobertura(Path("/home/runner/work/x"), [(OWNED + "A.cs", {1: 1})]), encoding="utf-8")
        self.assertEqual([OWNED + "A.cs"], list(read_reports([str(report)], self.root)))

    def test_changed_lines_come_from_added_hunks_only(self):
        diff = "\n".join([
            "diff --git a/x b/x", "--- a/x", "+++ b/x", "@@ -1,2 +1,3 @@", "@@ -9 +10 @@", "@@ -20,2 +21,0 @@",
            "diff --git a/y b/y", "--- a/y", "+++ /dev/null", "@@ -1 +0,0 @@",
        ])
        self.assertEqual({"x": {1, 2, 3, 10}}, changed_lines(diff))

    def test_ranges_join_consecutive_lines(self):
        self.assertEqual(["1-3", "7", "9-10"], ranges([9, 1, 2, 3, 7, 10]))

    def test_summary_without_baseline_says_so(self):
        text = self.summarize()
        self.assertTrue(text.startswith(MARKER))
        self.assertIn("**50.00%** | 2 of 4", text)
        self.assertIn("nothing to compare with", text)
        coverage = json.loads((self.output / "coverage.json").read_text(encoding="utf-8"))
        self.assertEqual({OWNED + "A.cs": [1, 3], OWNED + "B.cs": [1, 1]}, coverage["files"])
        self.assertEqual("c0ffee1234", coverage["commit"])

    def test_summary_compares_with_main_per_file(self):
        baseline = self.root / "latest.json"
        baseline.write_text(json.dumps({
            "commit": "1234567890", "covered": 1, "coverable": 4,
            "files": {OWNED + "A.cs": [2, 3], OWNED + "Gone.cs": [1, 3]},
        }), encoding="utf-8")
        text = self.summarize("--baseline", str(baseline))
        self.assertIn("| `main` (1234567) | 25.00% | 1 of 4 |", text)
        self.assertIn("**+25.00 pp**", text)
        self.assertIn("| `A.cs` | 66.7% | 33.3% | -33.33 pp |", text)
        self.assertIn("| `B.cs` | new | 100.0% |  |", text)
        self.assertIn("| `Gone.cs` | 33.3% | removed |  |", text)

    def test_summary_lists_uncovered_changed_lines(self):
        diff = self.root / "pr.diff"
        diff.write_text(f"+++ b/{OWNED}A.cs\n@@ -1 +1,4 @@\n+++ b/README.md\n@@ -1 +1 @@\n", encoding="utf-8")
        text = self.summarize("--diff", str(diff), "--repository-url", "https://github.com/o/r")
        self.assertIn("**Changed lines:** 1 of 3 coverable lines covered (33.3%).", text)
        self.assertIn(f"[`A.cs`](https://github.com/o/r/blob/c0ffee1234/{OWNED}A.cs#L2-L3) | 2-3 |", text)
        self.assertIn("Uncovered changed lines (1 file)", text)

    def test_history_replaces_a_rerun_of_the_same_commit(self):
        branch = self.root / "branch"
        branch.mkdir()
        for commit, covered in (("aaaaaaa1", 1), ("bbbbbbb2", 2), ("bbbbbbb2", 3)):
            coverage = self.root / "coverage.json"
            coverage.write_text(json.dumps({
                "commit": commit, "date": "2026-10-14T10:00:00Z", "covered": covered, "coverable": 4,
                "files": {OWNED + "A.cs": [covered, 4]},
            }), encoding="utf-8")
            main(["history", "--coverage", str(coverage), "--branch-dir", str(branch)])
        with (branch / "history.csv").open(encoding="utf-8") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual([("aaaaaaa1", "25.00"), ("bbbbbbb2", "75.00")], [(r["commit"], r["line_coverage"]) for r in rows])
        readme = (branch / "README.md").read_text(encoding="utf-8")
        self.assertIn('x-axis ["aaaaaaa", "bbbbbbb"]', readme)
        self.assertIn("line [25.00, 75.00]", readme)
        self.assertIn("| 75.00% | +50.00 pp |", readme)
        self.assertIn("coverage: 75.0%", (branch / "badge.svg").read_text(encoding="utf-8"))
        self.assertEqual("bbbbbbb2", json.loads((branch / "latest.json").read_text(encoding="utf-8"))["commit"])


if __name__ == "__main__":
    unittest.main()
