# AGENTS.md

## Project overview

ePlugin Framework is a C# editor-plugin framework for Godot 4.4-4.7, targeting `net8.0`.
The Godot project and solution are under `src/eplugin-framework/`.

## Repository guidance

- Edit framework code under `src/eplugin-framework/addons/ePlugin/`.
- Treat `addons/sample_plugin/`, `addons/sample_dependant_plugin/`,
  `addons/sample_addedcode_plugin/`, and `addons/sample_optional_plugin/` as read-only usage
  examples unless explicitly asked to change them.
- Treat `addons/gdUnit4/` as a read-only vendored dependency unless explicitly asked to change it.
- Tests are under `src/eplugin-framework/tests/`.
- Most framework code is wrapped in `#if TOOLS` because it runs only in the Godot editor.

## Build and test

```text
dotnet build "src/eplugin-framework/EPluginFramework.sln"
dotnet test "src/eplugin-framework/EPluginFramework.sln" --settings "src/eplugin-framework/.runsettings"
dotnet test "src/activation-progress.Tests/ActivationProgress.Tests.csproj"
```

The framework tests require `GODOT_BIN` to point to a Godot .NET/Mono editor executable; the progress helper's tests
need only .NET. To run one test:

```text
dotnet test "src/eplugin-framework/EPluginFramework.sln" --settings "src/eplugin-framework/.runsettings" --filter "FullyQualifiedName~IDotnetCliTests.VersionTest"
```

Line coverage of `addons/ePlugin` (Cobertura report; needs `dotnet-coverage` on `PATH` and a Debug build first, see
`docs/code-coverage.md`), and the CI reports' tests (no Godot needed):

```text
dotnet run tools/coverage.cs -- coverage.cobertura.xml
dotnet test "tools/ci-reports.Tests/CiReports.Tests.csproj"
```

Alternative test runner: gdUnit4's `src/eplugin-framework/addons/gdUnit4/runtest.cmd` / `runtest.sh`, which takes
`--godot_binary <path>` or `GODOT_BIN`.

## Tooling and CI

- Repository tooling is C# on .NET 10, not Python or shell scripts: single-file apps in `tools/`
  (`dotnet run tools/<name>.cs -- <args>`: `coverage.cs`, `progress_smoke.cs`, `package_release.cs`,
  `screenshots/capture.cs`, and the known-plugins generators `build_godot_addons.cs` and `generate_known_plugins.cs`)
  and the CI reports project `tools/ci-reports`.
- Documentation screenshots (`docs/images`) are regenerated with `dotnet run tools/screenshots/capture.cs` (needs a
  desktop session with X11 or Xwayland and `GODOT_BIN`). Add new dialogs to `tools/screenshots/DocScreenshots.cs`.
- A pull request gets one CI comment, which CI-PR's `pr-comment` job composes from the `pr-comment-*` artifacts. Don't
  post further comments; add a section instead. Keep CI-Analysis free of `workflow_call`, or its README badge shows no
  status.

## Architecture notes

- `EPluginPlugin` bootstraps the framework and reinitializes state after Godot C# assembly reloads.
- `EGlobal` owns plugin contexts, lifecycle state, dependency resolution, and recipe installation.
- Consumer plugins implement `IEEditorPlugin.CreateRecipe` and call `EnableEPlugin()` /
  `DisableEPlugin()` from their Godot lifecycle methods.
- The builder surface is split: `IEEditorPluginRecipeBuilder` declares resources, `IEEditorPluginBuilder`
  extends it with dependency declarations. Only the root recipe declares dependencies; the nested
  recipe of an optional dependency gets the resource-only interface.
- `AddOptionalPluginDependency` recipes are installed only while the named plugin is enabled and its
  version matches. `EGlobal` re-evaluates them in both directions (enabling a plugin applies newly
  satisfied recipes, disabling one reverses the recipes that depended on it), so activation order does
  not matter. They never enable a plugin, never fail an activation, and never cascade a disable.
- Recipe changes should go through the `IDotnetCli` abstraction; new CLI operations must be
  implemented for both .NET 9 and .NET 10 CLI implementations (`DotnetCli9`, `DotnetCli10`), with arguments checked
  in `IDotnetCliTests`. Pass arguments one per element and unquoted, and project paths through `ProjectFile`.
- Read `docs/internals/update-system.md` before changing the update system under `Internal/Update/`.
- `EGlobal` reaches the editor only through `IEditorHost`; lifecycle tests use `tests/LifecycleFixture.cs` with a fake
  editor. Tests use `internal` members, not reflection.
- Keep `IEEditorPlugin` as an interface; this works around Godot 4.5+ `EditorPlugin` regressions.

## Change conventions

- Keep recipe creation deterministic and side-effect free.
- Preserve existing public API and plugin lifecycle behavior unless the task explicitly changes it.
- Follow `.editorconfig` and existing C# style.
- Static analysis uses SDK analyzers during builds and `actionlint` for workflows. See `docs/static-analysis.md` for local commands and CI policy.

## Commit and PR messages

Release notes are generated from **PR titles** (`.github/workflows/ci-release.yml`), sorted by their
Conventional Commits prefix, and `ci-pr-title.yml` fails PRs without one. See "Commit and PR
messages" in `CLAUDE.md` for the details.

- PR titles and commit subjects: `type(scope)!: description`, with a lowercase type from `build`, `chore`,
  `ci`, `docs`, `feat`, `fix`, `perf`, `refactor`, `revert`, `style`, `test`. Scope and `!` (breaking)
  are optional; a scope may only contain letters, digits, `_`, `-` and `.`. Write exactly one space after the colon,
  and start the description with a letter or digit.
- `feat:` goes to Feature, `fix:` to Fix, and everything else, including titles without a prefix, to Other. A PR label
  `feat`/`feature` or `fix`/`bug` does the same, and `ignore` leaves the PR out.
- A PR is one release-notes entry under one prefix, so don't mix unrelated features and fixes in one PR. Map other
  types: tooling to `chore:` or `build:`, workflows to `ci:`, docs to `docs:`, dependency bumps to `chore(deps):`.
- Write a short imperative description in lowercase, with no trailing period, e.g.
  `fix: keep the ePlugin Manager open after a successful update`.
- Keep GitHub's default merge-commit message. Direct pushes to `main` need a prefix too
  (e.g. `chore: update version for next release`).
