# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

ePlugin Framework — a C# editor-plugin framework for Godot 4.4-4.7 (targets `net8.0`, tested against .NET 8/10).
It solves problems in Godot's native C# plugin system: editor plugins can't use project references or NuGet
packages, can't live outside the main game project (avoiding assembly-reload churn), and can't declare
dependencies on other plugins. ePlugin lets a plugin author declare what it needs (NuGet packages, project
references, autoloads, managed asset directories, other plugin dependencies) and installs/uninstalls all of
it automatically when the plugin is enabled/disabled in the Godot editor.

The whole project lives under `src/eplugin-framework/` (Godot project root — `project.godot`,
`EPluginFramework.csproj/.sln`).

### Repository layout

- `src/eplugin-framework/addons/ePlugin/` — **the main part of this project, the framework itself**. This is
  what you'll be editing.
- `src/eplugin-framework/addons/sample_plugin/`, `sample_dependant_plugin/`,
  `sample_addedcode_plugin/`, `sample_optional_plugin/` — any addon under `addons/` whose name starts with
  `sample` is a reference plugin demonstrating usage of the ePlugin framework, bundled with full source.
  Treat these as **read-only**: don't modify them unless explicitly asked. They're useful reading — minimal,
  working examples of how to consume the `IEEditorPlugin` API (see Architecture below).
- `src/eplugin-framework/addons/gdUnit4/` — a vendored unit testing framework plugin (used to run this
  repo's own test suite), not an example of the ePlugin API. Treat it as a **read-only** code dependency:
  don't modify it unless explicitly asked.
- `src/eplugin-framework/tests/` — gdUnit4 test suite for the framework.
- `design/` — logo/icon assets referenced by the README.

Almost all framework code is wrapped in `#if TOOLS`, since it only runs inside the Godot editor, never in
exported games.

## Commands

Build:
```
dotnet build "src/eplugin-framework/EPluginFramework.sln"
```

Run tests (gdUnit4 via its VSTest adapter — requires the `GODOT_BIN` environment variable to point at a
Godot .NET/Mono editor binary, since tests spin up a headless Godot runtime):
```
dotnet test "src/eplugin-framework/EPluginFramework.sln" --settings "src/eplugin-framework/.runsettings"
```
Run a single test:
```
dotnet test "src/eplugin-framework/EPluginFramework.sln" --filter "FullyQualifiedName~IDotnetCliTests.VersionTest"
```
Alternative: gdUnit4's own CLI runner (`src/eplugin-framework/addons/gdUnit4/runtest.cmd` /
`runtest.sh`), which takes `--godot_binary <path>` or the same `GODOT_BIN` env var.

Note: gdUnit4 editor tests are not currently possible in Godot (upstream
[gdUnit4#911](https://github.com/MikeSchulze/gdUnit4/issues/911)), so tests exercise framework internals
directly (e.g. `EGlobal`) with a mocked `EPluginPlugin` (Moq) rather than driving the real editor UI.

There is no lint command configured beyond the `.editorconfig` (UTF-8 charset only).

Documentation screenshots (`docs/images`) are regenerated with `tools/screenshots/capture.sh` (needs a desktop session
with X11 or Xwayland, `GODOT_BIN`, python3 with python-xlib and Pillow; `EPLUGIN_SCREENSHOTS_DARK=1` on GNOME). It
injects `tools/screenshots/DocScreenshots.cs` as an autoload for one editor run, opens every dialog with sample data,
captures each window with its frame, and restores the project afterwards. Add new dialogs there.

## Architecture

### Bootstrap and lifecycle

`EPluginPlugin` (`addons/ePlugin/EPluginPlugin.cs`) is the framework's own `EditorPlugin`
(`addons/ePlugin/plugin.cfg` → `script="EPluginPlugin.cs"`). Its `_Process` checks
`EGlobal.Instance.IsValid()` every frame and re-initializes if not — this is the workaround for Godot's C#
assembly-reload behavior, which wipes static state (including `EGlobal`) without re-running
`_EnterTree`/`_EnablePlugin`.

Every build of the project assembly (an activation's `TryRebuildSolution`, but also Rider or `dotnet test`) makes
GodotTools' `HotReloadAssemblyWatcher` reload it in place within half a second, and Godot 4.7.2's in-place reload is
fragile. Two rules follow. Never hand Godot a C# delegate for later work: use `CallDeferred(MethodName.X, args)` or
`new Callable(this, MethodName.X)` on a GodotObject (Godot resolves those on the reloaded instance, so guard against a
stale one), and `EGlobal.Post` from anywhere else, including background threads (`OnEditorThread` uses it; a queue
drained by `EPluginPlugin._Process` that a reload discards). And do not start a plugin operation while a reload is due:
`AssemblyReload.IsPending` (the build's timestamp against the one this assembly was loaded from) blocks the ePlugin
Manager's actions until the new assembly is loaded, and gives up after 15 s so a reload that never happens cannot lock
the manager. Don't use GodotTools' `Internal.IsAssembliesReloadingNeeded` for this: it reported a reload as needed
with no newer build on disk.

`EGlobal` (`addons/ePlugin/Internal/EGlobal.cs`) is an internal singleton owning all plugin state. It is the
brain of the framework: it tracks a `PluginContext` per managed `EditorPlugin`, drives the
enable/disable state machine (`EEditorPluginState`: `Created → Activated`/`Deactivated`/`Error`), resolves
plugin dependencies (enabling/version-checking them before the dependent plugin), and applies/reverses each
plugin's install recipe.

### Authoring contract (public API surface)

A consumer plugin implements `IEEditorPlugin.CreateRecipe(IEEditorPluginBuilder builder)` and calls the
extension methods `this.EnableEPlugin()` / `this.DisableEPlugin()` (`IEEditorPluginExtensions`) from its own
`_EnablePlugin()` / `_DisablePlugin()` overrides. `CreateRecipe` must be deterministic and side-effect free —
it only declares requirements via the fluent `IEEditorPluginBuilder` (NuGets, project references/solution
entries, autoloads, managed directories, plugin dependencies via `AddPluginDependency`, and optional plugin
dependencies carrying a nested recipe via `AddOptionalPluginDependency`). See
`addons/sample_plugin`, `addons/sample_dependant_plugin` (dependency example),
`addons/sample_addedcode_plugin` (`AddDirectory` example), and `addons/sample_optional_plugin`
(`AddOptionalPluginDependency` example) for minimal reference implementations, and the
README's "Advanced Plugin" example for the full builder surface.

`IEEditorPlugin` is an interface rather than an abstract base class specifically to work around C# bugs in
Godot 4.5.x/4.6.x EditorPlugin handling — don't "simplify" this back to a base class.

### Builder → Recipe → install/uninstall

The public builder surface is split in two: `IEEditorPluginRecipeBuilder` declares resources (`AddAutoload`,
`AddProject`, `AddNuget(s)`, `AddDirectory`), and `IEEditorPluginBuilder` extends it with dependency
declarations. Only the root recipe may declare dependencies — nested recipes get the resource-only interface.
The root interface re-declares the inherited members with `new` and an `IEEditorPluginBuilder` return type so
fluent chains can still reach the dependency methods; `EEditorPluginBuilder` therefore carries explicit
interface forwarders for the base-typed variants.

`EEditorPluginBuilder` (internal impl of `IEEditorPluginBuilder`) accumulates an `EEditorPluginRecipe`
(records for `Nuget`, `Project`, `Autoload`, plugin `Plugin` dependencies, `OptionalPlugin` dependencies,
plus a directory list); `EEditorPluginSubRecipeBuilder` is the `IEEditorPluginRecipeBuilder` impl used for the
nested recipe of an optional dependency (a bare `EEditorPluginRecipe` — no implicit ePlugin self-dependency).
`EGlobal` applies a recipe in `InstallEPlugin`/`ApplyRecipe` (add NuGets → add/reference solution projects →
show managed directories → add autoloads) and reverses it in `UninstallEPlugin`/`ReverseRecipe` in roughly
opposite order. Recipe application always goes through `PluginContext.Cli` (an `IDotnetCli`), never raw
`dotnet` calls elsewhere.

Optional dependencies are resolved by `EGlobal.ResolveOptionalRecipes` at install time: a nested recipe is
applied only when its plugin is already enabled and satisfies any version constraint. It never enables a
plugin and never fails the activation. What was applied is snapshotted in
`PluginContext.AppliedOptionalDependencies` so uninstall reverses exactly that (falling back to re-resolving
when the snapshot was lost to an assembly reload).

Activation order does not matter, because both directions are re-evaluated against the other installed
plugins:

- enabling: `ReevaluateOptionalDependencies` applies the nested recipes that just became satisfied. The
  baseline of "already installed" is that plugin's snapshot or, when an assembly reload lost it,
  `ResolveOptionalRecipes(..., ignoreSlug: <the plugin just enabled>)`, so only genuinely new recipes are
  applied.
- disabling: `UninstallOptionalDependenciesOn` reverses the nested recipes that were installed *because of*
  the plugin being disabled, so nothing is left referencing a plugin that is gone. The baseline falls back to
  `ResolveOptionalRecipes(..., assumeEnabledSlug: <the plugin being disabled>)`.

Entries stay in the snapshot once applied even if they stop being satisfied, so uninstall still reverses them.
Optional dependencies still take no part in the reverse-dependency scan in `DisableEPlugin`: disabling an
optional plugin removes the nested recipe but does not disable the plugin that declared it.

Both hooks sit in `EnableEPlugin`/`DisableEPlugin`, which return early for plugins that do not implement
`IEEditorPlugin`, so enabling or disabling a plain Godot plugin does not re-evaluate anything.

`PluginContext` (`addons/ePlugin/Internal/PluginContext.cs`) is the per-plugin state bag: the `EditorPlugin`
instance, its `IEEditorPlugin`/metadata/slug, its logger, its `IDotnetCli`, its recipe builder, its
`EEditorPluginState`, and the `AppliedOptionalDependencies` snapshot.

When a recipe's NuGet entry has an external/local `source`, `NugetConfigManager`
(`addons/ePlugin/Internal/Dotnet/NugetConfigManager.cs`) additionally mirrors that source into a root
`nuget.config`, so a fresh checkout (without the installing machine's ad-hoc `--source` flag) can still
restore the package. Entries it creates are tagged with a deterministic `ePlugin-<hash>` key and a
preceding XML comment tracking which plugin slugs currently depend on that source (multiple plugins can
share one entry); it never touches sources it didn't create, and only deletes `nuget.config` entirely once
removing the last managed entry leaves nothing else in the file.

### Dotnet CLI abstraction

`DotnetVersionManager` detects the installed `dotnet --version` and hands out either `DotnetCli10` or
`DotnetCli9` (both `DotnetCliBase : ExecuteCliBase, IDotnetCli`) — the two dotnet CLI generations differ
enough in `sln`/`nuget`/reference command behavior to need separate implementations. `ExecuteCliBase` does
the actual process execution. Any new dotnet CLI operation needs a corresponding abstract member on
`DotnetCliBase`/`IDotnetCli` implemented in both `DotnetCli9` and `DotnetCli10`.

Removals are idempotent: the CLI fails to remove a package, project reference or solution entry that is already gone,
although that is the state the caller wants. Both CLIs therefore first ask `DotnetCliBase.Check*Removal`, which reads
the `.csproj`/`.sln`/`.slnx` through `ProjectReferences` (pure, tested): an invalid package id or project path fails,
an absent entry succeeds with a warning, and anything it cannot tell (unreadable file, MSBuild properties) runs the CLI
as before.

### Update system

`Internal/Update/` contains pure checks/sources, bounded ZIP and sparse-git staging, validation, the transaction
applier, recipe snapshots/diffs, journal/cache persistence, and early self-update recovery.
`EGlobal.Updates.cs`, `EGlobal.UpdateApply.cs`, `EGlobal.UpdateRecipes.cs`, and `EGlobal.RecipeOperations.cs`
provide the Godot integration. HTTP/git work runs off the editor thread; lifecycle and recipe changes stay on it.

Local plugin directories (`LocalSourceSettings`, per-user JSON in the editor config folder, never in the project)
are a second source next to `update_url`: `LocalPackageIndexer` reads only the central directory and `plugin.cfg`
of every ZIP below them, using `SafeZipExtractor.PluginRoot` (one root `plugin.cfg`, sub-plugins below it, anything
else ambiguous) so it never lists what staging would refuse; the root folder's name is the slug it matches.
The index is rebuilt on every start and assembly reload by `EGlobal.LocalSources.cs` before the startup check;
`LocalIndexCache` (per-user `local-index.json`, keyed by path/size/mtime) keeps unchanged ZIPs from being opened again
and must be bumped via `IndexerVersion` whenever `LocalPackageIndexer.Read` changes what it extracts. Missing
directories are skipped but kept in the list. `PendingUpdates` merges remote and local candidates.
Project update sites (`UpdateSiteSettings`, the committed `res://addons/eplugin-update-sites.json`,
`EGlobal.UpdateSites.cs`, `UpdateSitesDialog`) replace a plugin's `update_url`. `PluginUpdateTarget.UpdateUrl` stays
the plugin.cfg value (the validator's R9 compares new packages against it) and `OverrideUrl` holds the project's site;
`UpdateUrls` lists the override first. `UpdateService.FirstWorkingSiteAsync` tries them in order, so an unsupported
or failing site falls back to plugin.cfg (reported in `UpdateCheckResult.Fallbacks`), and a candidate from either URL
belongs to the plugin (`CurrentCached`, `source_changed`). `CollectUpdateTargets`/`CollectPlugins` reload the file;
like `LocalSourceSettings` it is never overwritten when unreadable or newer, and it is deleted with its last entry.
`KnownPlugins` (`KnownPlugins.Data.cs` is generated, see its header) is the built-in list of add-on update sites, keyed by
slug: `PluginUpdateTarget.KnownUrl` is the last entry of `UpdateUrls`, and `PluginInfo.Known` fills in documentation,
source and website links. `eplugin/updates/builtin_update_sites` (default on, `EGlobal.UseKnownPlugins`) turns it off.
Regenerate the data with `tools/generate_known_plugins.cs` from `tools/build_godot_addons.cs`'s output (run both in
`tools/`). Each slug is, in this order: the one `tools/extra_addons.txt` gives, the plugin folder in the latest release's
ZIP (chosen like the updater's `ReleaseAssets.Select`, file list only), the slug already in the list, the plugin folder in
the repository at the latest release or version tag. A slug claimed by several repositories goes to the one with the most
stars. Release ZIPs the updater refuses (no or several plugin roots, GDExtension) leave the add-on out. Entries are not
installed or validated in advance. Add-ons the ranking misses go in `tools/extra_addons.txt` (`slug;url` or `url` per
line, any git host), which `build_godot_addons.cs` always lists regardless of stars and activity.
Checks use installed metadata and a 20-hour local cache, never the shared working-version index. A release announced as
newer whose `plugin.cfg` is not (an author forgot to raise it) is an R6 review warning rather than a refusal; once it is
installed, `UpdateService.WithoutInstalled` hides it using the revision and version `Commit` records in `UpdateCache.Revisions`. Every apply
requires dialog confirmation. Stage and validate the whole batch before touching addons (`PackageFetcher` leaves a package
that cannot be fetched or is refused out of the batch, as a `RefusedPackage`, and fails only when all are refused), then save and close all
open scenes (`IUpdateHost.CloseScenes`; `close_scene` exists only from Godot 4.5) before the swap. Managed plugins are
updated in place without disabling/uninstalling them; plain plugins may be toggled under observer suppression.
The interim bridge keeps old visible resources until the new assembly is loaded. Reconcile executes per-item
operations without intermediate builds/refreshes, tracks pending operations durably, handles hard/optional
dependencies, and protects shared NuGets. `ICheckedDotnetCli.TryBuild` in both CLI implementations returns exit
status/output and forces non-incremental compilation, including rollback (old timestamps cannot reuse new code).

A staged batch is split by `UpdateGroups.Plan` (pure, tested): plugins connected by hard dependencies, also through
plugins outside the batch, share a group; the implicit dependency on the framework does not join groups, and the
framework's group goes first. `UpdateApplier.ApplyGroups` moves each later group's staging into its own transaction
folder, then runs every group as an independent transaction (`UpdateJournal.Group` set; one group keeps `Group` null and
behaves exactly as before). A grouped journal never asks or keeps on a build failure; it rolls back. Only an update
started from a plugin's details (`ApplyUpdatesAsync(askOnBuildFailure: true)`, persisted as `UpdateJournal.AskOnBuildFailure`)
asks, for the interim and the final build (`UpdateFailureDecision.Policy`); the batch **Update** rolls back. `EGlobal` runs all
groups synchronously (no frames, so the editor cannot reload an assembly a group built), defers `RequestReload` and
`Scan` until the last group (`DeferRestarts`/`DeferScans`, also around `ResumeUpdates`), and restarts at most once.
`UpdateJournals.Read` orders journals by creation and group, so groups resume in the order they were applied; resume
reloads each journal because an earlier group may have changed it. When a journal commits or rolls back,
`RebasePending` refreshes the `backup-project` of every journal that has not reconciled yet and, on commit, its recipe
baselines for plugins it does not update, so a later rollback keeps the earlier group's changes. Reconcile leaves alone
the recipes of plugins in another active journal or rolled back since the assembly was loaded (`UntouchedRecipes`).

A group owns one PluginStateStore attempt. Write its local marker before side effects; acknowledge all versions
once after final build/verification. Rollback restores backups and abandons the marker without changing the
shared index. Keep-after-build-failure retains an `update_kept_build_failed` marker; an acknowledgement
conflict retains `update_commit_failed`. Retry dispatches these to the update applier. A journal kept before reconciling
(`BeforeReconcile`: interim or plain C# build failed) is not committed by Retry: once it builds, Retry resets the marker
to `attempt_in_progress`, rebases the other plugins' recipes and `backup-project`, saves `InterimBuilt` and restarts, so
the update resumes with reconciling and the final build. An acknowledged attempt
must never be rolled back merely because diagnostic cleanup failed. State-file schema remains unchanged.

C# handoff uses a durable editor restart, with another restart after a managed final/rollback build. The interim
build already holds the new plugin code, so the final build and its restart are skipped when reconciling changed no
NuGet, project or directory and the bridge kept no old files (`UpdateApplier.ChangedSinceInterimBuild`);
`restart_policy=always` keeps them. Engine
metadata prevents scheduled checks and early recovery counters from repeating after assembly reloads.
`EPluginPlugin` invokes `UpdateRecovery` before touching EGlobal; this reader is deliberately independent of
EGlobal and recipe execution. Self-update holds the ActivationProgress window across the folder swap and always rolls
back failures. Two unhealthy starts restore backups, project settings and directory visibility, force a build,
then clear the marker. A health marker is written only after verification. Keep journal readers additive and
backward compatible across framework versions; unknown/newer schemas need manual repair.

See README's update/publishing/recovery contract and
`docs/implementation-plans/update-system-spikes.md` for measured engine behavior and pending platform checks.

### ePlugin Manager

`Internal/Manager/` holds the framework's only editor UI entry point, opened from the single Tools menu entry and
the toolbar button that `EPluginPlugin.AddManagerUi` installs (the button's instance id is kept in Engine metadata
so a stale one can be removed after an assembly reload). When an update restarts the editor while the manager is open,
`EGlobal.RestartRequested` writes `ManagerReopenMarker` (`.godot/eplugin/manager-open`, renewed by an interim restart,
expires after an hour); the start that requests no further restart wraps `Initialize` in one `ActivationProgress` scope,
reopens the manager and closes that progress window a few frames later. Every dialog is its own scene (`EPluginManagerDialog.tscn`,
`LocalSourcesDialog.tscn`, `UpdateFailureDialog.tscn`); scripts only bind `%`-unique nodes and set what a scene cannot
store (e.g. Tree column titles/widths). Keep layout changes in the scenes. Close dialogs with `EditorWindows.Release`, never `QueueFree` in the frame that hides them: freeing a window from its
own button, or right after a reload while it has focus and the mouse, crashed Godot 4.7.2 on the next window message
(use-after-free in its message handling). Release hides it now and frees it 0.25 s later through a native callable.
Scene roots set `auto_translate_mode = 2`,
otherwise the editor translates the English UI into its own language. An autowrapping Label needs a
`custom_minimum_size` width, or the dialog measures it at zero width and pops up taller than the screen. Scenes are
authored for 100% editor scale: `EditorWindows.Prepare` scales the window's size and its controls' minimum sizes, and
plugin BBCode pages go through `PluginManagerViewModel.ScaleFontSizes`. Icons come
from `EditorIcons`, which renders the SVGs at the editor scale (a headless editor re-imports them at 100%). `EGlobal.CollectPlugins` scans every `res://addons/<slug>/plugin.cfg`; `PluginCatalog.Classify`
recognizes disabled ePlugins by the `ScriptPathAttribute` of their compiled type. `PluginManagerViewModel` is pure
(rows, update selection, BBCode details) and unit-tested. New user-facing actions belong in this dialog, not in
additional Tools menu entries. Long-running actions (enabling/disabling, Retry failed, Check for updates,
downloading and installing) show their progress through an `ActivationProgress` scope, not through the dialog: the
dialog has no progress bar and only puts the outcome in its status line.

`ActivationProgress` is thread-safe. A `Begin` outside any scope starts a *user* of the progress window; nested scopes,
Godot callbacks inside it and its async flow (tracked with an `AsyncLocal`) belong to the same user, and a
`Scope.SetText` may come from any thread (downloads report that way). Internal `Begin` calls request hiding Project
Settings once per active progress lifetime, on the editor thread; public scopes leave it visible. A lifecycle scope
can still hide it when public progress is already active. Only the editor thread starts a helper, because both UI
operations need Godot APIs. While users are active a
`System.Threading.Timer` sends a heartbeat for each every 5 s, also while the main thread is blocked; the assembly's
`Unloading` hook stops it (a running timer would keep the collectible assembly loaded). The helper (`src/activation-progress`,
protocol in its `Commands`) shows the newest text of the active users, closes the window after 15 s without a message
from any of them (a user's `HOLD` extends that once, used for restarts) and exits 15 minutes after the last message.
Editors of the OS user using the same `ProgressSession.Protocol` share one helper: it is published as
`<token>.helper` in Godot's editor data directory (`eplugin/progress`), next to `<token>.commands`. Editors connect to
the oldest running compatible helper whose command file they can open; missing or inaccessible command files are
skipped. Raise `Protocol` whenever the command protocol or transport coordination changes. Protocol 3 requires every
writer to acquire the helper's named mutex (`eplugin-progress-<token>`, with a 100 ms wait limit), seek to the end,
and write a complete command without buffering before releasing it. `FileMode.Append` on a persistent stream alone
can overwrite another editor's commands. Older writers use a separate helper. The helper runs with redirected
standard streams, so it never keeps the editor's output open.

Progress feedback is best effort: launch, send, handoff and cleanup failures do not fail plugin work. A helper lost
before it is 30 s old is retried only after all current users end and another operation begins. An older helper may
be replaced immediately, replaying the active users' text. Recovery uses at most two connection/send attempts per
command; replay sends directly instead of recursively entering recovery. If the replacement also fails, retries
stop until all current users end and another operation begins. Failure to read the helper's age still disposes the
connection. `ActivationProgressTests` and `ProgressSessionTests` cover recovery, concurrent writes, inaccessible
helpers and restart handoff.

`EPlugin.Progress` exposes `IEPluginProgress.Begin(string)` to other C# editor plugins without looking up the editor
node. Its `IEPluginProgressScope` implements `IProgress<string>` and `IDisposable`; the internal adapter uses
`ActivationProgress.Begin(text, hideSettingsDialog: false)`. Reports are synchronous and thread-safe, disposal is
idempotent, and reports after disposal are ignored even when the enclosing user is still active. A missing helper
still returns a usable scope. Cancellation belongs to the operation; restart handoff remains internal. Consumers
can inject `IEPluginProgress` for tests. `docs/progress-dialog.md` documents the public API, user behavior, timing,
failure handling, internal scope usage and transport rules.

### Plugin licenses

A plugin's license comes from the recipe's `SetLicense` (text, or a `res://` path), else `license_file` in
`plugin.cfg`, else the plugin's root `LICENSE`. Only `license_required=true` in `plugin.cfg` makes it require
acceptance, because that has to be known without running plugin code (e.g. for staged updates). `Internal/Licenses/`
is pure and unit-tested: `PluginLicense` resolves/reads it and gives it a *source* (relative file, `res://` path, or
`text:<hash>` for recipe text; file contents are never compared), and `LicenseReview` holds per-license decisions
(declining a dependency's license cancels and skips its dependants). Acceptances live in the optional `licenses` list
of the shared `eplugin-state.json` (`PluginStateStore.TryRecordLicenses`). It is omitted while empty because the
store's reader disallows unmapped members, so older framework versions can still read such files.
`EGlobal.Licenses.cs` gates `EnableEPlugin` before `JoinTransition` (skipped for manual retries and update reconciles)
over the plugin and every disabled hard dependency it would enable; a disabled ePlugin's recipe comes from a
throw-away instance of its compiled script type. With pending licenses the plugin is disabled again (deferred: Godot is
still inside its `_EnablePlugin`), the review is queued, and `EPluginPlugin._Process` shows `LicenseDialog` a frame
later; accepting re-enables it. `ReviewEnabledLicenses` (end of `Initialize`) asks about enabled plugins whose license
source is not accepted, e.g. a recipe license an update changed; declining disables them. The ePlugin Manager asks
before `SetPluginEnabled` and before installing staged updates (skipping recipe-licensed plugins), and its details link
the license to `LicenseDialog.CreateViewer`. The `eplugin/licenses/auto_accept` project setting (no manager UI, logged)
skips the dialog.

### Plugin welcome pages

A plugin's welcome page comes from the recipe's `SetWelcome` (text, or a `res://` path), else `welcome_file` in
`plugin.cfg`, else its root README (`README.md`, `README.txt`, `README`, case-insensitive); without any of these it has
none. `Internal/Welcomes/PluginWelcome.cs` is pure and unit-tested and reuses `PluginLicense`'s path rules and display.
Shown pages live in the optional `welcomes` list of the shared `eplugin-state.json` (`PluginStateStore.TryRecordWelcomes`),
recorded by slug only, so a page is shown once per project and omitted while empty like `licenses`.
`EGlobal.Welcomes.cs` has no lifecycle hook: `EPluginPlugin._Process` calls `TakeWelcomes` every frame, which every 30
frames collects enabled, unrecorded plugins (ePlugins only once `Activated`) while no transition, recipe update, update
journal or license review is pending. That one path covers activations, plain plugins enabled in Project Settings,
assembly reloads and restarts. A session set keeps plugins without a page from being read again. `WelcomeDialog`
(scene + script, like `LicenseDialog`) lists several pages; closing it in any way records all of them, but an assembly
reload drops it unrecorded. It opens only while no license or update-failure dialog is open. `UpdateApplier.Commit` records
the updated plugins as shown, so updates never show a page. The ePlugin Manager links the page in the details
(`PluginManagerViewModel.WelcomeLine`).

### Logging

`ILogger`/`ILoggerFactory` are pluggable (`GodotConsoleLogger`, `NullLogger`, `GenericLoggerFactory`).
`EGlobal.SwitchLogging` lets a dependent plugin (e.g. an external logging plugin) take over ePlugin's own
console logging at runtime — this is why logger wiring goes through the factory rather than being
constructed once and cached globally. `GD.PushError`/`PushWarning` always report their direct caller, so
`GodotConsoleLogger` binds GodotSharp's internal `NativeFuncs.godotsharp_err_print_error` and passes the first frame
outside any `ILogger` (or `[StackTraceHidden]` method) as the location; without that binding it falls back to `GD.Push*`.

### Directory show/hide

`ShowHideHelper` implements `AddDirectory`: it toggles a managed directory's visibility by renaming it with/
without a leading `.` (POSIX-style hidden dirs) and toggling the Windows hidden attribute. Used for shipping
plugin source that should stay inert until the plugin is activated. A recipe may name either form (`src` or `.src`).
A directory already in the wanted state is left alone, one missing in both forms is only logged as a warning, and only
both forms existing at once fails, because neither can be renamed onto the other.

## Commit and PR messages

The release notes are generated, so message prefixes decide where a change appears in them. Pushing a `v*` tag runs
`.github/workflows/ci-release.yml`, whose `mikepenz/release-changelog-builder-action` (`mode: HYBRID`) drafts the
GitHub release from everything merged since the previous semver tag. Each **pull request becomes one entry, and the
entry is its title**. The commits inside the PR and the `Merge pull request #N from …` commit never appear. In HYBRID mode a
commit pushed straight to `main` without a PR can also be listed, by its subject line. A regex on
that title gives the entry its category:

```
^(build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test)(\(scope\))?(!)?: <description>
```

| Prefix | Section |
|---|---|
| `feat:` | Feature |
| `fix:` | Fix |
| `build:` `chore:` `ci:` `docs:` `perf:` `refactor:` `revert:` `style:` `test:`, or no prefix | Other |

A GitHub label `feat`/`feature` or `fix`/`bug` on the PR does the same, and `ignore` leaves it out. A title the regex
does not match lands in Other. That is why many past releases had to be re-sorted by hand.
`.github/workflows/ci-pr-title.yml` fails a PR whose title does not follow the rules below (re-run on every title edit);
keep its pattern in step with the release workflow's `label_extractor`. Rules:

- **PR title**: always use a prefix. Use `feat:` for new user-visible behavior, `fix:` for a bug fix, and the other
  types for anything users won't notice. If a PR does both, prefix by the change users care about most. The whole
  PR is a single entry under that one prefix, so don't open a PR that mixes unrelated features and fixes.
- Write the type in lowercase, follow it with `: ` (colon and exactly one space), and start the description with a
  letter or digit. The regex is case-sensitive and requires a word character right after the space, so
  `Fix: …`, `fix:…` and ``fix: `Foo` …`` all fall into Other. Write a short imperative description in lowercase, with
  no trailing period. The entry reads as a sentence on its own, e.g. `fix: keep the ePlugin Manager open after a
  successful update`.
- Scope is optional and may only contain letters, digits, `_`, `-` and `.` (`feat(manager): …`). Mark breaking
  public-API changes with `!` (`feat!: …`). There is no separate breaking-changes section, so also say what breaks
  in the PR description.
- Only these eleven types are recognized. Others (`tools:`, `update:`) fall into Other, so map them instead.
  Tooling goes under `chore:` or `build:`, workflows under `ci:`, README and `docs/` under `docs:`, and dependency
  bumps under `chore(deps):`, which is Dependabot's prefix.
- **Commits** use the same format (`type(scope)!: description`), so a branch can be squash-merged or pushed
  directly without losing its category. Any direct push to `main` (version bumps, store images) still needs a
  prefix, e.g. `chore: update version for next release`.
- **Merge commits**: keep GitHub's default `Merge pull request #N from …` message. It is ignored, and the PR title
  is what counts. Merge PRs with a merge commit, as the history does. If you squash instead, keep the PR title
  (with its prefix) as the squash commit's subject.

`.github/release.yml` (labels `enhancement`, `breaking-change`, `ignore-for-release`) is only used by GitHub's own
"Generate release notes" button, not by the release workflow.

## Known constraints (see README for full list)

- Activating/deactivating an ePlugin freezes the Godot editor UI for the duration of the install/uninstall.
- A failure mid-install/uninstall can leave the project in a non-compilable state requiring manual fixup.
- The Godot editor's plugin UI does not auto-refresh; newly-activated dependent plugins only appear after
  reopening the plugin UI.
- External Assets (scenes, models, non-code resources) aren't supported directly — only whole directories
  toggled via `AddDirectory`, since Godot requires a unique ID per asset that external files can't have.
- Godot 4.5+ has a regression with `EditorPlugin` ([godotengine/godot#110971](https://github.com/godotengine/godot/issues/110971)); this is the reason `IEEditorPlugin` is an interface, not a base class.
