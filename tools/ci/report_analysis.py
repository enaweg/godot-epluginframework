"""Publish owned-code Roslyn findings as GitHub summaries, annotations and SARIF."""

import argparse
import copy
import json
import os
from collections import Counter
from pathlib import Path
from urllib.parse import unquote, urlparse


PROJECTS = ("EPluginFramework", "ActivationProgress", "ActivationProgress.Tests")
OWNED_PATHS = (
    "src/eplugin-framework/addons/ePlugin/",
    "src/eplugin-framework/tests/",
    "src/activation-progress/",
    "src/activation-progress.Tests/",
)
STAGES = {
    "RESTORE_FRAMEWORK": "Framework dependency audit",
    "RESTORE_HELPER": "Helper dependency audit",
    "BUILD_FRAMEWORK": "Framework analysis build",
    "BUILD_HELPER": "Helper analysis build",
}


def repository_path(uri, root):
    parsed = urlparse(uri)
    if parsed.scheme not in ("", "file") or parsed.netloc:
        return None
    value = unquote(parsed.path)
    if os.name == "nt" and len(value) > 2 and value[0] == "/" and value[2] == ":":
        value = value[1:]
    path = Path(value)
    try:
        return (path if path.is_absolute() else root / path).resolve().relative_to(root).as_posix()
    except ValueError:
        return None


def markdown(value):
    return str(value).replace("\r", " ").replace("\n", " ").replace("|", "\\|")


def command_escape(value, property_value=False):
    value = str(value).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return value.replace(":", "%3A").replace(",", "%2C") if property_value else value


def collect(reports, root):
    metrics = {"projects": {}, "excluded": Counter(), "rules": Counter()}
    results, rules, rule_indexes = [], [], {}
    for project in PROJECTS:
        report = reports / f"{project}.sarif"
        if not report.exists():
            metrics["projects"][project] = None
            continue
        counts = Counter(error=0, warning=0, note=0)
        data = json.loads(report.read_text(encoding="utf-8-sig"))
        for run in data["runs"]:
            descriptors = {rule["id"]: rule for rule in run["tool"]["driver"].get("rules", [])}
            for original in run.get("results", []):
                if any(s.get("status") != "rejected" for s in original.get("suppressions", [])):
                    metrics["excluded"]["suppressed"] += 1
                    continue
                locations = original.get("locations", [])
                physical = locations[0].get("physicalLocation", {}) if locations else {}
                path = repository_path(physical.get("artifactLocation", {}).get("uri", ""), root)
                if not path or not path.startswith(OWNED_PATHS):
                    metrics["excluded"]["outside owned source"] += 1
                    continue
                if any(part in ("obj", "bin", ".godot") for part in path.split("/")) or path.endswith(
                    (".generated.cs", ".g.cs")
                ):
                    metrics["excluded"]["generated"] += 1
                    continue
                rule_id = original["ruleId"]
                descriptor = descriptors.get(rule_id, {"id": rule_id})
                level = original.get("level", descriptor.get("defaultConfiguration", {}).get("level", "warning"))
                if level == "none":
                    continue
                counts[level] += 1
                metrics["rules"][rule_id] += 1
                if rule_id not in rule_indexes:
                    rule_indexes[rule_id] = len(rules)
                    rules.append(copy.deepcopy(descriptor))
                # Keep location and rule metadata while removing absolute runner paths and fix payloads.
                location = {"physicalLocation": {"artifactLocation": {"uri": path}}}
                if "region" in physical:
                    location["physicalLocation"]["region"] = copy.deepcopy(physical["region"])
                results.append({
                    "ruleId": rule_id,
                    "ruleIndex": rule_indexes[rule_id],
                    "level": level,
                    "message": copy.deepcopy(original["message"]),
                    "locations": [location],
                    "properties": {"project": project},
                })
        metrics["projects"][project] = dict(counts)
    totals = sum((Counter(c) for c in metrics["projects"].values() if c is not None), Counter())
    metrics["totals"] = {level: totals[level] for level in ("error", "warning", "note")}
    metrics["reports_complete"] = all(c is not None for c in metrics["projects"].values())
    sarif = {
        "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
        "version": "2.1.0",
        "runs": [{
            "tool": {"driver": {"name": ".NET SDK analyzers", "rules": rules}},
            "automationDetails": {"id": "dotnet/"},
            "columnKind": "utf16CodeUnits",
            "results": results,
        }],
    }
    return metrics, sarif


def summary(metrics):
    lines = ["<!-- eplugin-static-analysis -->", "### Static analysis", ""]
    run_url = os.environ.get("ANALYSIS_RUN_URL")
    if run_url:
        lines.extend([f"[Workflow run and downloadable reports]({run_url})", ""])
    lines.extend(["| Check | Status |", "| --- | --- |"])
    for variable, label in STAGES.items():
        lines.append(f"| {label} | {markdown(os.environ.get(variable, 'not run'))} |")
    lines.extend(["", "| Project | Errors | Warnings | Notes |", "| --- | ---: | ---: | ---: |"])
    for project, counts in metrics["projects"].items():
        values = " | ".join(str(counts.get(level, 0)) for level in ("error", "warning", "note")) if counts is not None else "not produced | — | —"
        lines.append(f"| {project} | {values} |")
    totals = metrics["totals"]
    lines.append("| **Reported total** | " + " | ".join(str(totals.get(level, 0)) for level in ("error", "warning", "note")) + " |")
    lines.extend(["", "Counts cover owned source and tests, excluding suppressed, generated, example and vendored diagnostics.",
                  "These are current totals, not counts of new issues. Existing warnings remain informational.", ""])
    if not metrics["reports_complete"]:
        lines.extend(["**Analysis is incomplete.** Missing reports are not zero findings; code-scanning upload is skipped.", ""])
    if metrics["rules"]:
        lines.extend(["| Most frequent rules | Findings |", "| --- | ---: |"])
        for rule_id, count in metrics["rules"].most_common(10):
            lines.append(f"| {markdown(rule_id)} | {count} |")
        lines.append("")
    lines.append("CodeQL reports security findings separately in the PR's code-scanning checks and the repository Security view.")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--reports", type=Path, default=Path("artifacts/static-analysis"))
    args = parser.parse_args()
    metrics, sarif = collect(args.reports, args.root.resolve())
    args.reports.mkdir(parents=True, exist_ok=True)
    (args.reports / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
    (args.reports / "github.sarif").write_text(json.dumps(sarif, indent=2) + "\n", encoding="utf-8")
    body = summary(metrics)
    (args.reports / "summary.md").write_text(body, encoding="utf-8")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(body)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"reports_complete={str(metrics['reports_complete']).lower()}\n")
    # Bound the log annotations; the complete findings remain in SARIF and the counts above.
    findings = sorted(sarif["runs"][0]["results"], key=lambda r: r["level"] != "error")
    for result in findings[:10]:
        physical = result["locations"][0]["physicalLocation"]
        path = command_escape(physical["artifactLocation"]["uri"], True)
        line = physical.get("region", {}).get("startLine", 1)
        title = command_escape(result["ruleId"], True)
        message = command_escape(result["message"].get("text", ""))
        severity = result["level"] if result["level"] in ("error", "warning") else "notice"
        print(f"::{severity} file={path},line={line},title={title}::{message}")


if __name__ == "__main__":
    main()
