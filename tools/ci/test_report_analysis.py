"""Reporting checks; run with python -m unittest discover -s tools/ci."""

import copy
import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

from report_analysis import PROJECTS, collect, command_escape, main, summary


class AnalysisReportingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.reports = self.root / "reports"
        self.reports.mkdir()

    def finding(self, rule_id="CA2208", path="src/eplugin-framework/addons/ePlugin/Test.cs", **extra):
        return {
            "ruleId": rule_id, "ruleIndex": 0, "level": "warning",
            "message": {"text": "Example diagnostic"},
            "locations": [{"physicalLocation": {
                "artifactLocation": {"uri": (self.root / path).as_uri()},
                "region": {"startLine": 7},
            }}],
            **extra,
        }

    def write_report(self, project, results):
        rules = [{"id": result["ruleId"], "helpUri": "https://example.com/rules"} for result in results]
        payload = {"version": "2.1.0", "runs": [{"results": results, "tool": {"driver": {"name": "Compiler", "rules": rules}}}]}
        (self.reports / f"{project}.sarif").write_text(json.dumps(payload), encoding="utf-8")

    def empty_reports(self):
        for project in PROJECTS:
            self.write_report(project, [])

    def test_counts_exclude_suppressed_generated_and_unowned_findings(self):
        self.empty_reports()
        owned = self.finding(path="src/eplugin-framework/addons/ePlugin/A space.cs")
        self.write_report(PROJECTS[0], [
            owned,
            self.finding(suppressions=[{"kind": "inSource"}]),
            self.finding(path="src/eplugin-framework/addons/ePlugin/obj/Generated.cs"),
            self.finding(path="src/eplugin-framework/addons/sample_plugin/Sample.cs"),
            self.finding(path="src/eplugin-framework/addons/gdUnit4/Vendor.cs"),
            self.finding(suppressions=[{"kind": "inSource", "status": "rejected"}]),
        ])
        metrics, sarif = collect(self.reports, self.root)
        self.assertEqual(2, metrics["totals"]["warning"])
        self.assertEqual({"suppressed": 1, "generated": 1, "outside owned source": 2}, metrics["excluded"])
        self.assertEqual("src/eplugin-framework/addons/ePlugin/A space.cs", sarif["runs"][0]["results"][0]["locations"][0]["physicalLocation"]["artifactLocation"]["uri"])
        self.assertEqual(7, sarif["runs"][0]["results"][0]["locations"][0]["physicalLocation"]["region"]["startLine"])

    def test_merging_reports_remaps_rules_and_keeps_levels_and_help_links(self):
        self.empty_reports()
        self.write_report(PROJECTS[0], [self.finding("CA2208", level="error")])
        self.write_report(PROJECTS[1], [self.finding("CA1806", "src/activation-progress/Native.cs")])
        metrics, sarif = collect(self.reports, self.root)
        run = sarif["runs"][0]
        self.assertTrue(metrics["reports_complete"])
        self.assertEqual(1, metrics["totals"]["error"])
        self.assertEqual(1, metrics["totals"]["warning"])
        for result in run["results"]:
            self.assertEqual(result["ruleId"], run["tool"]["driver"]["rules"][result["ruleIndex"]]["id"])
        self.assertEqual("https://example.com/rules", run["tool"]["driver"]["rules"][1]["helpUri"])

    def test_missing_report_is_incomplete_not_a_clean_project(self):
        self.write_report(PROJECTS[0], [])
        metrics, _ = collect(self.reports, self.root)
        with patch.dict(os.environ, {"RESTORE_HELPER": "failure"}):
            body = summary(metrics)
        self.assertFalse(metrics["reports_complete"])
        self.assertIsNone(metrics["projects"][PROJECTS[1]])
        self.assertIn("not produced", body)
        self.assertIn("Analysis is incomplete", body)
        self.assertIn("Helper dependency audit | failure", body)

    def test_outside_root_and_relative_parent_paths_are_excluded(self):
        self.empty_reports()
        outside = self.finding()
        outside["locations"][0]["physicalLocation"]["artifactLocation"]["uri"] = "../outside.cs"
        remote = copy.deepcopy(outside)
        remote["locations"][0]["physicalLocation"]["artifactLocation"]["uri"] = "https://example.com/outside.cs"
        self.write_report(PROJECTS[0], [outside, remote])
        metrics, sarif = collect(self.reports, self.root)
        self.assertEqual(2, metrics["excluded"]["outside owned source"])
        self.assertEqual([], sarif["runs"][0]["results"])

    def test_annotation_values_cannot_inject_workflow_commands(self):
        self.assertEqual("message%0A::error::injected%25", command_escape("message\n::error::injected%"))
        self.assertEqual("file%2Ctitle%3Afake%0D", command_escape("file,title:fake\r", True))

    def test_cli_publishes_summary_output_metrics_and_error_annotation(self):
        self.empty_reports()
        self.write_report(PROJECTS[0], [self.finding(level="error")])
        output = self.root / "outputs.txt"
        step_summary = self.root / "summary.txt"
        with patch.dict(os.environ, {
            "GITHUB_OUTPUT": str(output), "GITHUB_STEP_SUMMARY": str(step_summary),
            "ANALYSIS_RUN_URL": "https://github.com/example/repo/actions/runs/123",
        }), patch("sys.argv", ["report_analysis.py", "--root", str(self.root), "--reports", str(self.reports)]), redirect_stdout(io.StringIO()) as annotations:
            main()
        self.assertIn("reports_complete=true", output.read_text(encoding="utf-8"))
        self.assertIn("actions/runs/123", step_summary.read_text(encoding="utf-8"))
        self.assertIn("::error file=src/eplugin-framework/addons/ePlugin/Test.cs,line=7,title=CA2208::", annotations.getvalue())
        metrics = json.loads((self.reports / "metrics.json").read_text(encoding="utf-8"))
        self.assertEqual({"error": 1, "warning": 0, "note": 0}, metrics["totals"])


if __name__ == "__main__":
    unittest.main()
