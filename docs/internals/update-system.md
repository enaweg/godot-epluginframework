# Update system internals

How ePlugin's updater is built, for maintainers of the framework. The user-facing behaviour is described in
[Updating plugins](../updating-plugins.md).

## Layout

`Internal/Update/` contains the pure parts: checks and sources, bounded ZIP and sparse-git staging, validation, the
transaction applier, recipe snapshots and diffs, journal and cache persistence, and early self-update recovery.
`EGlobal.Updates.cs`, `EGlobal.UpdateApply.cs`, `EGlobal.UpdateRecipes.cs` and `EGlobal.RecipeOperations.cs` connect
them to Godot. HTTP and git work runs off the editor thread; lifecycle and recipe changes stay on it.

The ePlugin Manager's workflows (checking, loading a plugin's versions, staging, reviewing and installing a batch) are in
`Internal/Manager/UpdateFlow.cs`, which reaches the framework through `IUpdateWorkflowHost` and is tested without the
editor in `UpdateFlowTests`.

## Sources

### Local plugin directories

Local plugin directories (`LocalSourceSettings`, per-user JSON in the editor config folder, never in the project) are a
second source next to `update_url`. `LocalPackageIndexer` reads only the central directory and `plugin.cfg` of every ZIP
below them, using `SafeZipExtractor.PluginRoot` (one root `plugin.cfg`, sub-plugins below it, anything else ambiguous),
so it never lists what staging would refuse; the root folder's name is the slug it matches.

The index is rebuilt on every start and assembly reload by `EGlobal.LocalSources.cs` before the startup check.
`LocalIndexCache` (per-user `local-index.json`, keyed by path, size and modification time) keeps unchanged ZIPs from
being opened again; bump `IndexerVersion` whenever `LocalPackageIndexer.Read` changes what it extracts. Missing
directories are skipped but kept in the list. `PendingUpdates` merges remote and local candidates.

### Project update sites

Project update sites (`UpdateSiteSettings`, the committed `res://addons/eplugin-update-sites.json`,
`EGlobal.UpdateSites.cs`, `UpdateSitesDialog`) replace a plugin's `update_url`. `PluginUpdateTarget.UpdateUrl` stays the
`plugin.cfg` value (the validator's R9 compares new packages against it) and `OverrideUrl` holds the project's site;
`UpdateUrls` lists the override first. `UpdateService.FirstWorkingSiteAsync` tries them in order, so an unsupported or
failing site falls back to `plugin.cfg` (reported in `UpdateCheckResult.Fallbacks`), and a candidate from either URL
belongs to the plugin (`CurrentCached`, `source_changed`). `CollectUpdateTargets` and `CollectPlugins` reload the file.
Like `LocalSourceSettings`, it is never overwritten when it is unreadable or was written by a newer version, and it is
deleted with its last entry.

### Built-in update sites

`KnownPlugins` (`KnownPlugins.Data.cs` is generated, see its header) is the built-in list of add-on update sites, keyed by
slug. `PluginUpdateTarget.KnownUrl` is the last entry of `UpdateUrls`, and `PluginInfo.Known` fills in documentation,
source and website links. `eplugin/updates/builtin_update_sites` (default on, `EGlobal.UseKnownPlugins`) turns it off.

Regenerate the data with `tools/generate_known_plugins.cs` from the output of `tools/build_godot_addons.cs` (run both in
`tools/`); never edit it by hand. `build_godot_addons.cs` needs a GitHub token without scopes and reads three hosts at
the same time: GitHub repositories from the godot-stars ranking (at least 20 stars, read in GraphQL batches one after
another), and the searches of gitlab.com and codeberg.org for Godot projects (at least 5 stars; a token is optional, with
one GitLab also leaves out forks). Each token comes from its environment variable (`GITHUB_TOKEN`, `GITLAB_TOKEN`,
`CODEBERG_TOKEN`) or else from the host's CLI when it is logged in (`gh`, `glab`, a codeberg.org login of `tea`). Every
add-on needs a commit or activity within two years and a release or version tag. The 500-entry maximum applies to the
GitHub ranking only. Searches also find games: a repository with a game topic and no add-on topic is skipped, and
entries found by a search are marked `searched`. Each slug is, in this order:

1. the one `tools/extra_addons.txt` gives;
2. the plugin folder in the latest release's ZIP, chosen like the updater's `ReleaseAssets.Select` (file list only);
3. the slug already in the list;
4. the plugin folder in the repository at the latest release or version tag.

A searched entry is listed only when its slug is named like its repository (one contains the other, ignoring `-`, `_`,
`.` and "godot"), because games and projects often hold someone else's add-on in `addons/`. A slug claimed by several
repositories goes to the one with the most stars. Release ZIPs the updater refuses (no or several plugin roots) leave the
add-on out. Entries are not installed or validated in advance. Add-ons the ranking and searches miss or leave out go in
`tools/extra_addons.txt` (`slug;url` or `url` per line, any git host), which `build_godot_addons.cs` always lists
regardless of stars and activity. GitLab and Forgejo repositories there are read through their APIs; other hosts only
through their version tags.

## Checks

Checks use installed metadata and a 20-hour local cache, never the shared working-version index. A release announced as
newer whose `plugin.cfg` is not (an author forgot to raise it) is an R6 review warning rather than a refusal; once it is
installed, `UpdateService.WithoutInstalled` hides it using the revision and version that `Commit` records in
`UpdateCache.Revisions`.

## Plugin kinds

`Internal/Kinds/` tells the kinds of plugins apart (`PluginKind`: Framework, EPlugin, CSharp, GDScript, GDExtension,
Unknown, GDExtensionAddon) and gives each its own sealed `PluginKindWorkflow`, so the update steps that differ between kinds are in one
class per kind and every decision is logged with the kind's name (`GDExtension plugin native: ...`). `UpdateApplier`
runs the same steps for every kind: `BeforeSwap`, disable (`DisabledDuringSwap`), `Swap.Install`, the uid map and the
interim bridge, `AfterSwap`; on rollback `Swap.Restore` and `AfterRestore`; `NeedsRestart` after commit and rollback.
`Validate` adds the kind's findings to `AddonPackageValidator`.

`PluginKinds.Classify` is the manager's classification (an ePlugin by its compiled script type, a GDExtension plugin by
a `.gdextension` file below its folder, otherwise by the entry script); a folder without `plugin.cfg` whose
`.gdextension` lies outside hidden folders is a GDExtension add-on (`PluginKinds.IsExtensionAddon`, `EGlobal.ExtensionAddons.cs`).
Updates use `ForPackage` (the installed framework, ePlugin or GDExtension add-on keeps its kind, a `.gdextension` in the
installed or staged version makes it a GDExtension plugin)
and record the kind by name in `UpdatePluginJournal.Kind`. Journals without it, or with a kind this version does not
know, fall back to `IsEPlugin` (Framework, EPlugin or Unknown). The swap is chosen per plugin (`Swap(plugin)`): both
GDExtension workflows always use `AddonFileSwap`, the ePlugin workflow when `UpdatePluginJournal.NativeExtension` says
the installed or staged version has a `.gdextension`, every other case the folder swap.

### GDExtension plugins

In the editor on Windows, Godot loads every GDExtension library from a copy named `~<library>` in the same folder
(hidden, with `~<library>_N.pdb` debug symbols) and deletes it when the library is closed; on other systems it loads
the original. The copy stays open, so the plugin folder cannot be moved. `AddonFileSwap` replaces files one by one
instead: it copies the old version, without the runtime copies (`PackageFiles.IsRuntimeCopy`, which `PackageFiles.Skip`
also leaves out of packages), to `backup/<slug>.partial` and renames it to `backup/<slug>` before any installed file
changes; then it deletes the old files (one that cannot be deleted but can be renamed, such as a library Godot loaded
directly, goes to `discarded/<slug>`), moves the staged files in, and removes the folders left empty. Restoring deletes
the new files and copies the backup back, so an interrupted swap or restore can be repeated.

`NativeExtensionWorkflow.AfterSwap` (the base of `GDExtensionPluginWorkflow` and `GDExtensionAddonWorkflow`, which
differ only in `DisabledDuringSwap`) compares the `.gdextension` files (`GDExtensionConfig`) of the backup and the new
version: the same files, all `reloadable` in both, and nothing discarded reload in place through
`IUpdateHost.ReloadExtension` (`GDExtensionManager.ReloadExtension`); anything else sets `RestartRequired`, and `Commit`
restarts the editor. `NativeSession` names the editor process the flags refer to: after a rollback, an editor that
neither reloaded nor restarted since the swap still runs the restored libraries and needs nothing; otherwise the restored
extension is reloaded in place or the editor restarts. `GDExtensionRules` refuses libraries outside the plugin folder
(R18, a warning for icons), a missing library for the editor's platform (R19), a linked plugin folder (R20) and an
extension outside the editor's Godot version limits (R22, `GDExtensionConfig.CompatibilityProblem`, which follows
Godot's own check), and warns about platforms or release libraries the installed version had and the new one drops
(R23, `DroppedPlatforms`), for every kind with a GDExtension; R21 says whether the update reloads or restarts. An
ePlugin with a GDExtension needs no reload logic: its
update always builds and restarts, which loads the new libraries, and so does its rollback. Only the framework refuses a
GDExtension (R10): it ships none, and early self-update recovery replaces its folder at startup, after Godot loaded
every extension. GDExtensions in `AddDirectory` directories must be reloadable: `ShowHideHelper` unloads them through
`IEditorHost.UnloadExtension` before hiding (which closes the runtime copies that keep the folder from being renamed on
Windows), loads them after showing, and refuses to show a directory with one that is not reloadable.

### GDExtension add-ons

A GDExtension add-on has no `plugin.cfg`, so nothing in its files names a version or an update site. Its installed
version is the one the shared state recorded (`PluginStateStore.GetShared`), written by `TryAcknowledgeVersions` like
any update's; until then it is "" and `CheckForUpdatesAsync` leaves it out (`Checked`), while versions are still listed
and installed from the manager. `AddonPackageValidator` takes the announced version for a package without `plugin.cfg`
(R6 is informational while the installed version is unknown, R24 when the new version brings a `plugin.cfg`), and
`UpdateApplier` reads the old version from the store (`InstalledVersion`) and the new one from the package
(`PackageVersion`). The add-on is never disabled, `IUpdateHost.IsEnabled` counts it as enabled, and verification only
checks that it is still an add-on or became a plugin. Staging finds its folder with `SafeZipExtractor.PackageRoot` /
`ExtensionRoot` (the shallowest folder named like the slug holding a `.gdextension`), also in git; a git source pinned
to a branch cannot name its version, so only tags and releases update it. `PlainPluginObserver` treats it as owned, so
its activated shared entry is not reported as a disabled plugin.

## Staging and swapping

Every apply requires dialog confirmation. Stage and validate the whole batch before touching addons: `PackageFetcher`
leaves a package that cannot be fetched or is refused out of the batch, as a `RefusedPackage`, and fails only when all are
refused. Then save and close all open scenes (`IUpdateHost.CloseScenes`; `close_scene` exists only from Godot 4.5) before
the swap.

Managed plugins are updated in place without disabling or uninstalling them; plain plugins may be toggled under observer
suppression. The interim bridge keeps old visible resources until the new assembly is loaded. Reconcile executes per-item
operations without intermediate builds or refreshes, tracks pending operations durably, handles hard and optional
dependencies, and protects shared NuGets. `ICheckedDotnetCli.TryBuild` in both CLI implementations returns exit status
and output and forces non-incremental compilation, including on rollback (old timestamps cannot reuse new code).

## Groups

A staged batch is split by `UpdateGroups.Plan` (pure, tested): plugins connected by hard dependencies, also through
plugins outside the batch, share a group; the implicit dependency on the framework does not join groups, and the
framework's group goes first. `UpdateApplier.ApplyGroups` moves each later group's staging into its own transaction
folder, then runs every group as an independent transaction (`UpdateJournal.Group` set; a single group keeps `Group`
null and behaves exactly as before).

A grouped journal never asks or keeps on a build failure; it rolls back. Only an update started from a plugin's details
(`ApplyUpdatesAsync(askOnBuildFailure: true)`, persisted as `UpdateJournal.AskOnBuildFailure`) asks, for the interim and
the final build (`UpdateFailureDecision.Policy`); the batch **Update** rolls back. The exception is a build that fails
only in project code while the plugins compile on their own (below): it is kept, in a group too, because a later group
then fails with the same project errors and is kept the same way.

## Build failures in project code

`UpdateApplier.BuildPasses` wraps the interim, plain C# and final builds (not rollback and Retry builds). On failure,
`BuildDiagnostics.Classify` (pure) reads MSBuild's canonical `file(line,col): error CODE: message` lines; the `error`
keyword is not localized. An error below `addons/` or `IUpdateHost.PluginCodeRoots` (link targets of addons, directories
of recipe projects, never the project directory itself) is `PluginCode`; one without a `.cs` file, in `.godot/`, below
an `obj/` folder or in a file that does not exist (source generator output) is `Unknown`; `MSB3021/3026/3027`, `CS2012`
and `NU1301` are `Transient` and get one more build (`<name>-again`); otherwise every error is `ProjectCode`. Only
`ProjectCode` runs the plugins-only build (`<name>-plugins-only`, recorded in `journal.Builds`), and only when keeping
is possible at all (`MayKeepForProjectCode`: `keep_on_project_errors`, UI available, `on_build_failure` not `rollback`,
no self-update). The journal keeps `FailureCause`, `PluginsBuildAlone` and the first 200 `ProjectErrors` (additive
fields; `KeptForProjectCode` combines them) and `Policy(..., projectCodeOnly)` keeps it, or asks for an update from the
details. The marker is the usual `update_kept_build_failed`, so every framework version's Retry handles it. Retry
refreshes `ProjectErrors` while the project still fails; `ResumeAfterRestart` clears them.

The plugins-only build (`ICheckedDotnetCli.TryBuildPluginsOnly`, `PluginsOnlyBuild`) builds the Godot `.csproj`
non-incrementally with `-p:CustomAfterMicrosoftCommonTargets=<generated targets>` and `-p:OutDir=<journal folder>`.
The targets file removes every `Compile` and `ProjectReference` item outside the plugin roots in a target that runs
before `BeforeRebuild`/`BeforeBuild`/`AssignProjectConfiguration` (a Rebuild resolves references while cleaning, and
item metadata cannot be used in evaluation conditions), only for the Godot project (global properties reach referenced
projects). Its `OutDir` must never be the editor's: its clean would empty it. Paths are MSBuild-escaped in the targets
file and in the property values, and `OutDir` is passed without a trailing separator (a quoted `...\"` would escape the
quote). Godot's own incremental build afterwards compiles the full project again.

`IUpdateHost.KeepOnProjectErrors`, `PluginCodeRoots` and `BuildPluginsOnly` have defaults (off, none, failing) so test
hosts opt in. `UpdateApplier.RollbackKept` (the manager's **Roll back to** link in a kept update's details) rebases
`backup-project` and the other plugins' recipes first when the journal was kept before reconciling, then rolls back.

Every non-incremental build goes through `DotnetCliBase.BuildKeepingOutput`: a Rebuild cleans
`.godot/mono/temp/bin/Debug` before compiling, so a failed one left the editor without any assembly after a restart (no
ePlugin, no Retry). `BuildOutputGuard` copies the folder to `.godot/eplugin/build-output` first and, when the build fails
and the assembly is gone, puts the missing files back with their timestamps, so neither GodotTools' watcher nor
`AssemblyReload` takes them for a new build.

`EGlobal` runs all groups synchronously (no frames, so the editor cannot reload an assembly a group built), defers
`RequestReload` and `Scan` until the last group (`DeferRestarts`/`DeferScans`, also around `ResumeUpdates`), and restarts
at most once. `UpdateJournals.Read` orders journals by creation and group, so groups resume in the order they were
applied; resuming reloads each journal because an earlier group may have changed it.

When a journal commits or rolls back, `RebasePending` refreshes the `backup-project` of every journal that has not
reconciled yet and, on commit, its recipe baselines for plugins it does not update, so a later rollback keeps the earlier
group's changes. Reconcile leaves alone the recipes of plugins in another active journal or rolled back since the
assembly was loaded (`UntouchedRecipes`).

## Plugin state attempts

A group owns one `PluginStateStore` attempt. Write its local marker before side effects; acknowledge all versions once
after the final build and verification (`TryAcknowledgeVersions`, which also records the attempt as the shared file's
`lastCompletedAttemptId`; recovery and rollback compare it with the journal). Rollback restores backups and abandons
the marker without changing the shared index.

Keeping a version after a build failure retains an `update_kept_build_failed` marker; an acknowledgement conflict retains
`update_commit_failed`. Retry dispatches these to the update applier. A journal kept before reconciling
(`BeforeReconcile`: the interim or plain C# build failed) is not committed by Retry: once the project builds, Retry
resets the marker to `attempt_in_progress`, rebases the other plugins' recipes and `backup-project`, saves `InterimBuilt`
and restarts, so the update resumes with reconciling and the final build.

An acknowledged attempt must never be rolled back merely because diagnostic cleanup failed. The state file schema is
unchanged by updates; see `PluginStateStore` for its compatibility rules.

## Restarts and recovery

C# handoff uses a durable editor restart, with another restart after a managed final or rollback build. The interim build
already holds the new plugin code, so the final build and its restart are skipped when reconciling changed no NuGet,
project or directory and the bridge kept no old files (`UpdateApplier.ChangedSinceInterimBuild`); `restart_policy=always`
keeps them. Engine metadata prevents scheduled checks and early recovery counters from repeating after assembly reloads.

`EPluginPlugin` invokes `UpdateRecovery` before touching `EGlobal`; this reader is deliberately independent of `EGlobal`
and recipe execution. A self-update holds the `ActivationProgress` window across the folder swap and always rolls back
failures. Two unhealthy starts restore backups, project settings and directory visibility, force a build, then clear the
marker. A health marker is written only after verification.

Keep journal readers additive and backward compatible across framework versions; unknown or newer schemas need manual
repair.
