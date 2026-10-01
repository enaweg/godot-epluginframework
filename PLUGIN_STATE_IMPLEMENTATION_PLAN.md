# Plugin state serialization: implementation plan

This plan describes the persistence and recovery foundation. Automatic update, migration, and rollback execution remain future work.

## Goal and scope

Persist each ePlugin-managed editor plugin's slug, last acknowledged version, and finalized lifecycle state across editor restarts, C# assembly reloads, and project checkouts. Keep local attempts and failures through reload without publishing them as shared project state. Include `ePlugin` itself. These records will later support automatic addon updates, version migrations, and rollback decisions. Godot still controls which plugins are requested to be enabled; the records never enable one by themselves.

Plain Godot plugins are outside the first implementation: the framework does not receive their complete enable/disable lifecycle. They can still be inspected as dependencies using Godot's existing API. A managed plugin that has never been enabled may have no entry yet, because its C# type cannot reliably be identified from `plugin.cfg` alone.

## Storage and meaning

Use two project-owned JSON files, resolved with `ProjectSettings.GlobalizePath`:

- `res://addons/eplugin-state.json` is the shared, checked-in index. It contains the initial snapshot of currently active managed plugins when the index is missing, then only finalized states after all work in a later transition, update, or migration succeeds. It must never contain `failed` or `invalid`.
- `res://addons/eplugin-state.json.user` is the local working and failure record. It records an attempt before side effects start, then its failure or invalidity if work cannot finish. It survives editor restart and assembly reload, but must be ignored by Git and never be committed. Add an ignore rule to this repository and document the rule for consuming projects.

Both files sit directly under `addons/`, outside every addon directory, especially `addons/ePlugin/`, so replacing an addon cannot delete their history. The shared index also stays outside `.godot/` and `user://` so it survives a clean checkout. Consuming projects commit `eplugin-state.json` with `project.godot` and their addon files; the framework cannot make their Git commit.

The shared index records the **last acknowledged version** and last finalized framework lifecycle outcome. Its first creation is a baseline taken from the current editor state, not evidence that earlier recipes or migrations ran. After that baseline, the version and state are independent: ordinary activation or deactivation may complete while a newer installed version remains unacknowledged. The local `.user` file records incomplete or failed work without changing that shared history. Godot's `project.godot` and editor APIs remain authoritative for the requested enabled state. A discrepancy is drift to report, not a reason to overwrite history during initialization.

Start the shared index with this format:

```json
{
  "schemaVersion": 1,
  "lastCompletedAttemptId": null,
  "plugins": [
    { "slug": "ePlugin", "version": "1.0", "state": "activated" },
    { "slug": "sample_plugin", "version": "1.2.3-beta.1", "state": "deactivated" }
  ]
}
```

The local `eplugin-state.json.user` file is JSON too. It contains only local attempts, for example:

```json
{
  "schemaVersion": 1,
  "attempts": [
    { "attemptId": "b9a22459-1aa1-4246-8c5a-a419aa22fb48", "slug": "sample_plugin", "installedVersion": "2.0.0", "targetState": "activated", "state": "failed", "reason": "nuget_add_failed" }
  ]
}
```

- `slug` is the directory name used by `GetPluginSlug()` and is the unique key.
- `version` is the exact nonempty `plugin.cfg` value last **acknowledged** by the framework, including any prerelease suffix. It does not advance merely because files changed or a lifecycle transition ran. If no trustworthy version exists, omit that addon from the shared index and record the local problem; never invent `0.0`.
- In the shared index, `state` is only `activated` or `deactivated`, meaning either the initial observed baseline or a completed framework lifecycle transition. In `.user`, an attempt may have `failed` or `invalid`: `failed` means side effects failed or their completion is uncertain, including an interruption; `invalid` means addon-specific identity, metadata, or configuration prevented a safe attempt. A version mismatch alone is drift, not `invalid`.
- A local failure includes a stable, portable `reason` code; detailed exceptions belong in logs. `schemaVersion` versions each JSON format independently of addon versions. Sort entries by slug for stable diffs and reject duplicate slugs.
- `attemptId` identifies one logical operation, including its dependency cascade. The shared file's `lastCompletedAttemptId` changes only on a successful commit. If the editor stops after committing the shared file but before clearing `.user`, the matching ID proves that local attempt completed and its stale marker can be removed. A bootstrap baseline can use `null`.

The shared index keeps the prior finalized `activated` or `deactivated` state when `.user` records a failure. This provides the previous completed state without a separate `lastSuccessfulState` field. It does not prove that partly applied side effects were rolled back.

Keep a shared entry after an addon is disabled or temporarily absent from disk. A malformed JSON file, unsupported schema, or duplicate slug is a **file-level** error: preserve it, enter read-only recovery mode, and report its path rather than replacing it with a baseline. Such a file cannot safely be interpreted as per-addon `invalid` entries.

Because the shared index is checked in, another machine may receive an acknowledged version or finalized state that differs from its local addon files or Godot settings. Detect and report this drift without silently rewriting the version. Its local `.user` failure is not propagated through Git. A local downgrade needs an explicit rollback policy before its version can be acknowledged again. Atomic replacement prevents truncation, not Git merge conflicts when two machines edit the shared index.

## Implementation steps

1. Add internal state models and JSON stores under `src/eplugin-framework/addons/ePlugin/Internal/`, with no public API change. Load both files during `EGlobal.Initialize`, before `ReloadContexts` reconstructs contexts. Validate schema, required fields, enum values, and unique slugs. A missing file is allowed. Preserve either invalid file and enter read-only recovery mode instead of discarding local error information or replacing shared history.

2. After `ReloadContexts` discovers active editor nodes, create a missing shared index from the current state: record `ePlugin` after successful bootstrap and each currently active managed plugin using its current `plugin.cfg` version. This is a one-time observed baseline, not proof that earlier recipes or migrations ran. Do not add an addon with an unresolved `.user` attempt or unusable metadata as finalized; keep or create its local problem record instead. `ePlugin` also needs explicit disable handling because it does not implement the managed recipe callback and its current `_Process` path alone cannot record framework deactivation. After the baseline, add newly discovered managed plugins only after their first successful activation; a first failed attempt remains only in `.user`. A preexisting installation without an index cannot reveal versions installed before this baseline; document that limit.

3. Before framework-controlled side effects, including dependency activation, durably write a local `.user` attempt with a fresh ID and a conservative `failed`/`attempt_in_progress` marker. If this write fails, do not start the operation. Treat the root transition, required dependency cascade, and affected optional recipes as one logical operation. Stage resulting `activated`/`deactivated` entries in memory. Run and verify the required solution build before claiming success; `RefreshEditor()` currently mixes that build with a filesystem scan, so split those phases. Only after every required recipe and build step succeeds, atomically write the shared index once, then trigger the editor scan that may reload assemblies. Remove the completed attempt from `.user` after the shared commit. An early reload leaves the local marker for manual recovery. Do not commit nested transitions individually when `refreshAtEnd` is false. Keep an existing entry's `version` unchanged even if the installed `plugin.cfg` differs; a new successful entry establishes the current version as its initial baseline. The explicit `Activated` assignment from commit `3500a7d` is already in this task branch, but durable success must wait for validation.

4. Audit **both** install and reverse-recipe failure reporting before making transitions durable. Several CLI operations, including the solution rebuild, discard their exit status, and filesystem or Godot operations may throw. On a failed or uncertain operation, keep the shared index untouched and update `.user` with `failed` plus a stable reason. Record addon-specific preconditions that prevent a safe attempt as `invalid` in `.user`. Waiting for a dependency is neither success nor a permanent error. If updating `.user` fails, its prior `attempt_in_progress` marker still blocks automatic retry. Preserve existing public API behavior while adding internal success reporting.

5. Reconcile at startup and after assembly reload. `EPluginPlugin._Process` can recreate `EGlobal` after static state is lost; `ReloadContexts` currently assumes visible plugin nodes are `Activated`. Apply unresolved `.user` attempts as local blocks, including interrupted attempts, so framework recipe work is not automatically retried. If an attempt ID equals the shared file's `lastCompletedAttemptId`, clear that stale local marker after a completed commit. Provide an explicit editor action for manual retry: after the cause and any partial side effects are addressed, it re-evaluates the addon and attempts the requested transition; success commits shared state and clears the local marker, while failure keeps it. A failed deactivation may leave no live plugin node after reload, so recovery must reconstruct a usable context or report that manual cleanup is needed without clearing the marker. A reload, changed addon version, or ordinary Godot enable request does not clear a block. Compare the shared index with Godot's requested enabled state and installed `plugin.cfg`, report drift, and preserve the acknowledged version. Blocking framework recipe work cannot prevent arbitrary code in a consumer plugin's own Godot callbacks.

6. Write both JSON files safely: serialize each replacement to a separate temporary file in the same directory, flush it, then replace the destination. The persistent `.user` file is not the transient atomic-write file. Clean abandoned atomic-write files on the next load. If the shared save fails, keep the prior shared index and the local attempt marker. Avoid claiming completion or advancing the acknowledged version. Keep serialization deterministic so no-op initialization does not dirty the project. Detect external changes to the shared file before committing and stop instead of overwriting another machine's pulled state.

7. Add focused tests for both schemas, missing/invalid files, duplicate slugs, failed/interrupted writes, failed solution builds, initial baseline, version drift, stale `.user` cleanup after a shared commit, and blocked attempts across reload. Verify a failure leaves the shared file byte-for-byte unchanged and `.user` is ignored by Git. Cover activation/deactivation, failure propagation, dependency cascades, manual retry with a missing plugin node, and assembly reload with an editor integration harness where `EditorInterface` is available; existing headless tests cannot exercise all these paths. Run `dotnet build "src/eplugin-framework/EPlugin Framework.sln"` and `dotnet test "src/eplugin-framework/EPlugin Framework.sln"` with `GODOT_BIN` set to a Godot .NET editor executable.

## Migration and update handoff

Compare the acknowledged version with the installed `plugin.cfg` version before treating an updated addon as migrated. A later update or migration executor must create a `.user` attempt before changing addon files or running migrations, and advance the shared `version` only after the entire operation succeeds. On failure it leaves the prior shared version and finalized state intact and records the local problem. A disabled addon defers migration until enabled or until a deliberate offline path exists. An unknown starting version and a downgrade need explicit policies; neither silently rewrites the shared index.

Updating `addons/ePlugin/` follows the same rule: both state files are outside that directory and are read early on the next bootstrap. The shared index identifies the acknowledged version and prior finalized lifecycle state; `.user` identifies unfinished local work. These files do not contain old addon binaries, record every side effect, or guarantee rollback of a partly applied migration. Automatic rollback will still need a recoverable copy of the old addon and a defined resume/restore protocol. This implementation establishes state tracking and recovery gates; the update, migration, and rollback executors are later work.

## Completion criteria

- A missing `eplugin-state.json` is created from currently active managed plugins and `ePlugin` as a one-time baseline. Once checked in, it contains only finalized `activated`/`deactivated` states and acknowledged versions, and survives editor restart, C# assembly reload, addon replacement, and a clean checkout on another machine.
- A Git-ignored `eplugin-state.json.user` keeps local failed, invalid, or interrupted attempts across reload. These attempts are not automatically retried; only an explicit manual action can retry them.
- A failed transition or failed solution build leaves the shared index unchanged. A completed logical operation updates it once after validation and before the editor scan. A crash after that commit can be recognized by the matching attempt ID, so its stale local marker is cleared.
- Changing `plugin.cfg` produces visible drift without advancing the acknowledged version before migration.
- An interrupted write cannot leave either JSON file truncated, and invalid input cannot be silently replaced.
