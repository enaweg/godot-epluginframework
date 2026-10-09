# Plugin licenses

A plugin can ask users to accept its license before it is enabled. The framework then shows the license and enables the plugin only once it is accepted. Every plugin's license can be read again from the [ePlugin Manager](eplugin-manager.md).

## Where the license comes from

The framework uses the first of these that the plugin provides:

1. The license set by an ePlugin's recipe with `SetLicense` (see below).
2. The `license_file` in the `[plugin]` section of `plugin.cfg`.
3. The `LICENSE` file in the plugin's root directory.

Every plugin should ship a `LICENSE` file. If a plugin has none, and sets no other license, the ePlugin Manager shows its license as missing.

The license is shown as [BBCode](https://docs.godotengine.org/en/stable/tutorials/ui/bbcode_in_richtextlabel.html), so it can use formatting such as `[b]`, `[i]`, `[color]` and `[url]`. Plain text works too. Links open in the browser only when they use `http` or `https`. A license file can be up to 1 MiB.

### In plugin.cfg

```ini
[plugin]

name="My Plugin"
version="1.0.0"
script="MyPlugin.cs"
license_required=true
license_file="docs/EULA.txt"
```

`license_required=true` asks for the license to be accepted; without it the license is only shown. `license_file` is optional. It must be a relative path inside the plugin directory; `res://addons/<slug>/...` is accepted as well. Paths that leave the plugin directory are refused.

### In the recipe

An ePlugin can set its license in `CreateRecipe`, either as text or as the `res://` path of a file:

```csharp
public void CreateRecipe(IEEditorPluginBuilder builder)
{
    builder.SetLicense($"{this.GetPluginDirectory()}/legal/EULA.txt");
    // or: builder.SetLicense("[b]My Plugin License[/b]\n\nYou may ...");
}
```

A value that starts with `res://` is a path, anything else is the license text. `SetLicense` replaces the `license_file` of `plugin.cfg`. Whether the license has to be accepted is still decided by `license_required` in `plugin.cfg`, because the framework needs to know that before it runs plugin code, for example when it checks a downloaded update.

## Enabling a plugin

The license dialog opens when a plugin with `license_required=true` is enabled from **Project Settings > Plugins** or from the [ePlugin Manager](eplugin-manager.md), before anything is installed:

- **Accept** enables the plugin.
- **Decline**, or closing the dialog, leaves the plugin disabled. Nothing is installed.

The plugin shows as disabled in **Project Settings > Plugins** while the dialog is open.

When enabling a plugin also enables disabled dependencies that ask for a license, all of these licenses are collected into one dialog. The list on the left shows each plugin and whether its license was accepted or declined. **Accept all** accepts every license that is not decided yet. A plugin is enabled only if all the licenses it needs are accepted. Declining a dependency's license leaves the plugin that needs it disabled as well.

A license is accepted once per project. Disabling and enabling the plugin again does not ask again. In a headless editor no dialog can be shown, so a plugin that needs a license is not enabled unless licenses are accepted automatically.

## When a license counts as accepted

A license stays accepted while it comes from the same place: the same license file path, the same `res://` path set by the recipe, or, for a license text set by the recipe, the same text. The contents of a file are not compared. Plugin authors who want an updated license file to be accepted again should therefore ship it under a new name, such as `LICENSE-2.0.md`.

At startup and after C# assembly reloads, the framework also checks the enabled plugins that require a license. A plugin whose license is not accepted as it is now asks for it again: accepting keeps the plugin enabled, declining disables it. This happens when an update changed the license its recipe sets, when a plain plugin was enabled directly in **Project Settings > Plugins**, or when a plugin was enabled before its acceptance was recorded.

## Updates

An update whose license is unchanged is installed without asking again. When the new version names a different license file, or asks for a license for the first time, the license is shown before installation. If you decline the license of one update in a batch, that update is skipped and the remaining updates are installed.

A license set by a recipe is only known once the new code is compiled. Such a license is checked after the update instead: if it changed, the dialog asks for it, and declining disables the plugin.

## Reading a license again

The plugin details of the [ePlugin Manager](eplugin-manager.md) show each plugin's license. Click it to open the license again. For a plugin that requires a license, the details also show whether and when it was accepted. A missing `LICENSE` file is shown as missing.

## Accepting licenses automatically

The project setting `eplugin/licenses/auto_accept` (Project Settings > General, with Advanced Settings shown) accepts every license without showing it. It is stored in `project.godot`, so it applies to everyone working on the project.

> [!WARNING]
> This is risky. You agree to license terms nobody has read, which may for example restrict commercial use, require attribution or forbid redistribution. Each license accepted this way is logged with a warning and recorded as accepted automatically.

It is mainly meant for headless editors, such as CI exports, where no dialog can be shown.

## Recorded acceptances

Accepted licenses are recorded in the `licenses` list of `res://addons/eplugin-state.json`, the [plugin state file](plugin-state.md), next to the plugin states. Each entry holds the plugin slug, where the license came from, the plugin version, the time, and whether it was accepted automatically. Commit the file with the addon files.

The `licenses` list is left out while no license has been accepted, so the file stays readable by framework versions without license support. Once it holds licenses, such older versions cannot read the file. If the file cannot be read or changed on disk in the meantime, licenses accepted in that state are remembered until the editor restarts and are asked for again afterwards.
