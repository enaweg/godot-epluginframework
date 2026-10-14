# Static analysis for pull requests and main

Status: the initial rollout is implemented. See [Static analysis in CI](../static-analysis.md)
for the installed checks, local commands and GitHub configuration. The examples
below preserve the original proposal; the workflow files are authoritative.

## Recommendation

Use the .NET SDK's Roslyn analyzers for C# correctness, CodeQL for security
dataflow analysis, and actionlint for GitHub Actions workflows. Add NuGet Audit
and NuGet Dependabot updates alongside these checks. Start with a small set of
blocking rules and expand it after reviewing the existing findings.

Here, "GitHub agents" means GitHub-hosted Actions runners. The same .NET and
actionlint commands can also be run by coding agents before opening a PR.

This approach needs no separate analysis server or service token. Code scanning
is available for public repositories; private organization repositories require
an eligible plan and GitHub Code Security. The repository is publicly readable.
See [GitHub code-scanning availability](https://docs.github.com/en/code-security/concepts/code-scanning/code-scanning).

## Original repository and measured baseline

- `ci-pr.yml` builds and tests pull requests with .NET 8/10, checks Godot
  compatibility, and tests the native progress helper on multiple operating systems.
- `ci-release.yml` tests releases triggered by tags. There is no workflow triggered
  by pushes to `main`.
- The framework targets `net8.0`, uses Godot.NET.Sdk 4.7.2 and nullable annotations,
  and already includes `gdUnit4.analyzers`. SDK analyzers are already enabled,
  with an evaluated `AnalysisLevel` of `8.0`.
- `.editorconfig` specifies only UTF-8. Dependabot currently updates Actions only.
- `EPluginFramework.sln` contains the Godot project, not the separate progress-helper
  projects. Analyze those explicitly too.
- Most framework code is behind `#if TOOLS`. Evaluating MSBuild confirmed that
  `Debug` defines `TOOLS`, while `ExportRelease` does not. Use `Debug` for analysis.

Local checks on 2026-10-14, Windows, SDK 10.0.401:

| Check | Result |
| --- | --- |
| Godot solution rebuild with `AnalysisLevel=8.0-recommended` | Passed; 240 emitted warnings: 64 in framework code, 171 in tests, 5 in samples |
| Progress-helper test project rebuild with the same level, including its project reference | Passed; 42 emitted warnings |
| NuGet restore audit, direct and transitive dependencies, high/critical failures enabled | Passed for both the solution and helper test project |
| actionlint 1.7.11 on current workflows | Passed; ShellCheck and pyflakes integrations disabled for this local check |
| `dotnet format whitespace` on framework and tests with the current configuration | Passed; no changes needed |

The largest Godot warning group was CA1822 (129 suggestions to make members
static). Other findings include disposal (CA1001), ignored return values
(CA1806), argument exceptions (CA2208), culture handling, and performance advice.
These require triage: Godot lifecycle methods and native interop do not always
benefit from generic analyzer suggestions. No framework or example code was changed.

Count emitted diagnostics, not every SARIF result: the compiler report also
contains source-suppressed warnings from Godot-generated code. CodeQL has not been
run locally, and the proposed workflows have not been exercised on GitHub.

## Tool comparison

| Tool | Runner setup and feedback | Fit for this repository |
| --- | --- | --- |
| [.NET SDK analyzers](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview) | Already part of `dotnet build`; compiler diagnostics, IDE feedback, optional SARIF | First choice for correctness, disposal, APIs and performance. Configure a reviewed rule set; avoid making all existing warnings fatal. |
| [`dotnet format`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format) | Included with SDK; `--verify-no-changes` checks without editing | Useful for explicit whitespace/style policy. Its analyzer subcommand checks fixable diagnostics and does not replace a full analyzer build. |
| [CodeQL](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/manage-your-configuration/codeql-for-compiled-languages) | GitHub-maintained Actions; alerts, PR annotations and security dataflow | Recommended security layer. Use advanced setup with a manual Debug build to control Godot symbols, generated code and project coverage. |
| [SonarAnalyzer.CSharp](https://github.com/SonarSource/sonar-dotnet) | NuGet Roslyn analyzer; runs during builds and in supported IDEs | Good optional addition for bug patterns and maintainability. Can run without SonarQube Cloud; pin the package and use `PrivateAssets=all`. Pilot after the SDK baseline is understood. |
| [SonarQube Cloud](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/ci-based-analysis/github-actions-for-sonarcloud) | Service project, scanner setup, token, dashboard and quality gates | Consider if centralized trends, duplication and new-code quality gates become requirements. CI analysis needs additional handling for fork PRs because the token is unavailable there. More setup than the proposed stack. |
| [Semgrep](https://docs.semgrep.dev/supported-languages) | CLI/container; C# support and custom rules | Useful for a future project-specific pattern check. Product/engine capabilities differ; advanced cross-file analysis and Pro rules should not be assumed available in the community CLI. Additional rule maintenance is unnecessary for the first rollout. |
| [actionlint](https://github.com/rhysd/actionlint) | Small CLI or official container; expression, workflow and action-input checks, optional ShellCheck | Add immediately for `.github/workflows/*.yml`. Run ShellCheck on Linux before requiring the expanded check. |

NuGet Audit is dependency vulnerability analysis, rather than source analysis.
It complements these tools and is already available during restore. Explicitly
set `NuGetAuditMode=all`: targeting `net8.0` does not get the newer framework's
transitive-audit default merely by installing SDK 10.
See [NuGet Audit](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)
and [severity codes](https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1901-nu1904).

## Proposed implementation

### Shared policy

Add `src/Directory.Build.props` so all three source projects get the same analyzer
configuration. This file is outside the shipped addon and does not impose policy
on projects consuming ePlugin. Keep the test/compatibility SDK matrix; do not add
a root `global.json` that forces every job onto one SDK.

```xml
<Project>
  <PropertyGroup>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <RunAnalyzersDuringBuild>true</RunAnalyzersDuringBuild>
    <AnalysisLevel>8.0-recommended</AnalysisLevel>
  </PropertyGroup>
  <PropertyGroup Condition="'$(CIStaticAnalysis)' == 'true'">
    <ErrorLog>$(MSBuildThisFileDirectory)../artifacts/static-analysis/$(MSBuildProjectName).sarif,version=2.1</ErrorLog>
  </PropertyGroup>
</Project>
```

The dedicated analysis job should select SDK 10.0.401 initially, matching the
measured baseline. Upgrade it deliberately and review rule changes. Pinning
`AnalysisLevel` alone does not pin the analyzer implementation across SDK versions.

Use `.editorconfig` entries for individual rule IDs and owned source paths.
Initially keep the recommended rules as warnings. After reviewing and fixing
existing occurrences, promote selected correctness rules to `error`, for example:

```ini
[src/eplugin-framework/addons/ePlugin/**/*.cs]
dotnet_diagnostic.CA2208.severity = error

[src/activation-progress/**/*.cs]
dotnet_diagnostic.CA2208.severity = error
```

CA2208 is an illustrative first candidate, not a validated zero-warning gate.
Do not enable these error entries until its baseline is resolved. Keep tests
under analysis, with their own rule severities where appropriate. Samples and
vendored gdUnit4 remain build inputs, but are outside the owned-code style gate.
Avoid global `TreatWarningsAsErrors`, broad suppressions, and marking handwritten
samples as generated code just to silence analysis.

### PR and main quality workflow

Add `.github/workflows/ci-analysis.yml`. Use one Ubuntu job per tool group rather
than repeating static analysis across the existing OS/Godot matrix. Run on PRs
targeting `main`, pushes to `main`, and manual dispatch. If PRs to other branches
also need the checks, remove the PR branch filter.

The following skeleton assumes the shared props file above. Action major tags
are shown for readability; resolve Actions to reviewed commit SHAs when installing
the workflow and keep their versions updated with Dependabot.

```yaml
name: CI-Analysis
on:
  pull_request:
    branches: [main]
  push:
    branches: [main]
  workflow_dispatch:
permissions:
  contents: read
concurrency:
  group: analysis-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  managed-analysis:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: true
      DOTNET_NOLOGO: true
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.401
      - name: Restore and audit
        run: |
          dotnet restore src/eplugin-framework/EPluginFramework.sln --force \
            -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high \
            -warnaserror:NU1900,NU1903,NU1904
          dotnet restore src/activation-progress.Tests/ActivationProgress.Tests.csproj --force \
            -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high \
            -warnaserror:NU1900,NU1903,NU1904
      - name: Analyze all managed projects
        run: |
          mkdir -p artifacts/static-analysis
          dotnet build src/eplugin-framework/EPluginFramework.sln \
            --configuration Debug --no-restore --no-incremental -p:CIStaticAnalysis=true
          dotnet build src/activation-progress.Tests/ActivationProgress.Tests.csproj \
            --configuration Debug --no-restore --no-incremental -p:CIStaticAnalysis=true
      - name: Preserve compiler and analyzer reports
        if: always()
        uses: actions/upload-artifact@v7
        with:
          name: managed-analysis
          path: artifacts/static-analysis/*.sarif
          if-no-files-found: warn
          retention-days: 14
  workflow-lint:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
      - name: Check workflows and embedded shell scripts
        uses: docker://rhysd/actionlint:1.7.11
```

The framework build already has a target that publishes the progress helper.
That nested publish is best-effort and does not substitute for the explicit
helper/test-project analysis build. Static analysis needs the Godot NuGet SDK,
but no Godot editor executable, Xvfb, GTK installation or running tests.

NU1903/NU1904 make high/critical dependency vulnerabilities fatal; NU1900 makes
audit-feed failures visible as failures instead of silently passing the audit.
Review any other restore/audit infrastructure warnings too. Auditing all
dependencies can flag test-only dependencies; triage rather than suppressing
the entire audit. Full low/moderate reporting can be added to the scheduled scan.

Initially archive Roslyn reports as artifacts. SARIF upload into GitHub code
scanning is a later option: first filter source-suppressed/generated diagnostics
and confirm supported SARIF output. Each project must keep its own report; a
single shared `ErrorLog` filename would overwrite results.

Add whitespace verification once explicit indentation/newline rules are agreed
in `.editorconfig`. Run from each project's directory; check only owned files:

```sh
# Working directory: src/eplugin-framework
dotnet format whitespace EPluginFramework.sln --no-restore --verify-no-changes --include addons/ePlugin tests
# Working directory: src/activation-progress
dotnet format whitespace ActivationProgress.csproj --no-restore --verify-no-changes
# Working directory: src/activation-progress.Tests
dotnet format whitespace ActivationProgress.Tests.csproj --no-restore --verify-no-changes
```

The current charset-only check passed, but that does not establish a complete
formatting policy. Do not introduce a broad StyleCop rule set or automatically
reformat the examples/vendor tree as part of installing analysis.

### CodeQL security workflow

Add a separate `.github/workflows/ci-codeql.yml`, using advanced setup rather
than a second default-setup scan. Trigger it on PRs to `main`, pushes to `main`,
manual dispatch and a weekly schedule. The weekly run updates findings even
when no code changes. A workflow must reach the default branch before its
schedule runs.

```yaml
name: CI-CodeQL
on:
  pull_request:
    branches: [main]
  push:
    branches: [main]
  schedule:
    - cron: '23 5 * * 1'
  workflow_dispatch:
permissions:
  contents: read
concurrency:
  group: codeql-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  codeql-csharp:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    permissions:
      contents: read
      security-events: write
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.401
      - uses: github/codeql-action/init@v4
        with:
          languages: csharp
          build-mode: manual
          queries: security-extended
      - name: Build editor code and progress-helper projects under tracing
        run: |
          dotnet build src/eplugin-framework/EPluginFramework.sln \
            --configuration Debug --no-incremental
          dotnet build src/activation-progress.Tests/ActivationProgress.Tests.csproj \
            --configuration Debug --no-incremental
      - uses: github/codeql-action/analyze@v4
        with:
          category: /language:csharp
```

Manual extraction analyzes compiled source, so initialize CodeQL before builds,
force compilation, and explicitly cover the projects outside the solution. It
also avoids guessing whether no-build extraction reproduces the Godot symbols
and generated sources. This is a repository-specific recommendation, not a
claim that C# requires a build for CodeQL: GitHub supports no-build C# analysis.
See [C# build modes](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages).

Do not assume CodeQL `paths-ignore` removes files from a manual compiled C#
database. Compile the normal editor project and triage findings from its tests,
examples and generated code. Validate extraction logs show framework code under
`addons/ePlugin` and the progress helper, not just generated/test code.

Use `pull_request`, allowing GitHub's code-scanning handling of fork PR uploads;
do not switch to `pull_request_target` to execute PR code with privileges. Check
the repository's fork-workflow settings and test a fork PR before requiring it.
No Sonar/service secrets are needed. Package/process/filesystem paths are
particularly relevant here, but static analyzers cannot validate Godot's native
lifetime or assembly-reload behavior; keep the existing runtime tests.

Require both successful CodeQL execution and GitHub's CodeQL results/ruleset
gate for new high/critical security alerts and error-level findings. An analysis
job can succeed while producing security alerts; requiring only that job is
insufficient. See [GitHub's PR result checks](https://docs.github.com/en/code-security/how-tos/manage-security-alerts/manage-code-scanning-alerts/triage-alerts-in-pull-requests).

### Main tests and dependency maintenance

Extend `ci-pr.yml` to run its existing jobs on `push: branches: [main]` as well
as `pull_request`; consider renaming it to `CI` so the name reflects both events.
Leave `ci-pr-title.yml` and `ci-pr-build.yml` as PR-only workflows, and keep the
release workflow tag-triggered. Static analysis adds to the existing runtime
tests and compatibility checks.

Add weekly NuGet entries to `.github/dependabot.yml` for
`/src/eplugin-framework` and `/src/activation-progress.Tests`, alongside the
existing Actions entry. The helper has no PackageReferences today; add its
directory if that changes. Group related test packages where upgrades need to
stay compatible, and review prerelease gdUnit packages deliberately.

## Rollout and acceptance

1. Add shared analyzer configuration, report artifacts and PR/main triggers.
   Analyzer warnings remain informational; failed builds and high/critical
   audits fail. Check Linux actionlint with ShellCheck before making it required.
2. Add and validate advanced CodeQL on both a repository PR and a fork PR. Check
   actual source coverage and establish the `main` baseline. Require its security
   results gate after triaging existing alerts.
3. Review the 64 owned-framework warnings and helper findings. Fix correctness
   issues in separate changes; apply narrow, explained exceptions for Godot and
   native interop. Promote only resolved, valuable rule IDs to errors. Expand
   test rules separately. No blanket warnings-as-errors rollout.
4. Add explicit whitespace policy and its gate. Pilot SonarAnalyzer.CSharp only
   if the team wants additional maintainability checks after this baseline.

Acceptance checks for the implementation:

- PR and `main` builds analyze all three managed projects with `TOOLS` active
  in the Godot Debug build; distinct reports survive a failed analysis build.
- A deliberate owned-code violation of an enabled error rule fails CI; ordinary
  baseline warnings continue to report without blocking unrelated PRs.
- A malformed workflow/expression fails actionlint; a high/critical vulnerable
  package fails restore auditing, including when introduced transitively.
- CodeQL reports framework/helper source coverage, posts PR results, and runs
  on `main` and schedule. A security finding exercises the results gate, not
  merely the analyzer-execution check.
- Fork PRs run with the expected token restrictions, and required checks finish
  on documentation-only PRs. Avoid workflow-level path filters that leave
  required status checks pending.
- Existing .NET/Godot/native-helper test matrices remain operational; examples,
  vendored dependencies and the public plugin API are unchanged by policy setup.

These are implementation acceptance checks, not claims of tests already run.
