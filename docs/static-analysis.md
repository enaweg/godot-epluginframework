# Static analysis in CI

Pull requests and pushes to `main` run the following checks:

| Workflow / job | What it checks | Failure policy |
| --- | --- | --- |
| [CI-Analysis-Jobs / managed-analysis](../.github/workflows/ci-analysis-jobs.yml) | SDK analyzers, compiler diagnostics, and NuGet Audit for the framework, progress helper and helper tests, after the reporting tests (`tools/ci-reports.Tests`) | Failing reporting tests, build errors, high/critical dependency vulnerabilities, and failure to retrieve vulnerability data fail the job. Existing analyzer warnings remain informational. |
| [CI-Analysis-Jobs / workflow-lint](../.github/workflows/ci-analysis-jobs.yml) | All Actions workflow files, expressions, action inputs, and embedded Bash scripts through actionlint and ShellCheck | Lint findings fail the job. |
| [CI-CodeQL / codeql-csharp](../.github/workflows/ci-codeql.yml) | C# security dataflow with the `security-extended` query suite | Extraction, build or analysis failures fail the job; security findings are reported through GitHub's separate code-scanning results check. |
| [CI-PR](../.github/workflows/ci-pr.yml) | Existing .NET/Godot compatibility and native progress-helper tests | Build, runtime test and smoke-test failures fail the relevant matrix job. |

CodeQL also runs weekly on Monday at 05:23 UTC and supports manual dispatch.
The analysis jobs live in the reusable CI-Analysis-Jobs workflow. CI-Analysis calls it for `main` and manual dispatch;
for pull requests the CI-PR workflow calls it (as its `analysis` job), so its summary becomes part of the PR's single CI
comment. CI-Analysis itself must not get a `workflow_call` trigger: GitHub then finds none of its push runs, and the
README badge shows "no status". The PR packaging/title workflows remain PR-only, and the release workflow remains
tag-triggered.

## Analyzer policy and reports

[src/Directory.Build.props](../src/Directory.Build.props) enables the SDK's
`8.0-recommended` rules for the three projects under `src/` (framework, progress
helper and its tests), including local builds. `tools/ci-reports` is outside it.
The dedicated analysis and CodeQL jobs use SDK 10.0.401. Other test jobs retain
the .NET 8/10 matrix. Update the dedicated SDK version in `ci-analysis-jobs.yml`
and `ci-codeql.yml` deliberately and review the changed diagnostics.

The Godot project must be built with `--configuration Debug`: it defines `TOOLS`,
which includes the editor framework. Export configurations omit that code.
The helper projects are built explicitly because they are outside the Godot
solution; the framework's best-effort helper publish is not an analysis gate.

Setting `CIStaticAnalysis=true` writes SARIF 2.1 reports to
`artifacts/static-analysis/`, with one filename per project. CI preserves the
reports as the `managed-analysis` artifact for 14 days, including reports from
failed builds. These files are gitignored. The reporting script also merges owned
source findings into `github.sarif`, removing suppressed, generated, example and
vendored diagnostics and normalizing locations to repository-relative paths.
The filtered report is uploaded to GitHub code scanning with the `dotnet` category,
separate from CodeQL. Raw reports remain available for full inspection.

## Where results are visible

| View | What appears |
| --- | --- |
| PR conversation | The *Static analysis* section of the PR's single CI comment, updated on each run, with audit/build status, per-project error/warning/note counts and frequent rules. |
| PR checks and source annotations | Native job status checks, up to ten immediate owned-source diagnostic annotations, and code-scanning annotations for changed lines. |
| Actions run summary | The same numeric analysis table and a workflow-lint status summary, including when a build or audit fails. |
| Repository Security / code scanning | Owned-source `.NET SDK analyzers` findings and separate CodeQL alerts, tracked across commits once scans run on `main`. |
| Artifacts and API consumers | Raw SARIF, filtered `github.sarif`, `summary.md`, and `metrics.json` with per-project counts, totals, per-rule counts, excluded counts, and report completeness. |
| README | Static-analysis and CodeQL badges show the latest `main` workflow status. |

Counts are current totals, not a computed delta against `main`. Notes are counted
separately from warnings. Missing reports say **not produced**, rather than zero;
incomplete sets are not uploaded, to avoid incorrectly resolving earlier alerts
from projects that were not analyzed. Code-scanning annotations compare findings
using GitHub's own tracking, with upload-sarif generating fingerprints.

Fork PRs retain checks, annotations and run summaries; their read-only tokens
cannot write the conversation comment. The comment is limited to same-repository
PRs and uses the workflow's GitHub token, without additional service secrets.
CI-PR's `pr-comment` job writes it with `tools/ci-reports comment` from the reports
the jobs upload as `pr-comment-*` artifacts.
Custom numeric trends are not automatically added to GitHub's general Insights
charts; the JSON artifact provides those values for a future dashboard.

The reporting tests run in CI and can be run locally with
`dotnet test tools/ci-reports.Tests/CiReports.Tests.csproj`. To render existing local SARIF reports,
run `dotnet run --project tools/ci-reports -- analysis` from the repository root. In Actions, this
also writes the job summary and annotations.

Recommended analyzer warnings are not globally treated as errors. The initial
Windows/SDK 10 baseline contained 240 Godot-project warnings (64 framework,
171 tests, 5 samples) and 42 helper-project warnings. Review and fix useful
correctness findings before promoting individual rules to errors in
`.editorconfig`, scoped to owned files. Leave samples and vendored gdUnit4 intact.
Godot lifecycle conventions and native interop need specific judgment.

Whitespace/style enforcement and additional Sonar analyzers remain follow-up
work. `.editorconfig` specifies UTF-8 and turns off CA1050 and CA1707 for the
sample plugins; it has no formatting rules, and no formatting rewrite is part
of these checks.

## Run the checks locally

Run from the repository root with the .NET SDK installed:

```sh
dotnet restore src/eplugin-framework/EPluginFramework.sln --force \
  -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high \
  -warnaserror:NU1900,NU1903,NU1904
dotnet restore src/activation-progress.Tests/ActivationProgress.Tests.csproj --force \
  -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high \
  -warnaserror:NU1900,NU1903,NU1904

mkdir -p artifacts/static-analysis
dotnet build src/eplugin-framework/EPluginFramework.sln \
  --configuration Debug --no-restore --no-incremental -p:CIStaticAnalysis=true
dotnet build src/activation-progress.Tests/ActivationProgress.Tests.csproj \
  --configuration Debug --no-restore --no-incremental -p:CIStaticAnalysis=true
```

PowerShell users should put each command on one line or replace Bash's `\`
continuations with backticks, and create the report directory with
`New-Item -ItemType Directory -Path artifacts/static-analysis -Force`.
Static analysis requires the Godot NuGet SDK, but no running Godot editor,
GTK or Xvfb. Existing framework runtime tests still require `GODOT_BIN`.

For workflows, install actionlint 1.7.11 plus ShellCheck, then run
`actionlint` from the repository root. On a Linux Docker host, the exact pinned
CI tool image can also be run locally:

```sh
docker run --rm -v "$PWD:/repo:ro" -w /repo \
  rhysd/actionlint@sha256:6f03470d0152251d7f07f7c4dc019dbe7024c72cd952f839544c7798843efa8f
```

## GitHub configuration

The workflow files produce status checks; repository rules decide which ones
must pass before merging. Require `managed-analysis` and `workflow-lint`
alongside the existing test checks.
For CodeQL, require successful `codeql-csharp` execution **and** the CodeQL
results gate for high/critical security findings and error-level findings.
A successful analyzer execution can still have security alerts.
See [GitHub's code-scanning result checks](https://docs.github.com/en/code-security/how-tos/manage-security-alerts/manage-code-scanning-alerts/triage-alerts-in-pull-requests).

Use advanced CodeQL setup for this workflow, and disable any competing default
setup before enabling it. Code scanning is available for public repositories;
private organization repositories require the relevant GitHub Code Security
entitlement. Validate a repository PR and a fork PR, and inspect extraction
coverage for `addons/ePlugin` and the progress helper before requiring the gate.
Weekly scans begin once the workflow reaches the default branch.

The workflows use `pull_request` without service secrets or privileged fork-code
execution. They have no path filters, so documentation-only PRs also finish
the checks. CI-Analysis-Jobs and CI-CodeQL pin their actions by commit and the
lint image by digest; the other workflows reference actions by version tag.
Dependabot updates Actions and the three directories with NuGet dependencies
(`src/eplugin-framework`, `src/activation-progress.Tests`, `tools/ci-reports.Tests`)
weekly.
