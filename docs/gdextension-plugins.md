# ePlugin for GDExtension plugins

A GDExtension plugin is a plugin below `res://addons` with a `plugin.cfg` that also ships a GDExtension: a `.gdextension` file and the native libraries it names. Its entry script can be GDScript or C#. The ePlugin Manager lists such a plugin with the type *GDExtension plugin*.

Only GDExtensions with a `plugin.cfg` are supported: ePlugin supports only plugins with a `plugin.cfg`, whatever their type. A GDExtension without one is not a plugin for Godot or ePlugin: the manager does not list it and ePlugin does not update it.

An ePlugin (a C# plugin with a recipe) can ship a GDExtension too. It is listed as an ePlugin, and its updates replace the libraries the same way (see [ePlugins with a GDExtension](#eplugins-with-a-gdextension)).

GDExtensions in a directory that a recipe manages with `AddDirectory` are not supported. Enabling and disabling the plugin shows and hides that directory by renaming it, and once Godot has loaded a library from it, the copy the editor keeps open (see [Updates](#updates)) prevents the rename on Windows. Keep the `.gdextension` file and its libraries outside managed directories.

Everything [GDScript plugins](gdscript-plugins.md) get works the same way for GDExtension plugins: the manager, updates from GitHub, GitLab, Git or local ZIP packages, licenses, welcome pages, links, plugin state, and being a dependency of an ePlugin. This page describes only what is different.

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

A plugin with a C# entry script is also built like a [C# plugin](updating-plugins.md#builds-restart-and-recovery), which restarts the editor anyway.

### ePlugins with a GDExtension

An ePlugin stays enabled during its update, as always. When its installed or new version ships a GDExtension, its files are replaced one by one like a GDExtension plugin's, next to the `~` copies. Every ePlugin update builds the project and restarts the editor into the new assembly, and that restart loads the new libraries as well, so nothing is reloaded in place. A rollback restarts the same way. The rules of [Publish a release](#publish-a-release) apply to its package too.

ePlugin Framework itself ships no GDExtension, and a framework package with one is refused.

### Publish a release

Publish a release like for a [GDScript plugin](gdscript-plugins.md#publish-a-release), with the native libraries in the plugin folder. In addition:

- Every library and dependency that the `.gdextension` names must be inside the plugin folder, as a `res://addons/<slug>/…` path or a path relative to the `.gdextension` file. A library elsewhere in the project, or an absolute path, is refused, because an update could not replace it.
- Include the library for every platform you support. When the `.gdextension` names a library for the editor's platform that the package does not contain, the update is refused. When it names none for that platform, the review warns that Godot will not load the extension.
- Do not include Godot's `~` copies. They are left out of packages anyway, so a ZIP made from a project that the editor had open still works.
- Set `reloadable = true` when the extension supports it, so updates do not restart the editor.
- A plugin folder that is a link (a symlink or junction to another checkout) is not updated. Update it where it points to.
- Keep the GDExtension out of directories a recipe manages with `AddDirectory`; that is not supported.

| Code | Severity | Reason |
|---|---|---|
| R10 | Error | A package of ePlugin Framework ships a GDExtension. |
| R18 | Error | A `.gdextension` file cannot be read, or names a library outside the plugin folder. |
| R19 | Error | The library for the editor's platform is missing from the package. |
| R19 | Warning | The `.gdextension` names no library for the editor's platform. |
| R20 | Error | The plugin folder is a link. |
| R21 | Info | Whether the extension is reloaded in place or the editor restarts. |
