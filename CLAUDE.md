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
`EPlugin Framework.csproj/.sln`).

### Repository layout

- `src/eplugin-framework/addons/ePlugin/` — **the main part of this project, the framework itself**. This is
  what you'll be editing.
- `src/eplugin-framework/addons/sample_plugin/`, `sample_dependant_plugin/`,
  `sample_addedcode_plugin/`, `sample_optional_plugin/` — any addon under `addons/` whose name starts
  with `sample` is a reference plugin demonstrating usage of
  the ePlugin framework, bundled with full source. Treat these as **read-only**: don't modify them unless
  explicitly asked. They're useful reading — minimal, working examples of how to consume the
  `IEEditorPlugin` API (see Architecture below).
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
dotnet build "src/eplugin-framework/EPlugin Framework.sln"
```

Run tests (gdUnit4 via its VSTest adapter — requires the `GODOT_BIN` environment variable to point at a
Godot .NET/Mono editor binary, since tests spin up a headless Godot runtime):
```
dotnet test "src/eplugin-framework/EPlugin Framework.sln"
```
Run a single test:
```
dotnet test "src/eplugin-framework/EPlugin Framework.sln" --filter "FullyQualifiedName~IDotnetCliTests.VersionTest"
```
Alternative: gdUnit4's own CLI runner (`src/eplugin-framework/addons/gdUnit4/runtest.cmd` /
`runtest.sh`), which takes `--godot_binary <path>` or the same `GODOT_BIN` env var.

Note: gdUnit4 editor tests are not currently possible in Godot (upstream
[gdUnit4#911](https://github.com/MikeSchulze/gdUnit4/issues/911)), so tests exercise framework internals
directly (e.g. `EGlobal`) with a mocked `EPluginPlugin` (Moq) rather than driving the real editor UI.

There is no lint command configured beyond the `.editorconfig` (UTF-8 charset only).

## Architecture

### Bootstrap and lifecycle

`EPluginPlugin` (`addons/ePlugin/EPluginPlugin.cs`) is the framework's own `EditorPlugin`
(`addons/ePlugin/plugin.cfg` → `script="EPluginPlugin.cs"`). Its `_Process` checks
`EGlobal.Instance.IsValid()` every frame and re-initializes if not — this is the workaround for Godot's C#
assembly-reload behavior, which wipes static state (including `EGlobal`) without re-running
`_EnterTree`/`_EnablePlugin`.

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
`PluginContext.AppliedOptionalRecipes` so uninstall reverses exactly that (falling back to re-resolving when
the snapshot was lost to an assembly reload).

Activation order does not matter: after installing a plugin, `ReevaluateOptionalDependencies` re-checks every
other already-installed plugin and applies the nested recipes that just became satisfied. The baseline of
"already installed" comes from that plugin's snapshot, or — when it was lost to an assembly reload — from
`ResolveOptionalRecipes(..., ignoreSlug: <the plugin just enabled>)`, so only the genuinely new recipes are
applied. Recipes stay in the snapshot once applied even if they stop being satisfied, so uninstall still
reverses them. The reverse direction is deliberately not wired up: optional dependencies take no part in the
reverse-dependency scan in `DisableEPlugin`, so disabling an optional plugin neither cascades nor uninstalls
the nested recipe it enabled.

`PluginContext` (`addons/ePlugin/Internal/PluginContext.cs`) is the per-plugin state bag: the `EditorPlugin`
instance, its `IEEditorPlugin`/metadata/slug, its logger, its `IDotnetCli`, its recipe builder, and its
`EEditorPluginState`.

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

### Logging

`ILogger`/`ILoggerFactory` are pluggable (`GodotConsoleLogger`, `NullLogger`, `GenericLoggerFactory`).
`EGlobal.SwitchLogging` lets a dependent plugin (e.g. an external logging plugin) take over ePlugin's own
console logging at runtime — this is why logger wiring goes through the factory rather than being
constructed once and cached globally.

### Directory show/hide

`ShowHideHelper` implements `AddDirectory`: it toggles a managed directory's visibility by renaming it with/
without a leading `.` (POSIX-style hidden dirs) and toggling the Windows hidden attribute. Used for shipping
plugin source that should stay inert until the plugin is activated.

## Known constraints (see README for full list)

- Activating/deactivating an ePlugin freezes the Godot editor UI for the duration of the install/uninstall.
- A failure mid-install/uninstall can leave the project in a non-compilable state requiring manual fixup.
- The Godot editor's plugin UI does not auto-refresh; newly-activated dependent plugins only appear after
  reopening the plugin UI.
- External Assets (scenes, models, non-code resources) aren't supported directly — only whole directories
  toggled via `AddDirectory`, since Godot requires a unique ID per asset that external files can't have.
- Godot 4.5+ has a regression with `EditorPlugin` ([godotengine/godot#110971](https://github.com/godotengine/godot/issues/110971)); this is the reason `IEEditorPlugin` is an interface, not a base class.
