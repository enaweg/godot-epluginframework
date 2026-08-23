# AGENTS.md

## Project overview

ePlugin Framework is a C# editor-plugin framework for Godot 4.4-4.7, targeting `net8.0`.
The Godot project and solution are under `src/eplugin-framework/`.

## Repository guidance

- Edit framework code under `src/eplugin-framework/addons/ePlugin/`.
- Treat `addons/sample_plugin/`, `addons/sample_dependant_plugin/`, and
  `addons/sample_addedcode_plugin/` as read-only usage examples unless explicitly asked to
  change them.
- Treat `addons/gdUnit4/` as a read-only vendored dependency unless explicitly asked to change it.
- Tests are under `src/eplugin-framework/tests/`.
- Most framework code is wrapped in `#if TOOLS` because it runs only in the Godot editor.

## Build and test

```text
dotnet build "src/eplugin-framework/EPlugin Framework.sln"
dotnet test "src/eplugin-framework/EPlugin Framework.sln"
```

Tests require `GODOT_BIN` to point to a Godot .NET/Mono editor executable. To run one test:

```text
dotnet test "src/eplugin-framework/EPlugin Framework.sln" --filter "FullyQualifiedName~IDotnetCliTests.VersionTest"
```

## Architecture notes

- `EPluginPlugin` bootstraps the framework and reinitializes state after Godot C# assembly reloads.
- `EGlobal` owns plugin contexts, lifecycle state, dependency resolution, and recipe installation.
- Consumer plugins implement `IEEditorPlugin.CreateRecipe` and call `EnableEPlugin()` /
  `DisableEPlugin()` from their Godot lifecycle methods.
- Recipe changes should go through the `IDotnetCli` abstraction; new CLI operations must be
  implemented for both .NET 9 and .NET 10 CLI implementations.
- Keep `IEEditorPlugin` as an interface; this works around Godot 4.5+ `EditorPlugin` regressions.

## Change conventions

- Keep recipe creation deterministic and side-effect free.
- Preserve existing public API and plugin lifecycle behavior unless the task explicitly changes it.
- Follow `.editorconfig` and existing C# style.
- There is no separate lint command configured.
