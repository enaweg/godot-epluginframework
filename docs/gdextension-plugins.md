# ePlugin for GDExtension plugins

A GDExtension plugin is a plugin below `res://addons` with a `plugin.cfg` that also ships a GDExtension: a `.gdextension` file and the native libraries it names. Its entry script can be GDScript or C#. The ePlugin Manager lists such a plugin with the type *GDExtension plugin*.

A GDExtension without a `plugin.cfg`, in its own folder below `res://addons`, is a *GDExtension add-on*. It is not a plugin for Godot, but ePlugin lists it, updates it and lets ePlugins depend on it; see [GDExtensions without plugin.cfg](#gdextensions-without-plugincfg).

An ePlugin (a C# plugin with a recipe) can ship a GDExtension too. It is listed as an ePlugin, and its updates replace the libraries the same way (see [ePlugins with a GDExtension](#eplugins-with-a-gdextension)).

Everything [GDScript plugins](gdscript-plugins.md) get works the same way for GDExtension plugins: the manager, updates from GitHub, GitLab, Forgejo, Git or local ZIP packages, licenses, welcome pages, links, plugin state, and being a dependency of an ePlugin. This page describes only what is different.

## Example plugin

```text
addons/my_native/
├── plugin.cfg               script="plugin.gd"
├── plugin.gd
├── my_native.gdextension
└── bin/
    ├── my_native.windows.x86_64.dll
    ├── libmy_native.linux.x86_64.so
    └── libmy_native.macos.framework/
```

```ini
[configuration]
entry_symbol = "my_native_init"
compatibility_minimum = "4.4"
reloadable = true

[libraries]
windows.x86_64 = "res://addons/my_native/bin/my_native.windows.x86_64.dll"
linux.x86_64 = "bin/libmy_native.linux.x86_64.so"
macos = "bin/libmy_native.macos.framework"
```

Godot loads the extension whether the plugin is enabled or not. Enabling the plugin only runs its entry script.

## In the ePlugin Manager

The details of a plugin with a GDExtension list each `.gdextension` file: whether Godot loaded it, whether it is reloadable, and the platforms it ships libraries for. A GDExtension that names a library for the editor's platform but that Godot did not load turns the plugin red, and so does the toolbar button's puzzle piece, with the reason in the details:

- the library for this platform is missing,
- the extension needs another Godot version (its `compatibility_minimum` or `compatibility_maximum`), or
- Godot could not load it. Restart the editor; if that does not help, the output log says why.

A GDExtension without a library for the editor's platform is meant for other platforms and needs no attention. GDExtensions in hidden folders (such as a managed directory while its plugin is disabled) are not listed, because Godot does not load them.

## GDExtensions without plugin.cfg

Many GDExtensions ship only the `.gdextension` file and their libraries, without a `plugin.cfg`:

```text
addons/godot_servo/
├── godot_servo.gdextension
└── bin/
    ├── windows/godot_servo.x86_64.dll
    └── linux/libgodot_servo.x86_64.so
```

ePlugin treats every folder directly below `res://addons` that has no `plugin.cfg` but a `.gdextension` file (outside hidden folders) as a GDExtension add-on:

- **Manager.** It is listed with the type *GDExtension add-on* and the status *Always loaded*: Godot loads it whatever is enabled, so there is nothing to enable or disable. Its name is the folder name, or the name in ePlugin's built-in list. Its license is its `LICENSE` file and its README is linked as its welcome page, but neither is ever asked for or shown on its own.
- **Version.** Its files name no version. ePlugin records the version it installs in `addons/eplugin-state.json`, like the versions of plugins. Until ePlugin has installed one, the version is *unknown*.
- **Updates.** Its update site is the one the project sets for it on the **Update sources** tab of the manager's **Settings**, or the one of ePlugin's [built-in list](updating-plugins.md#built-in-update-sites). The version is the release's or tag's, so it can be updated from GitHub, GitLab and Forgejo releases and from version tags, but not from a pinned branch or from local directories. While its version is unknown, checks offer no update for it, but they look up its latest release: its details preselect that version, and **Install** installs it (or another version you choose). The latest release a check found is offered even when the update site's versions cannot be listed, e.g. while GitHub's rate limit is reached. From then on, updates are found as for any plugin. An add-on whose new version brings a `plugin.cfg` becomes a plugin with that update; enable it afterwards.
- **Dependencies.** An ePlugin can require it with `AddPluginDependency` or `AddOptionalPluginDependency`. It is always available, so it is never enabled for it. A version constraint is checked against the recorded version; while that is unknown, an activation with a constraint fails and says so.

A release of a GDExtension add-on puts the add-on in a folder named like its slug, at any depth (`addons/godot_servo/` in the ZIP or the repository). Without a `plugin.cfg`, nothing else identifies it. Otherwise the [rules for releases](#publish-a-release) apply as for GDExtension plugins.

## Updates

An update of a GDExtension plugin replaces native libraries that the running editor uses:

- On Windows, the editor loads every GDExtension library from a copy in the same folder, named like the library with a leading `~` (for example `~my_native.windows.x86_64.dll`, plus `~…_0.pdb` for its debug symbols), and keeps that copy open. The original library can be deleted and replaced, but the plugin folder cannot be moved as a whole. ePlugin therefore replaces the plugin's files one by one and leaves the `~` copies where they are. Godot deletes them itself when it unloads the library.
- On Linux and macOS, the editor loads the original library. ePlugin deletes the old file before it writes the new one, so the running editor keeps using the old library until it loads the new one.

Before any file changes, the old version is copied completely to the update's backup. If the update fails, or is rolled back later, the old files are put back the same way.

After the files are replaced, the editor needs to load the new libraries:

| The extension | What happens |
|---|---|
| has `reloadable = true` in its `[configuration]`, in the installed and in the new version | ePlugin reloads it in place. The editor does not restart for it. |
| is not reloadable, or a `.gdextension` file was added or removed | The editor restarts once the update is finished. |
| could not be reloaded, or one of its old files was still in use and could only be moved aside | The editor restarts once the update is finished. Files moved aside are deleted with the update's journal after the restart. |

The review before installation says which of these applies. A rollback reloads or restarts the same way if the editor already runs the new libraries.

A plugin with a C# entry script is also built like a [C# plugin](updating-plugins.md#builds-restart-and-recovery), which restarts the editor anyway. GDExtension add-ons are updated the same way, but are never disabled, since nothing enabled them.

### ePlugins with a GDExtension

An ePlugin stays enabled during its update, as always. When its installed or new version ships a GDExtension, its files are replaced one by one like a GDExtension plugin's, next to the `~` copies. Every ePlugin update builds the project and restarts the editor into the new assembly, and that restart loads the new libraries as well, so nothing is reloaded in place. A rollback restarts the same way. The rules of [Publish a release](#publish-a-release) apply to its package too.

ePlugin Framework itself ships no GDExtension, and a framework package with one is refused.

### GDExtensions in managed directories

A directory that a recipe manages with `AddDirectory` can hold a GDExtension when every `.gdextension` file in it has `reloadable = true`. Godot does not load extensions in hidden folders, so:

- enabling the plugin shows the directory and loads its extensions right away;
- disabling it unloads them first and then hides the directory. Unloading also closes the copies the editor on Windows keeps open, which would keep the directory from being renamed.

A GDExtension that is not reloadable cannot be unloaded safely while the editor runs, so its directory could not be hidden again. Enabling the plugin then fails before the directory is shown, and says so. Keep such an extension outside managed directories.

### Publish a release

Publish a release as for a [GDScript plugin](gdscript-plugins.md#publish-a-release), with the native libraries in the plugin folder. In addition:

- Every library and dependency that the `.gdextension` names must be inside the plugin folder, as a `res://addons/<slug>/…` path or a path relative to the `.gdextension` file. A library elsewhere in the project, or an absolute path, is refused, because an update could not replace it. Icons (`[icons]`) outside it are reported, since an update does not replace them either.
- Include the library for every platform you support. When the `.gdextension` names a library for the editor's platform that the package does not contain, the update is refused. When it names none for that platform, the review warns that Godot will not load the extension. When a platform, or the release library of one, that the installed version supports is missing from the new one, the review warns: other team members or exported games may run on it.
- Set `compatibility_minimum` (and `compatibility_maximum`, if any) to the Godot versions the extension supports. An update that the editor's Godot version cannot load is refused, before anything is replaced.
- Do not include Godot's `~` copies. They are left out of packages anyway, so a ZIP made from a project that the editor had open still works.
- Set `reloadable = true` when the extension supports it, so updates do not restart the editor.
- A plugin folder that is a link (a symlink or junction to another checkout) is not updated. Update it where it points to.
- When a release has several ZIP assets, ePlugin downloads the one named exactly like the slug, else the one whose name contains it (`-`, `_` and `.` count as the same), else an addon- or plugin-named one; among several such ZIPs, demo, example, sample and tutorial ZIPs are left out (see [Updating plugins](updating-plugins.md#configure-an-update-source)). Name a variant differently (`my_native+extras.zip` next to `my_native.zip`), or ePlugin falls back to the source archive, which has no built libraries.

| Code | Severity | Reason |
|---|---|---|
| R2 | Error | A GDExtension add-on's package has neither a `plugin.cfg` nor a `.gdextension` file. |
| R10 | Error | A package of ePlugin Framework ships a GDExtension. |
| R18 | Error | A `.gdextension` file cannot be read, or names a library outside the plugin folder. |
| R18 | Warning | A `.gdextension` file names an icon outside the plugin folder. |
| R19 | Error | The library for the editor's platform is missing from the package. |
| R19 | Warning | The `.gdextension` names no library for the editor's platform. |
| R20 | Error | The plugin folder is a link. |
| R21 | Info | Whether the extension is reloaded in place or the editor restarts. |
| R22 | Error | The editor's Godot version cannot load the extension: it is outside `compatibility_minimum`/`compatibility_maximum`, or `compatibility_minimum` is missing or older than 4.1. |
| R23 | Warning | The new version no longer ships a platform, or the release library of a platform, that the installed version has. |
| R24 | Info | A GDExtension add-on's new version has a `plugin.cfg`, so it becomes a plugin. |
