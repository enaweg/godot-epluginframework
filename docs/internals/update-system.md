# Update system internals

How ePlugin's updater is built, for maintainers of the framework. The user-facing behaviour is described in
[Updating plugins](../updating-plugins.md); measured engine behaviour and pending platform checks are in
[update-system-spikes.md](../implementation-plans/update-system-spikes.md).

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
`tools/`); never edit it by hand. Each slug is, in this order:

1. the one `tools/extra_addons.txt` gives;
2. the plugin folder in the latest release's ZIP, chosen like the updater's `ReleaseAssets.Select` (file list only);
3. the slug already in the list;
4. the plugin folder in the repository at the latest release or version tag.

A slug claimed by several repositories goes to the one with the most stars. Release ZIPs the updater refuses (no or
several plugin roots, GDExtension) leave the add-on out. Entries are not installed or validated in advance. Add-ons the
ranking misses go in `tools/extra_addons.txt` (`slug;url` or `url` per line, any git host), which
`build_godot_addons.cs` always lists regardless of stars and activity.

## Checks

Checks use installed metadata and a 20-hour local cache, never the shared working-version index. A release announced as
newer whose `plugin.cfg` is not (an author forgot to raise it) is an R6 review warning rather than a refusal; once it is
installed, `UpdateService.WithoutInstalled` hides it using the revision and version that `Commit` records in
`UpdateCache.Revisions`.

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
the final build (`UpdateFailureDecision.Policy`); the batch **Update** rolls back.

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
