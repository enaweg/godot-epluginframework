# Update system spikes — 2026-10-01

Environment: macOS, Godot 4.7.2 Mono, .NET 8.0.131, git 2.56.0. Disposable projects under `/private/tmp`;
repository examples and gdUnit4 were not modified.

| Spike | Evidence / implementation decision |
|---|---|
| S1 external build | An enabled C# probe retained its native node while the assembly load context unloaded and its static generation reset. `_Process` ran in the new assembly (`SPIKE_GENERATION 2 v3`). The fuller M5 fixture below supersedes this result: use a conservative restart and no managed watchdog. |
| S2 enabled replacement | Changed the probe's source and installed cfg version without disabling it; external build succeeded and filesystem scan detected both changes. No script/UID errors were logged. Recipe bridge/reconcile behavior is covered separately by transaction fixtures. |
| S3 partial sparse git | GitHub advertised `fetch=shallow wait-for-done filter`. A depth-one blob:none fetch of VContainer tag 1.19.0 (5401e5a…) and checkout of only `VContainer/Assets/VContainer/` produced two packs totaling **193.25 KiB** (object-pack size, not total wire bytes). Use non-cone sparse patterns: cone mode also includes ancestor/root files. Refuse servers without advertised filtering rather than silently downloading all blobs. VContainer is a URL syntax/transport example, not a Godot addon (no plugin.cfg). Self-hosted GitLab and SSH credentials cannot be exercised here; use fake transport tests. |
| S4 restart | RestartEditor(true) was requested from a deferred C# probe callback. In this headless macOS environment LaunchServices refused to launch Godot_mono.app. Keep the durable journal and print manual reopen instructions; startup resume works when the editor is reopened. Do not assume a restart request proves a new process launched. |
| S5 script tabs | ScriptEditor has no supported close-tabs API. Omit this optional best-effort step instead of depending on undocumented menu IDs. |
| S6 broken self-update | M7 exercised a real scratch self-update that compiled but threw in EGlobal.Initialize. The first reopen recorded one unhealthy start; the second restored backups and rebuilt; the third logged SELF_FIXTURE_RESTORED version=1.0 enabled=True, with no local marker. A separate compile-error fixture returned RolledBack at the interim gate. See the timestamp finding below. Windows file locks remain a release check. |
| S7 project settings | The C# probe registered SetSetting/SetInitialValue/AddPropertyInfo successfully and read back 20. Product settings use the same supported calls and preserve existing values. |
| S8 failure UI | Headless execution cannot exercise modal editor input. Treat a headless/shutting-down editor as no UI and roll back automatically. GUI decision behavior is isolated behind the tested failure policy/view model; interactive macOS/Windows checks remain release checks. |
| S9 enabled set | A disposable GDScript project reported `enabled_immediately=true disabled_immediately=true` immediately after SetPluginEnabled calls. The on-demand plain observer can read ProjectSettings directly. |

The repository's headless runner setting was corrected separately (commit 5025c9d); all 65 existing tests passed.
M0b (plain tracking) was already committed as 2251439. Plan documents still describe desired behavior; platform
checks above are intentionally distinguished from verified automated behavior.

M5 follow-up: the full managed-directory fixture (`.src` → `.code`, with game code consuming `Contract.Api`)
exposed Godot 4.7.2 collectible-assembly unload failures after an in-process update. Removing the managed watchdog
Timer did not make the reload reliable. Consequently `restart_policy=auto` conservatively uses a durable restart
for C# handoff, and another restart after a managed final build (or rollback build) to load that assembly. The
final build, and with it the second restart, is skipped when reconciling leaves the interim build's inputs unchanged
(no NuGet, project or directory change and no bridged old files); no assembly changes on disk then, so nothing can
trigger an in-process reload. Pure GDScript updates stay in-process. This avoids assuming that the simple S1 probe applies to the complete framework.

M7 timestamp finding: moving/copying backups restores old modification times. An incremental `dotnet build`
reported success but left the broken new assembly in place. Both checked CLI build implementations now use
`--no-incremental`; repeating the real self-update recovery loaded the original assembly successfully. The
compile-error fixture also restored and rebuilt the original framework in-process before its restart.
Early recovery copies backups (rather than consuming them), so restoration can be retried after a failed build.
Only this independent path restores a project.godot backup and reloads settings before restart; normal rollback
uses recipe/autoload operations against the live editor settings.

Implemented milestones M0b and M1–M8 are committed separately, with an additional final validation/fix commit.
Automated coverage includes 106 headless tests. Runtime fixtures verified a managed directory rename with game
code using its API, self-update compile failure, and a framework that compiles but cannot initialize. GUI modal
input, Windows locks/hidden attributes, authenticated SSH/self-hosted GitLab transport, and the full multi-plugin
NuGet/project-reference manual matrix still require release-platform checks; they are not claimed as verified.
