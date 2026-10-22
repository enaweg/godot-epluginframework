# ePlugin for GDScript plugins

ePlugin Framework is written in C#, but most of what it offers works for every plugin below `res://addons`, GDScript plugins included. Like every plugin, a GDScript plugin needs a `plugin.cfg` in its folder; scripts without one are not supported. A GDScript plugin needs no code for it: it opts in with keys in `plugin.cfg` and with files in its folder. Godot ignores keys it does not know, so the same plugin keeps working unchanged in projects without ePlugin.

The project using the plugin needs the Godot .NET editor and a .NET SDK, because ePlugin itself is a C# plugin (see [Requirements and installation](../README.md#requirements-and-installation)). The GDScript plugin stays pure GDScript.

## What a GDScript plugin gets

| Feature | What the plugin provides | Details |
|---|---|---|
| Listed, enabled and disabled in the ePlugin Manager, with progress window | Nothing | [ePlugin Manager](#in-the-eplugin-manager) |
| Updates from GitHub, GitLab, Forgejo or Git | `update_url` in `plugin.cfg` | [Updates](#updates) |
| Downgrade or reinstall a published version | `update_url` in `plugin.cfg` | [Updates](#updates) |
| Updates from local ZIP packages | A ZIP with one plugin folder | [Publish a release](#publish-a-release) |
| Updates without an `update_url` of its own | Nothing; the project or ePlugin's built-in list sets the site | [Plugins without an update_url](#plugins-without-an-update_url) |
| License shown, or accepted before enabling | `LICENSE`, `license_file`, `license_required` | [License](#license) |
| Welcome page shown once per project | `README` or `welcome_file` | [Welcome page](#welcome-page) |
| Documentation and source links | `documentation_url`, `source_url` | [Documentation and source links](#documentation-and-source-links) |
| Required or optional dependency of an ePlugin | A stable folder name and `version` | [Using a GDScript plugin from an ePlugin](#using-a-gdscript-plugin-from-an-eplugin) |
| Plugin state shared with the team | A valid `version` | [Plugin state](#plugin-state) |

NuGet packages, C# projects, managed autoloads and directories, dependencies of its own, startup initializers, and the progress and logging APIs need C#. See [When a plugin needs C#](#when-a-plugin-needs-c) for a way to add them without converting the GDScript plugin.

## Example plugin

A GDScript plugin using every option described here:

```text
addons/my_tool/
├── plugin.cfg
├── plugin.gd
├── dock.tscn
├── LICENSE
├── README.md
└── WELCOME.txt
```

```ini
[plugin]

name="My Tool"
description="Adds a dock for editing level data."
author="Example Author"
version="1.2.0"
script="plugin.gd"

update_url="https://github.com/owner/my_tool/releases"
documentation_url="https://github.com/owner/my_tool#readme"
source_url="https://github.com/owner/my_tool"
license_required=true
welcome_file="WELCOME.txt"
```

`plugin.gd` is an ordinary Godot editor plugin. Nothing in it refers to ePlugin:

```gdscript
@tool
extends EditorPlugin

var _dock: Control


func _enter_tree() -> void:
	_dock = preload("res://addons/my_tool/dock.tscn").instantiate()
	add_control_to_dock(DOCK_SLOT_RIGHT_UL, _dock)


func _exit_tree() -> void:
	remove_control_from_docks(_dock)
	_dock.queue_free()
```

Every key below `script` is optional. Leave out what the plugin does not need: without `license_required` the license is only shown, and without `welcome_file` the README is the welcome page.

## In the ePlugin Manager

The [ePlugin Manager](eplugin-manager.md) lists every plugin below `res://addons`, enabled or not. A plugin whose `script` ends in `.gd` has the type *GDScript plugin*. Its details show the author, version, description, license, welcome page, documentation and source links, update site and any earlier failures.

Enabling or disabling it, in the manager or in **Project Settings > Plugins**, is handled like for an ePlugin: a required [license](#license) is asked for first, ePlugins that require it are disabled with it, and optional recipes that depend on it are installed or removed (see [Using a GDScript plugin from an ePlugin](#using-a-gdscript-plugin-from-an-eplugin)). Longer work shows the [progress window](progress-dialog.md).

## Updates

Add `update_url` to `plugin.cfg` to receive updates. Any [supported source](updating-plugins.md#configure-an-update-source) works:

```ini
; GitHub, GitLab, Forgejo or Gitea (Codeberg) releases
update_url="https://github.com/owner/my_tool/releases"

; Git tags, for repositories that tag versions without release ZIPs
update_url="https://github.com/owner/my_tool/tags"

; the plugin folder of a Git branch, for example in a repository that holds a whole demo project
update_url="https://github.com/owner/my_tool.git?path=addons/my_tool#main"
```

Only enabled plugins are checked. The update is offered in the manager like any other: check it in the **Version** column and choose **Update**, or choose a version in the plugin's details to downgrade or reinstall. Before the files are replaced, the updater validates the package, saves and closes open scenes, and disables the plugin; it enables it again afterwards. A package without C# files is installed without a build and without restarting the editor. A failed installation is rolled back.

An update replaces the whole plugin folder, `plugin.cfg` included, so every release must contain the `update_url` again. The review before installation warns when the plugin's name, `update_url` or major version changes, and when its entry script changes language, for example from `plugin.gd` to a C# script. See [Review before installation](updating-plugins.md#review-before-installation).

### Publish a release

A release is a ZIP with exactly one plugin folder, named like the plugin's folder in `res://addons`:

```text
my_tool-1.2.0.zip
└── addons/
    └── my_tool/
        ├── plugin.cfg
        ├── plugin.gd
        └── ...
```

The folder can also sit at the ZIP's root (`my_tool/plugin.cfg`) or inside one wrapper folder, as in a repository source archive. For example, from the project root:

```bash
zip -r my_tool-1.2.0.zip addons/my_tool
```

Attach the ZIP to a GitHub, GitLab, Forgejo or Gitea (Codeberg) release, or put it in a [local directory](updating-plugins.md#local-directories). Without a ZIP asset, the release's source archive is used; repositories that only tag their versions use the tag source above.

- Raise `version` in `plugin.cfg` for every release. Versions have two to four numbers (`1.2`, `1.2.0`, `1.2.0.1`), optionally a leading `v` and a prerelease suffix such as `-beta.1`. Name the release or tag after the same version.
- Keep the folder name stable. It is the plugin's slug, which update sites and dependencies refer to.
- Do not include `project.godot`, `nuget.config`, symlinks or submodules. A plugin with a GDExtension (`.gdextension` files) is a [GDExtension plugin](gdextension-plugins.md), which has a few more rules.

All rules are in [Publish an updatable release](updating-plugins.md#publish-an-updatable-release).

### Plugins without an update_url

A project can update a GDScript plugin that has no `update_url`, or whose `update_url` stopped working, without changing the plugin. The **Update sources** tab of the ePlugin Manager's **Settings** stores a site per plugin in the committed `res://addons/eplugin-update-sites.json`:

```json
{
  "schema": 1,
  "sites": [
    { "slug": "my_tool", "url": "https://github.com/owner/my_tool/releases" }
  ]
}
```

Popular add-ons are also covered by ePlugin's [built-in update sites](updating-plugins.md#built-in-update-sites), which many GDScript add-ons are part of. A ZIP in a local directory works for any plugin as well. See [Project update sites](updating-plugins.md#project-update-sites).

## License

Ship a `LICENSE` file in the plugin folder. The ePlugin Manager shows it in the plugin details, and shows it as missing when there is none. To use another file, name it with `license_file`:

```ini
license_file="docs/EULA.txt"
```

The path is relative to the plugin folder. The license is shown as [BBCode](https://docs.godotengine.org/en/stable/tutorials/ui/bbcode_in_richtextlabel.html); plain text works too.

Set `license_required=true` to have users accept the license before the plugin is enabled, from the ePlugin Manager or from **Project Settings > Plugins**. Declining, or closing the dialog, leaves the plugin disabled; accepting enables it.

The manager asks before it enables the plugin. Project Settings enables a GDScript plugin before ePlugin can step in, so the framework disables it again in the next frame and shows the license then. The plugin's `_enable_plugin` and `_disable_plugin` therefore run once before the license is accepted; keep them free of work that should wait for the license.

An acceptance is recorded once per project and stays valid while the license comes from the same file path. To have users accept a changed license, ship it under a new name, such as `LICENSE-2.0.md`. An update that names a new license file, or requires a license for the first time, shows it before installation. See [Plugin licenses](plugin-licenses.md).

## Welcome page

The plugin's README (`README.md`, `README.txt` or `README`) is shown once per project after the plugin is enabled, from the ePlugin Manager or from **Project Settings > Plugins**. A README is shown as it is, without converting Markdown, so a BBCode page usually looks better. Name it with `welcome_file`:

```ini
welcome_file="WELCOME.txt"
```

```text
[b]Thanks for installing My Tool![/b]

Open the [i]My Tool[/i] dock on the right to start.

[url=https://github.com/owner/my_tool#readme]Documentation[/url]
```

A plugin that asks for its license shows its welcome page once the license is accepted. Updates never show a welcome page. The page can be read again from the plugin details. See [Plugin welcome pages](plugin-welcome.md).

## Documentation and source links

`documentation_url` and `source_url` add links to the plugin details. `http` and `https` links open in the browser; other values, such as an SSH Git address, are shown as text. For add-ons in ePlugin's built-in list, the list fills in links that `plugin.cfg` does not set. See [Documentation and source links](creating-plugins.md#documentation-and-source-links).

## Using a GDScript plugin from an ePlugin

An ePlugin can require a GDScript plugin. Enabling the ePlugin then enables the GDScript plugin first, and a version constraint is checked against the `version` in the GDScript plugin's `plugin.cfg`:

```csharp
public void CreateRecipe(IEEditorPluginBuilder builder)
{
    builder.AddPluginDependency("my_tool", ">=1.2.0"); // 1.2.0 or higher
}
```

An ePlugin can also declare an optional dependency on a GDScript plugin. Its nested recipe is applied when the ePlugin is enabled while the GDScript plugin is enabled and matches the version:

```csharp
builder.AddOptionalPluginDependency("my_tool", ">=1.0", optional => optional
    .AddDirectory($"{this.GetPluginDirectory()}/.my-tool-integration"));
```

A GDScript plugin runs no ePlugin code itself, so the framework watches for it being enabled or disabled, in the ePlugin Manager or in **Project Settings > Plugins**:

- Disabling the GDScript plugin also disables the ePlugins that require it, and removes the optional recipes of other ePlugins that depend on it. The project is built once for all of them.
- Enabling it installs the optional recipes of enabled ePlugins that it satisfies now, so it does not matter whether the ePlugin or the GDScript plugin is enabled first.

Updates of plugins that depend on each other are installed together, also when one of them is a GDScript plugin. See [Create an ePlugin](creating-plugins.md#recipe-resources).

## Plugin state

ePlugin records the version and enabled state of GDScript plugins in the committed `res://addons/eplugin-state.json`, next to ePlugins, so the whole team shares them. Their state is read at editor start and before **Retry failed**, including plugins toggled in Project Settings. A plugin without a `version` in `plugin.cfg` is recorded as invalid; add the version and use **Retry failed** in the manager. See [Plugin state files](plugin-state.md).

## When a plugin needs C#

Recipes, dependencies of its own, startup initializers, `EPlugin.Progress` and ePlugin's logging are C# APIs. A GDScript plugin that needs them can stay as it is, with a small companion ePlugin in its own folder that requires it:

```text
addons/my_tool/                 GDScript plugin, unchanged
addons/my_tool_dotnet/
├── plugin.cfg                  script="MyToolDotnetPlugin.cs"
├── MyToolDotnetPlugin.cs
└── .src/                       C# code that needs the NuGet package
```

```csharp
#if TOOLS
using Godot;
using Enaweg.Plugin;

[Tool]
public partial class MyToolDotnetPlugin : EditorPlugin, IEEditorPlugin
{
    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder
            .AddPluginDependency("my_tool", ">=1.2.0")
            .AddNuget("Newtonsoft.Json", "13.0.3")
            .AddDirectory($"{this.GetPluginDirectory()}/.src");
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();
        this.EnableEPlugin();
    }

    public override void _DisablePlugin()
    {
        base._DisablePlugin();
        this.DisableEPlugin();
    }
}
#endif
```

Enabling `my_tool_dotnet` enables `my_tool` first, then installs the NuGet package and makes `src` visible; disabling it reverses that and leaves `my_tool` enabled. Projects without .NET install only `my_tool`.

The alternative is to make the plugin's entry script a C# ePlugin and keep the rest in GDScript. The plugin then always needs .NET and ePlugin, and its first update to the C# entry script shows an *entry-point language changed* warning. See [Create an ePlugin](creating-plugins.md).
