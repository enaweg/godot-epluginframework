# Plan: keep plugin updates that only break project code

Status: implemented (2026-10-16); the maintained description is the "Build failures in project code" section of
[Update system internals](update-system.md), the user-facing one in [Updating plugins](../updating-plugins.md#updates-that-only-break-the-projects-code).
Owner: update system (`Internal/Update/`, `EGlobal.UpdateApply.cs`).
Decided: project code is isolated with an MSBuild compile filter, not by renaming directories.

Where the implementation differs from the plan below:

- No dialog after an *automatic* keep: closing the failure dialog means rollback. The status line says it was kept, and
  the plugin's details show a **Kept update** section with the project errors; the Needs attention dialog rolls it back.
- S1 found that a Rebuild resolves project references while cleaning, so the filter target runs before
  `BeforeRebuild;BeforeBuild;AssignProjectConfiguration`, not only `BeforeBuild`. Property values on the command line are
  MSBuild-escaped (an apostrophe in the path broke item transforms), `OutDir` has no trailing separator (a quoted
  `...\"` escapes its quote on Windows), and errors-only logs of parallel builds prefix lines with the node (`1:7>`).
- Names: the causes are `ProjectCode`/`PluginCode` (not `ProjectOnly`/`Plugin`/`Project`); the probe is the
  *plugins-only build*: `ICheckedDotnetCli.TryBuildPluginsOnly(PluginsOnlyBuild)` and `IUpdateHost.BuildPluginsOnly`
  (not `TryProbeBuild(ProbeBuild)`/`IUpdateHost.ProbeBuild`), run from `UpdateApplier.BuildPasses` (not
  `BuildFailureFallback`) and recorded in `journal.Builds` as `<name>-plugins-only`, e.g. `interim-plugins-only` (not
  `probe-interim`), with its errors in `build-<name>-plugins-only.errors.log`.
- The classifier reads the failed build's own output; only the plugins-only build writes an errors-only file log.
  `CS2012` is transient too.
- Not done: the review-step warning while another update is kept (corner case 26), and the group baseline follow-up.

## Problem

A plugin release can change its API in a way that breaks the *project's* own C# code (the game scripts calling the
plugin), while the plugin itself and every other addon still compile. Today the update's build fails, and the update
is rolled back: the plugin never gets updated, although nothing is wrong with it. The user cannot even get at the new
version to adapt their code, unless they started the update from the plugin's details and pick **Keep new version** in
the failure dialog (and only if they understand the errors are in their own code).

Goal: when the update fails to build **only because of project code**, keep the new plugin version automatically (the
project then has to be adapted, exactly like after **Keep new version** today). When plugin code fails, roll back as
today.

## What the code does today

- `UpdateApplier.Apply` builds after the swap (`interim` for managed plugins, `final` for a plain C# batch). The interim
  build compiles the new plugin code **and the project code** with the old recipes. `Resume` builds again (`final`)
  after reconciling recipes. Both go through `IUpdateHost.Build` → `ICheckedDotnetCli.TryBuild` =
  `dotnet build <sln> --no-incremental`.
- On failure, `UpdateFailureDecision.Choose/Policy` picks `rollback`, `keep` or `ask`:
  - self-updates, headless runs and grouped batches always roll back;
  - `ask` asks only for an update started from a plugin's details (`AskOnBuildFailure`); the batch **Update** rolls back.
- `Keep` already exists and does what we need as the end state: marker `update_kept_build_failed`, journal
  `CommittedWithErrors`, `BeforeReconcile` when the interim build failed. **Retry** under **Needs attention** builds;
  once the project compiles it resumes (`ResumeAfterRestart`: restart into the new code, reconcile, final build, acknowledge).
- There is no way to roll back a kept update afterwards from the UI (Retry with a failing build just stays kept).
- **A failed build deletes the editor's assembly** (S2, below). Today's Keep therefore only works until the next editor
  restart. This has to be fixed first, otherwise automatic keeping makes it much more common.

So the feature is: fix the lost assembly, a **classifier** that turns "build failed" into "plugin failure" vs
"project failure", a policy change that keeps project failures, and UI for the kept state.

## S2 result: a failed `--no-incremental` build removes the assembly

Tested 2026-10-16 with a minimal Godot 4.7.2 C# project (`Godot.NET.Sdk/4.7.2`, `net8.0`), a C# editor plugin under
`addons/plug` and project code calling its API. The plugin API was changed so that only the project code failed
(`CS0117` in `Main.cs`), as an update would:

| Build | `.godot/mono/temp/bin/Debug` afterwards |
|---|---|
| `dotnet build S2Test.slnx --no-incremental`, SDK 10.0.401 (what `DotnetCli10.TryBuild` runs) | **empty** (dll, pdb, deps, runtimeconfig gone) |
| `dotnet build S2Test.csproj --no-incremental`, SDK 8.0.425 (the `DotnetCli9` path) | **empty**, including `GodotSharp*.dll` |
| `dotnet build S2Test.slnx` (incremental, what Godot's Build button does) | unchanged, old assembly kept |

`--no-incremental` is `Rebuild` = `Clean;Build`: Clean deletes the outputs before the compiler fails. In `obj/Debug`
the dll is gone too.

Editor restart in that state (`--headless --editor`, Godot 4.7.2): no error, no build attempt, the plugin's
`_EnterTree` never runs; the plugin stays listed as enabled in `project.godot`. For ePlugin this means after a restart
while an update is kept (or after any failed activation build, `EGlobal.cs` uses the same `TryRebuildSolution`):
no framework, no ePlugin Manager, no **Retry**, no recovery, silently. The running editor is not affected,
because it loaded the assembly into memory.

Also seen: under the German SDK, diagnostic lines keep the English `error CS0117:` keyword and only the message is
localized (`"Plug" enthält keine Definition für "Api"`). This mostly answers S4.

### Fix (first PR)

In `DotnetCliBase` (shared by both CLIs, so activations and updates get it): before a non-incremental build, copy the
Godot project's output folder (`.godot/mono/temp/bin/<configuration>`, from MSBuild's `OutDir`, not a hard-coded
path) to a temporary snapshot. If the build fails and the project's assembly is missing afterwards, restore the snapshot
with the original timestamps, so neither GodotTools' `HotReloadAssemblyWatcher` nor `AssemblyReload.IsPending` sees a
new build. On success, delete the snapshot. Log the restore. The non-incremental guarantee is kept: nothing compiled
reuses old outputs, only the folder the editor loads from survives a failure.

Then a restart while kept loads the pre-update assembly (old plugin code, new files on disk). That is the state the
existing `Keep`/`BeforeReconcile` design already assumes.

## Proposed flow

Applies to the interim build, the plain-C# final build in `Apply`, and the final build in `Resume`; not to rollback and
Retry builds:

0. **Normal install** (unchanged). Build succeeds → continue as today.
1. **Classify the failed build** from its diagnostics (cheap, no build). Errors located only in project files →
   step 2. Any error in `addons/`, in a generated file, or without a source location (NuGet, MSBuild, CSC) → step 4
   directly. Transient errors (file locked, network) → rebuild once, then classify again.
2. **Plugins-only build** ("probe"): build the Godot project with every compile item outside `addons/` filtered out
   by MSBuild, to a separate output folder, so the editor's loaded assembly and the project's files stay untouched.
3. **Probe succeeds** → the plugins are fine and the project code must be adapted: `Keep` the update, with the
   cause and the project errors recorded in the journal. No further full build (it would fail again with the same
   errors, since nothing changed in between). No restart: the editor keeps the code it runs, and after a restart it
   loads the same assembly again (thanks to the S2 fix). When the user has fixed their code, **Retry** (under
   **Needs attention**) finishes the update as today.
4. **Probe fails** (or step 1 ruled it out) → `Rollback` as today.

## Plugins-only build (MSBuild compile filter)

Why not rename directories: a crash would leave the project hidden before Godot's first scan; Windows refuses to rename
directories with open handles; root-level `.cs` files and `Compile Include` outside the root are missed; other solution
projects break; and the build would overwrite the editor's assembly with one lacking every project script. The filter
touches no files and needs no persisted state.

Mechanism (to be confirmed by spike S1): `dotnet build <godot.csproj> -c Debug --no-incremental
-p:CustomAfterMicrosoftCommonTargets=<journal>/probe/probe.targets -p:OutDir=<journal>/probe/bin/
-fileLoggerParameters:LogFile=<journal>/build-probe-<name>.errors.log;ErrorsOnly`, where the generated `probe.targets`
applies only when `$(MSBuildProjectFullPath)` is the Godot project (global properties flow to referenced projects):

```xml
<ItemGroup Condition="'$(MSBuildProjectFullPath)' == '<godot.csproj>'">
  <Compile Remove="@(Compile)" Condition="!$([System.String]::Copy('%(FullPath)').StartsWith('<root>/addons/', StringComparison.OrdinalIgnoreCase))" />
  <ProjectReference Remove="<each reference not under addons/ and not declared by a recipe>" />
</ItemGroup>
```

Because of S2, the probe must never use the editor's `OutDir`: its Clean would delete the loaded assembly. With a
separate `OutDir` the Clean only hits the probe folder (verify in S1 that Clean of a shared `obj/` cannot touch `bin`).

Plugin code is everything Godot compiles under `addons/` (all addons, enabled or not, ePlugin or plain, since a plain
addon broken by the update is a plugin failure), plus the projects recipes declare. Recipe directories always live
under `res://addons/<slug>/`, so they are covered.

## Design

### Build diagnostics classifier (pure, `Internal/Update/BuildDiagnostics.cs`)

- Parses MSBuild's canonical format `path(line,col): error CODE: message [project]` from the errors-only file log
  (deduplicated, no summary lines; the `error` keyword stays English under a localized SDK, see S2). Falls back to
  parsing `BuildOutcome.Output` when the log is missing.
- Classifies each error: `Plugin` (path under `<root>/addons/`), `Project` (a `.cs` file elsewhere, including other
  projects of the solution), `Unknown` (no path, `.csproj`/`.sln`/`.props`/`.targets`, `obj/` or generated sources,
  `NUxxxx`, `MSBxxxx`, CSC without file), `Transient` (`MSB3026`, `MSB3027`, `MSB3021`, `NU1301`).
- Path rules: full-path normalization, case-insensitive on Windows, `/` vs `\`, both the link and the target path of a
  symlinked/junctioned addon folder.
- Result: `BuildFailureCause` = `ProjectOnly | Plugin | Unknown | Transient`, plus the capped list (200) of project
  errors for the UI. Exit code ≠ 0 with no parsable error at all → `Unknown`.

### Probe build (dotnet CLI)

- New `ICheckedDotnetCli.TryProbeBuild(ProbeBuild request)` in `DotnetCliBase`, implemented in `DotnetCli9` and
  `DotnetCli10`, arguments one per element, project path through `ProjectFile`, checked in `IDotnetCliTests`.
- Builds the Godot `.csproj`, not the solution. Separate `OutDir` under the journal folder: the editor's assembly in
  `.godot/mono/temp/bin` is never written or cleaned, so `HotReloadAssemblyWatcher` and `AssemblyReload.IsPending` do
  not react. `obj/` may be shared, because every later ePlugin build is non-incremental (verify Godot's own
  incremental build in S3).
- The probe folder is deleted after classification; its error log is kept with the other build logs.

### Applier (`UpdateApplier`)

- New `IUpdateHost.ProbeBuild(UpdateJournal)`; `GodotUpdateHost` passes the plugin directories, the recipe projects of
  `journal.Recipes` (old for the interim build, target for the final one) and the journal folder.
- New private `BuildFailureFallback(journal, build, phase)` called where `Build(...)` fails today, before
  `UpdateFailureDecision`: classify → (rebuild once on `Transient`) → probe on `ProjectOnly` → returns the cause.
  The probe is recorded in `journal.Builds` as `probe-interim` / `probe-final` so logs and resume see it.
- Decision: `ProjectOnly` + probe success → `Keep` (with `BeforeReconcile` as today for the interim build), unless the
  policy forbids it (below). Anything else → existing decision (normally rollback).
- With `ask` (update from the plugin's details) the dialog still asks, but shows the cause and makes **Keep new
  version** the default for `ProjectOnly` and **Roll back** otherwise.

### Policy and settings

- Fallback applies when `on_build_failure` is `ask` (default) or `keep`. An explicit `rollback` disables it.
- New `eplugin/updates/keep_on_project_errors` (bool, default `true`) to switch it off without changing
  `on_build_failure`.
- Never for self-updates or a group containing `ePlugin`: a kept framework update cannot load its new code, so
  `UpdateRecovery` would roll it back after two unhealthy starts anyway.
- Never headless (`!UiAvailable`): a CI run must not leave a project that does not compile.
- `UpdateFailureDecision.Policy` gets the cause as input; its table stays pure and unit-tested.

### Groups

The rule "a grouped journal never keeps" exists because a project that does not build would fail every later group.
With the classifier that is handled: a later group's build fails with the same project errors, is classified
`ProjectOnly`, its probe (which includes the earlier group's new plugin code) passes, and it is kept too. Errors in
plugin code still roll that group back. **Retry** resumes all kept journals once the project builds.
Follow-up (not v1): record the kept group's project errors as a baseline and let a later group whose errors are a
subset commit instead of being kept.

### Journal and state (backward compatible)

- Additive journal fields: `FailureCause` (string) and `ProjectErrors` (list of `{file, line, code, message}`). Old
  readers ignore them; `UpdateRecovery` does not need them.
- Reuse the marker reason `update_kept_build_failed`, so Retry in every framework version (including one a self-update
  rolled back to) handles the journal. No change to `eplugin-state.json`, no `schemaVersion` bump.
- `journal.Failure` text: "Project code does not compile against <plugin> <version>; the plugins build on their own."

### UI

- `UpdateGroupResult` carries the cause; the manager's status line says "Updated <plugin>; your project code needs
  changes (N errors)" instead of "rolled back".
- `UpdateFailureDialog` (scene) shows the cause line and the first project errors (`file:line: code message`), and
  **Open build log**. Shown once after an automatic keep too, not only for `ask`.
- New **Roll back** action for a kept update in the ePlugin Manager (today a kept update cannot be rolled back from the
  UI, and now users get kept updates they did not choose). Before rolling back, rebase like `ResumeAfterRestart`
  (`RebaseRecipes`, `BackupProject(replace: true)`), so edits the user made to `.csproj`/`nuget.config` while fixing
  are not overwritten; recipe changes are reversed by reconcile, not by restoring files. The confirmation warns that
  changes inside `addons/<slug>` are discarded.
- The toolbar piece stays red while kept (already true: failed attempt).
- Docs: `docs/updating-plugins.md` (build failures paragraph, settings table), `docs/internals/update-system.md`
  (Groups, Plugin state attempts), README "Known constraints" if wording changes.

## Corner cases

| # | Case | Handling |
|---|---|---|
| 1 | Errors in project **and** plugin files | Rollback without probe |
| 2 | Errors without location (restore `NU1xxx`, `MSBxxxx`, `CS0006`, `CS2001`) | `Unknown` → rollback |
| 3 | Transient failure (locked DLL by a running game/IDE, network) | Rebuild once, then classify |
| 4 | Error in a generated file (`obj/`, source generator output) | `Unknown` → rollback |
| 5 | Project was already broken before the update | Same outcome (kept); wording never claims the update caused it |
| 6 | Project-specific addon under `addons/` that uses game code | Probe fails → rollback (today's behaviour); documented limitation |
| 7 | Another addon (not updated) broken by the new API | Plugin error → rollback (intended: ePlugin protects plugins) |
| 8 | Root-level `.cs`, `Compile Include` outside the root, `EnableDefaultCompileItems=false` | Filter removes every non-addon compile item |
| 9 | Solution contains tests/tool projects that break | Their errors count as project; probe builds the Godot project only |
| 10 | User library `ProjectReference` breaks (e.g. references a plugin-provided project) | Project error; probe drops non-plugin references |
| 11 | `partial` types or `InternalsVisibleTo` spanning addons and project | Probe fails → rollback (safe) |
| 12 | `TreatWarningsAsErrors` + new `[Obsolete]` on the plugin API | Project error → kept |
| 13 | Project-owned analyzers/generators producing code that needs project types | Probe fails → rollback (safe) |
| 14 | Self-update, or group containing ePlugin | No fallback |
| 15 | `on_build_failure=rollback`, `keep_on_project_errors=false`, headless | No fallback |
| 16 | Final build after reconcile fails in project code (recipe bumped a shared NuGet) | Same fallback; kept with `BeforeReconcile=false`, Retry commits after a build |
| 17 | Later groups of a batch after a kept group | Classified `ProjectOnly` again → kept; plugin errors still roll back |
| 18 | Probe fails for infrastructure reasons (restore, disk) | Rollback |
| 19 | Probe must not trigger reload or clean the editor's output | Separate `OutDir`; `AssemblyReload.IsPending` unaffected (verify S1/S3) |
| 20 | Failed `--no-incremental` build deletes the editor's assembly (**confirmed**, S2) | Snapshot/restore of the output folder in `DotnetCliBase` (first PR); also fixes today's Keep and failed activations |
| 21 | Editor crash/close during the probe | Journal still `Swapped`/`Reconciled` → existing resume rolls back; probe folder is inside the journal folder |
| 22 | Restart while kept | Loads the restored pre-update assembly; `CommittedWithErrors` + marker, Retry offered under Needs attention, icon red |
| 23 | User wants the old version after an automatic keep | New **Roll back** action (rebase first, see UI) |
| 24 | User edited files in `addons/<slug>` while kept | Retry verifies version only; rollback warns that those edits are lost |
| 25 | Plugins toggled while kept | Existing `RebaseRecipes` on Retry |
| 26 | Another update started while kept | The kept plugin stays blocked (`IsBlocked`); other updates are classified like any other (their build fails with the same project errors → kept). Warn in the review step that the project currently does not build |
| 27 | Shared `eplugin-state.json` while kept | Not advanced until the project builds (existing); docs: don't commit the kept addon files before Retry succeeds |
| 28 | Localized SDK output | Canonical `error CODE:` keyword stays English (seen in S2); parse the errors-only file log |
| 29 | Symlinked/junction addon folders, path casing | Normalize both forms |
| 30 | Very many errors | Store at most 200 in the journal, full log on disk |
| 31 | GDScript project code broken by a plugin update | Not detectable (no build); out of scope |
| 32 | GDScript-only plugin update | No build, no change |

## Spikes

- **S1** Compile filter via `CustomAfterMicrosoftCommonTargets` (or a generated wrapper project) with Godot.NET.Sdk
  4.4–4.7, `DotnetCli9`/`DotnetCli10`, .NET 8/10: Godot source generators still run for addon scripts, the filter does
  not leak into referenced projects, `OutDir` is honoured, `TOOLS` is defined, and the probe's Clean leaves
  `.godot/mono/temp/bin` alone.
- **S2** Done (above): a failed `--no-incremental` build empties `.godot/mono/temp/bin/<configuration>` with SDK 8 and
  10; a restart then loads no C# at all, silently.
- **S3** After a probe that shared `obj/`, Godot's own incremental build (Play, Build button) recompiles the full
  project; the probe does not trigger `HotReloadAssemblyWatcher`.
- **S4** Mostly answered by S2 (German SDK keeps `error CODE:`); confirm the errors-only file log under another
  locale.

## Tests

- `DotnetCliBase` snapshot/restore (S2 fix): failed build with missing output restores files and timestamps; a
  successful build drops the snapshot; both CLIs.
- `BuildDiagnosticsTests` (pure): parsing, dedupe, every classification row above, path normalization, caps.
- `UpdateFailureDecision` policy table with the cause.
- `UpdateTransactionTests` with a fake `IUpdateHost`: interim/final/plain failures × cause × probe result × policy ×
  self-update × group; journal fields; Builds entries; outcome.
- `IDotnetCliTests`: probe arguments for both CLIs.
- Journal compatibility: an old journal without the new fields loads; a new journal loads in `UpdateRecovery`.
- `UpdateFlowTests` / view model: status line and dialog content for kept-with-project-errors; Roll back action.

## Delivery (one PR each)

1. `fix: keep the editor's assembly when a plugin build fails`: S2 snapshot/restore in `DotnetCliBase`.
2. `feat(updates): show whether a failed update build broke plugin or project code`: classifier, journal fields,
   dialog/status text. No behaviour change.
3. `feat(updates): keep updates that only break project code`: probe build in both CLIs, applier fallback, policy,
   setting, groups, docs.
4. `feat(manager): roll back a kept plugin update`: manager action with rebase.
