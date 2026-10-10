<div align="center">

<img src="./design/icon-16_9.png" alt="ePlugin framework logo" width="50%"/>

# ePlugin Framework

**Extended C# Plugin Framework for [Godot](https://godotengine.org/).**

[![CI](https://github.com/enaweg/godot-epluginframework/actions/workflows/ci-pr.yml/badge.svg)](https://github.com/enaweg/godot-epluginframework/actions/workflows/ci-pr.yml)
![Godot 4.4](https://img.shields.io/badge/Godot-v4.4-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)
![Godot 4.5](https://img.shields.io/badge/Godot-v4.5-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)
![Godot 4.6](https://img.shields.io/badge/Godot-v4.6-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)
![Godot 4.7](https://img.shields.io/badge/Godot-v4.7-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)

![Dotnet 8](https://img.shields.io/badge/8-02020?logo=dotnet&logoSize=auto&logoColor=purple&color=darkgreen&labelColor=E0E0E0)
![Dotnet 10](https://img.shields.io/badge/10-02020?logo=dotnet&logoSize=auto&logoColor=purple&color=darkgreen&labelColor=E0E0E0)

</div>

ePlugin Framework adds declarative dependency and resource management to Godot editor plugins written in C#. A plugin describes its requirements once, and the framework applies them when it is enabled and removes them when it is disabled.

## Requirements and installation

The project targets `net8.0`. The current CI-tested setup is [Godot 4.7.2 .NET](https://godotengine.org/download/archive/4.7.2-stable/) with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Other tested combinations are listed under [Testing](#testing).

Install [ePlugin Framework from the Godot Asset Store](https://store.godotengine.org/asset/enaweg/eplugin-framework/), or download a [release](https://github.com/enaweg/godot-epluginframework/releases) and copy its `addons/ePlugin` directory into your project's `addons` folder. Open the project in the Godot .NET editor, enable **ePlugin** under **Project > Project Settings > Plugins**, then enable the ePlugins you want to use.

Release archives include a small shared progress helper (about 20 KB) that runs on the installed .NET runtime and draws its window with native UI. Source checkouts do not include the generated helper; activation still works without a progress window. Linux needs GTK 3 for that window. Asset Library distributions should use the release archive to include the helper.

## Features

- **Declarative plugin recipes** install NuGet packages, C# projects, autoloads, and managed directories on activation, then reverse those changes on deactivation. NuGet sources are recorded in the project-root `nuget.config` for reproducible restores.
- **Plugin dependencies** enable required C# or GDScript plugins first, check optional version constraints, and disable dependants when a required plugin is disabled.
- **Optional dependencies** apply a nested resource recipe only while another plugin is already enabled and matches its version. They never enable the other plugin or fail the declaring plugin's activation.
- **Assembly reload recovery** rebuilds framework state from active plugins after Godot reloads C# assemblies. Startup initializers let managed code register `IInitialize` callbacks.
- **ePlugin Manager** lists plugins and their state, controls activation, checks updates, selects versions (including downgrades), manages local package directories, and retries interrupted or failed work.
- **Plugin updates** support GitHub and GitLab releases, Git sources, and ZIP packages in user-configured local directories. Local packages are indexed and can be rescanned; updates are validated, backed up, built, and recoverable while enabled ePlugins keep their recipes reconciled.
- **Plugin state tracking** records acknowledged versions and completed lifecycle operations in a project file, with separate per-user markers for interrupted or failed work.
- **Editor refresh** rescans the filesystem and rebuilds the solution after recipe changes. Project Settings is refreshed after activation and deactivation when possible.

| [ePlugin Manager](docs/eplugin-manager.md) | [Available updates](docs/updating-plugins.md#checks-and-installation) |
|---|---|
| ![ePlugin Manager showing the plugin overview](docs/images/eplugin-manager.png) | ![ePlugin Manager showing selected plugin updates](docs/images/eplugin-updates.png) |

Screenshots show the repository's sample plugins and example update packages.

## Documentation

- [Create an ePlugin](docs/creating-plugins.md): starter code, recipe resources, dependencies, helpers, and initializers.
- [ePlugin Manager](docs/eplugin-manager.md): inspect and control plugins, check updates, manage local sources, and retry failed work.
- [Updating plugins](docs/updating-plugins.md): configure update sources, publish packages, and understand validation, builds, and recovery.
- [Plugin state files](docs/plugin-state.md): checked-in state, per-user recovery state, and manual retry.
- [Plugin licenses](docs/plugin-licenses.md): ask users to accept a plugin's license before it is enabled or updated.
- [Plugin welcome pages](docs/plugin-welcome.md): greet users once after a plugin is installed, by default with its README.

The repository includes a sample project under `src/eplugin-framework` with examples for required and optional dependencies.

## Motivation and limitations

Godot plugins do not provide a built-in way to manage C# project references or NuGet packages, and external C# plugin code can trigger assembly reloads that discard editor state. ePlugin Framework handles those project changes around plugin lifecycle events.

Godot currently needs project-managed resource IDs for assets such as scenes, models, and scripts, so the framework cannot manage those assets outside the project. Plugin activation and deactivation can also freeze the editor while project changes are built. On supported desktop platforms the separate progress window remains responsive during that work.

## Testing

The current CI configuration builds and tests pull requests with Godot 4.7.2 and .NET 8. Other tested combinations are Godot 4.6.2, 4.5.1, or 4.4.1 with .NET 10.

```bash
cd src/eplugin-framework
dotnet build "EPluginFramework.sln" --configuration Debug
dotnet test "EPluginFramework.sln" --configuration Debug --settings .runsettings
```

Tests require a Godot .NET editor executable. See the [CI workflow](https://github.com/enaweg/godot-epluginframework/blob/main/.github/workflows/ci-pr.yml) for its headless setup. Godot 4.5 and newer have an [EditorPlugin regression](https://github.com/godotengine/godot/issues/110971), so the framework uses an interface-based plugin API.

## Plugins using ePlugin Framework

- [godot-elogger](https://github.com/enaweg/godot-elogger)
- [godot-econtainer](https://github.com/enaweg/godot-econtainer)
- [godot-emessagepipe](https://github.com/enaweg/godot-emessagepipe) — MessagePipe support for Godot .NET, with optional eContainer integration

## Contribute

Contributions to documentation, testing, and code are welcome.

Commercial services are available from [Enaweg](https://www.enaweg.at) for consulting, implementation assistance, and tailored development.

## License

Licensed under the [MIT license](LICENSE).
