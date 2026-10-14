"""Summarize framework line coverage for pull requests and keep its history on the coverage branch.

summary: merges the Cobertura reports of the test runs, compares them with the latest main measurement and the
         pull request's changed lines, and writes summary.md (PR comment and run summary) and coverage.json.
history: records a main measurement (coverage.json) in a checkout of the coverage branch: latest.json,
         history.csv, badge.svg and a README.md with a trend chart that GitHub renders.
"""

import argparse
import csv
import json
import math
import re
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timezone
from pathlib import Path


OWNED = "src/eplugin-framework/addons/ePlugin/"
MARKER = "<!-- eplugin-coverage -->"
HISTORY_FIELDS = ("date", "commit", "covered_lines", "coverable_lines", "line_coverage")
CHART_POINTS = 30
TABLE_ROWS = 30


def repository_path(filename, root):
    value = filename.replace("\\", "/")
    prefix = root.as_posix().rstrip("/") + "/"
    if value.lower().startswith(prefix.lower()):
        return value[len(prefix):]
    index = value.find("/" + OWNED)
    return value[index + 1:] if index >= 0 else value


def read_reports(paths, root):
    """Line hits per owned file, merged over all reports (a line counts as covered when any run covered it)."""
    files = {}
    for report in paths:
        for cls in ElementTree.parse(report).getroot().iter("class"):
            path = repository_path(cls.get("filename", ""), root)
            if not path.startswith(OWNED):
                continue
            lines = files.setdefault(path, {})
            for line in cls.iter("line"):
                number = int(line.get("number"))
                lines[number] = lines.get(number, False) or int(line.get("hits", "0")) > 0
    return files


def measure(files, commit, date):
    counts = {path: [sum(lines.values()), len(lines)] for path, lines in sorted(files.items()) if lines}
    return {
        "commit": commit,
        "date": date,
        "covered": sum(covered for covered, _ in counts.values()),
        "coverable": sum(coverable for _, coverable in counts.values()),
        "files": counts,
    }


def percent(covered, coverable):
    return 100.0 * covered / coverable if coverable else 100.0


def changed_lines(diff):
    """Added or modified lines per file of a `git diff --unified=0`."""
    changes, path = {}, None
    for line in diff.splitlines():
        if line.startswith("+++ "):
            target = line[4:].strip()
            path = target[2:] if target.startswith("b/") else None
        elif line.startswith("@@") and path:
            match = re.match(r"@@ -\S+ \+(\d+)(?:,(\d+))? @@", line)
            if match:
                start, count = int(match.group(1)), int(match.group(2) or "1")
                changes.setdefault(path, set()).update(range(start, start + count))
    return changes


def ranges(numbers):
    parts, numbers = [], sorted(numbers)
    start = previous = numbers[0] if numbers else None
    for number in numbers[1:] + [None]:
        if number is not None and number == previous + 1:
            previous = number
            continue
        parts.append(str(start) if start == previous else f"{start}-{previous}")
        start = previous = number
    return parts


def short(path):
    return path[len(OWNED):] if path.startswith(OWNED) else path


def short_commit(commit):
    return (commit or "")[:7]


def count(number, noun, plural=None):
    return f"{number} {noun if number == 1 else plural or noun + 's'}"


def signed(value):
    return f"{value:+.2f} pp"


def summary(current, baseline, files, changes, links):
    total = percent(current["covered"], current["coverable"])
    lines = [MARKER, "### Code coverage", ""]
    lines += ["| | Line coverage | Covered lines |", "|:--|--:|--:|"]
    lines.append(f"| {links.get('label', 'This run')} | **{total:.2f}%** | {current['covered']} of {current['coverable']} |")
    if baseline:
        base_total = percent(baseline["covered"], baseline["coverable"])
        lines.append(
            f"| `main` ({short_commit(baseline.get('commit'))}) | {base_total:.2f}% | "
            f"{baseline['covered']} of {baseline['coverable']} |"
        )
        lines.append(f"| Change | **{signed(total - base_total)}** | |")
    lines.append("")
    if not baseline:
        lines += ["No `main` measurement on the `coverage` branch yet, so there is nothing to compare with.", ""]

    if changes is not None:
        covered, coverable, uncovered = 0, 0, {}
        for path, numbers in changes.items():
            hits = files.get(path, {})
            for number in numbers:
                if number in hits:
                    coverable += 1
                    covered += hits[number]
                    if not hits[number]:
                        uncovered.setdefault(path, []).append(number)
        if coverable:
            lines.append(
                f"**Changed lines:** {covered} of {coverable} coverable lines covered "
                f"({percent(covered, coverable):.1f}%)."
            )
        else:
            lines.append("**Changed lines:** this pull request changes no coverable framework lines.")
        lines.append("")
        if uncovered:
            lines += [f"<details><summary>Uncovered changed lines ({count(len(uncovered), 'file')})</summary>", ""]
            lines += ["| File | Lines |", "|:--|:--|"]
            for path in sorted(uncovered):
                parts = ranges(uncovered[path])
                anchor = "L" + parts[0].replace("-", "-L")
                url = f"{links['blob']}/{path}#{anchor}" if links.get("blob") else None
                name = f"[`{short(path)}`]({url})" if url else f"`{short(path)}`"
                lines.append(f"| {name} | {', '.join(parts)} |")
            lines += ["", "</details>", ""]

    if baseline:
        base_files = baseline.get("files", {})
        differences = []
        for path in sorted(set(current["files"]) | set(base_files)):
            now, before = current["files"].get(path), base_files.get(path)
            if now == before:
                continue
            now_value = percent(*now) if now else None
            before_value = percent(*before) if before else None
            change = (now_value or 0.0) - (before_value or 0.0)
            if now and before and abs(change) < 0.005:
                continue
            differences.append((path, before_value, now_value, change))
        if differences:
            differences.sort(key=lambda item: item[3])
            lines += [f"<details><summary>Files with changed coverage ({len(differences)})</summary>", ""]
            lines += ["| File | `main` | Now | Change |", "|:--|--:|--:|--:|"]
            for path, before_value, now_value, change in differences[:TABLE_ROWS]:
                before_text = f"{before_value:.1f}%" if before_value is not None else "new"
                now_text = f"{now_value:.1f}%" if now_value is not None else "removed"
                change_text = signed(change) if before_value is not None and now_value is not None else ""
                lines.append(f"| `{short(path)}` | {before_text} | {now_text} | {change_text} |")
            if len(differences) > TABLE_ROWS:
                lines.append(f"| … {len(differences) - TABLE_ROWS} more | | | |")
            lines += ["", "</details>", ""]

    lines += file_table(current, f"All files ({len(current['files'])})")
    footer = "Line coverage of `src/eplugin-framework/addons/ePlugin` by the gdUnit4 tests of all SDK runs."
    if links.get("html"):
        footer += f" [Browse uncovered lines]({links['html']}) (HTML report)."
    if links.get("run"):
        footer += f" [Workflow run]({links['run']})."
    lines.append(footer)
    return "\n".join(lines) + "\n"


def file_table(measurement, title):
    lines = [f"<details><summary>{title}</summary>", "", "| File | Line coverage | Covered lines |", "|:--|--:|--:|"]
    for path, (covered, coverable) in sorted(measurement["files"].items(), key=lambda item: percent(*item[1])):
        lines.append(f"| `{short(path)}` | {percent(covered, coverable):.1f}% | {covered} of {coverable} |")
    return lines + ["", "</details>", ""]


def badge(value):
    colors = ((90, "#4c1"), (80, "#97ca00"), (70, "#a4a61d"), (60, "#dfb317"), (50, "#fe7d37"), (0, "#e05d44"))
    color = next(color for limit, color in colors if value >= limit)
    text = f"{value:.1f}%"
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" width="108" height="20" role="img" '
        f'aria-label="coverage: {text}"><title>coverage: {text}</title>'
        '<linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/>'
        '<stop offset="1" stop-opacity=".1"/></linearGradient>'
        '<clipPath id="r"><rect width="108" height="20" rx="3" fill="#fff"/></clipPath>'
        f'<g clip-path="url(#r)"><rect width="61" height="20" fill="#555"/><rect x="61" width="47" height="20" '
        f'fill="{color}"/><rect width="108" height="20" fill="url(#s)"/></g>'
        '<g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">'
        '<text x="30.5" y="14">coverage</text>'
        f'<text x="84.5" y="14">{text}</text></g></svg>\n'
    )


def read_history(path):
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as stream:
        return list(csv.DictReader(stream))


def write_history(path, rows):
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=HISTORY_FIELDS, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def history_readme(rows, latest, links):
    total = percent(latest["covered"], latest["coverable"])
    commit_url = f"{links['repository']}/commit/" if links.get("repository") else None

    def commit_link(commit):
        return f"[{short_commit(commit)}]({commit_url}{commit})" if commit_url else short_commit(commit)

    lines = [
        "# Code coverage history",
        "",
        "Line coverage of `src/eplugin-framework/addons/ePlugin` on `main`, recorded by the CI-PR workflow after "
        "every push. This branch is generated; do not edit it.",
        "",
        "![Line coverage](badge.svg)",
        "",
        f"**{total:.2f}%** ({latest['covered']} of {latest['coverable']} lines) at "
        f"{commit_link(latest['commit'])}, {latest['date'][:10]}.",
        "",
    ]
    recent = rows[-CHART_POINTS:]
    values = [float(row["line_coverage"]) for row in recent]
    low = max(0, math.floor(min(values)) - 2)
    high = min(100, math.ceil(max(values)) + 2)
    labels = ", ".join(f'"{short_commit(row["commit"])}"' for row in recent)
    pushes = count(len(recent), "push", "pushes")
    lines += [
        "```mermaid",
        "xychart-beta",
        f'    title "Line coverage on main (last {pushes})"',
        f"    x-axis [{labels}]",
        f'    y-axis "Line coverage (%)" {low} --> {high}',
        f"    line [{', '.join(f'{value:.2f}' for value in values)}]",
        "```",
        "",
        "| Date | Commit | Line coverage | Change |",
        "|:--|:--|--:|--:|",
    ]
    newest = list(reversed(rows))
    for index, row in enumerate(newest[:TABLE_ROWS]):
        value = float(row["line_coverage"])
        older = newest[index + 1] if index + 1 < len(newest) else None
        change = signed(value - float(older["line_coverage"])) if older else ""
        lines.append(f"| {row['date'][:10]} | {commit_link(row['commit'])} | {value:.2f}% | {change} |")
    lines += ["", "All measurements: [history.csv](history.csv).", ""]
    lines += file_table(latest, f"Files at {short_commit(latest['commit'])} ({len(latest['files'])})")
    return "\n".join(lines)


def run_summary(arguments):
    root = Path(arguments.root).resolve()
    files = read_reports(arguments.report, root)
    if not files:
        raise SystemExit("error: the coverage reports contain no framework files")
    date = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    current = measure(files, arguments.commit, date)
    baseline = None
    if arguments.baseline and Path(arguments.baseline).is_file() and Path(arguments.baseline).stat().st_size:
        baseline = json.loads(Path(arguments.baseline).read_text(encoding="utf-8"))
    changes = None
    if arguments.diff:
        changes = changed_lines(Path(arguments.diff).read_text(encoding="utf-8"))
    links = {"label": arguments.label, "html": arguments.html_url, "run": arguments.run_url}
    if arguments.repository_url and arguments.commit:
        links["blob"] = f"{arguments.repository_url}/blob/{arguments.commit}"
    output = Path(arguments.output_dir)
    output.mkdir(parents=True, exist_ok=True)
    (output / "coverage.json").write_text(json.dumps(current, indent=1) + "\n", encoding="utf-8")
    (output / "summary.md").write_text(summary(current, baseline, files, changes, links), encoding="utf-8")
    print(f"Line coverage: {percent(current['covered'], current['coverable']):.2f}% "
          f"({current['covered']} of {current['coverable']} lines)")


def run_history(arguments):
    latest = json.loads(Path(arguments.coverage).read_text(encoding="utf-8"))
    branch = Path(arguments.branch_dir)
    rows = [row for row in read_history(branch / "history.csv") if row["commit"] != latest["commit"]]
    value = percent(latest["covered"], latest["coverable"])
    rows.append({
        "date": latest["date"], "commit": latest["commit"], "covered_lines": latest["covered"],
        "coverable_lines": latest["coverable"], "line_coverage": f"{value:.2f}",
    })
    write_history(branch / "history.csv", rows)
    (branch / "latest.json").write_text(json.dumps(latest, indent=1) + "\n", encoding="utf-8")
    (branch / "badge.svg").write_text(badge(value), encoding="utf-8")
    links = {"repository": arguments.repository_url}
    (branch / "README.md").write_text(history_readme(rows, latest, links), encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    parse_summary = commands.add_parser("summary")
    parse_summary.add_argument("--report", action="append", required=True, help="Cobertura report (repeatable)")
    parse_summary.add_argument("--root", default=".", help="repository root the report paths start with")
    parse_summary.add_argument("--baseline", help="latest.json of the coverage branch")
    parse_summary.add_argument("--diff", help="git diff --unified=0 of the pull request")
    parse_summary.add_argument("--commit", default="")
    parse_summary.add_argument("--label", default="This run")
    parse_summary.add_argument("--repository-url")
    parse_summary.add_argument("--html-url")
    parse_summary.add_argument("--run-url")
    parse_summary.add_argument("--output-dir", required=True)
    parse_history = commands.add_parser("history")
    parse_history.add_argument("--coverage", required=True, help="coverage.json written by summary")
    parse_history.add_argument("--branch-dir", required=True, help="checkout of the coverage branch")
    parse_history.add_argument("--repository-url")
    arguments = parser.parse_args(argv)
    (run_summary if arguments.command == "summary" else run_history)(arguments)


if __name__ == "__main__":
    main()
