# Code coverage

The gdUnit4 tests of the [CI-PR](../.github/workflows/ci-pr.yml) workflow measure the line coverage of the framework
(`src/eplugin-framework/addons/ePlugin`). Tests, sample plugins, gdUnit4 and generated code are compiled into the same
assembly but are not counted. Both SDK runs (`dotnet8`, `dotnet10`) are measured and merged, because each one runs a
different dotnet CLI implementation (`DotnetCli9`, `DotnetCli10`). A line counts as covered when either run executed it.

No external service is involved; everything stays in GitHub.

## Where results are visible

| View | What appears |
| --- | --- |
| PR conversation | The *Code coverage* section of the PR's single CI comment, updated by each run: line coverage of the PR and of the latest `main`, the change in percentage points, coverage of the lines the PR changes with links to the uncovered ones, files whose coverage changed, and all files. |
| Actions run summary | The same summary, also for pushes to `main` and for fork PRs. |
| `coverage-report` artifact | ReportGenerator's HTML report, to browse covered and uncovered lines file by file (14 days). |
| [`coverage` branch](https://github.com/enaweg/godot-epluginframework/tree/coverage) | The history of `main`: a README with a trend chart of the last 30 pushes and the latest per-file coverage, all measurements in `history.csv`, the latest measurement in `latest.json` (what PRs compare against) and `badge.svg`. |
| README | The coverage badge of the latest `main` measurement. |

The `coverage-history` job writes the `coverage` branch after each push to `main` whose tests passed. It is the only job
with `contents: write`, and it never runs for pull requests. The branch is generated; do not edit it. Without a
`coverage` branch, a PR's comment says there is nothing to compare with.

Coverage does not fail a check. It is reported, not enforced.

## How it is measured

gdUnit4 runs the tests inside a Godot process, and Godot loads the project assembly from a memory stream. Profiler-based
collectors (`dotnet test --collect "Code Coverage"`, coverlet) only see assemblies loaded from a file. They report Godot's
own assemblies but nothing of the framework. [tools/coverage.cs](../tools/coverage.cs) therefore:

1. instruments the built `EPluginFramework.dll` on disk with `dotnet-coverage instrument` (filtered by
   [coverage.settings.xml](../src/eplugin-framework/coverage.settings.xml)),
2. starts a `dotnet-coverage collect` server session that the instrumented code in the Godot process reports to,
3. runs `dotnet test EPluginFramework.sln --configuration Debug --no-build --settings .runsettings` (plus any further
   arguments) and writes a Cobertura report when the session shuts down,
4. restores the original assembly, so an incremental build doesn't keep the instrumented one.

[tools/ci-reports](../tools/ci-reports/CoverageReport.cs) merges the reports and writes the comment section and the branch
contents. It counts each source line once per file, so its totals differ slightly from the HTML report, which counts a
line once per class. Its tests run with the other reporting tests
(`dotnet test tools/ci-reports.Tests/CiReports.Tests.csproj`).

Only line coverage is reported. Static instrumentation records no branch coverage.

## Running it locally

```bash
dotnet tool install --global dotnet-coverage
dotnet build src/eplugin-framework/EPluginFramework.sln --configuration Debug
dotnet run tools/coverage.cs -- coverage.cobertura.xml    # further arguments go to dotnet test, e.g. --filter ...
dotnet run --project tools/ci-reports -- coverage summary --report coverage.cobertura.xml --output-dir coverage
```

Both tools need the .NET 10 SDK. coverage.cs needs `GODOT_BIN` like `dotnet test`, runs the tests with the `dotnet`
on `PATH`, and exits with the exit code of `dotnet test`. `coverage/summary.md` is the coverage section a PR's comment would get. For an HTML report, run ReportGenerator
(`dotnet tool install --global dotnet-reportgenerator-globaltool`) on the Cobertura file.
