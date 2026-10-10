# Plugin welcome pages

A plugin can greet users once it is installed, for example with first steps, links to its documentation, or the settings it adds. The framework shows the welcome page once per project after the plugin is enabled, and it can be read again from the [ePlugin Manager](eplugin-manager.md).

## Where the welcome page comes from

The framework uses the first of these that the plugin provides:

1. The welcome page set by an ePlugin's recipe with `SetWelcome` (see below).
2. The `welcome_file` in the `[plugin]` section of `plugin.cfg`.
3. The README in the plugin's root directory: `README.md`, `README.txt` or `README`, in this order. The case of the file name does not matter.

A plugin that provides none of these has no welcome page, and nothing is shown.

The welcome page is shown as [BBCode](https://docs.godotengine.org/en/stable/tutorials/ui/bbcode_in_richtextlabel.html), so it can use formatting such as `[b]`, `[i]`, `[color]` and `[url]`. Plain text works too. Links open in the browser only when they use `http` or `https`. A welcome file can be up to 1 MiB.

> [!NOTE]
> A README is shown as it is. Markdown is not converted, so headings and links appear as plain text. Use `welcome_file` or `SetWelcome` to show a BBCode page instead.

### In plugin.cfg

```ini
[plugin]

name="My Plugin"
version="1.0.0"
script="MyPlugin.cs"
welcome_file="docs/WELCOME.txt"
```

`welcome_file` is optional. It must be a relative path inside the plugin directory; `res://addons/<slug>/...` is accepted as well. Paths that leave the plugin directory are refused. A configured file that is missing or cannot be read is shown with an error, so the problem is noticed.

### In the recipe

An ePlugin can set its welcome page in `CreateRecipe`, either as text or as the `res://` path of a file:

```csharp
public void CreateRecipe(IEEditorPluginBuilder builder)
{
    builder.SetWelcome($"{this.GetPluginDirectory()}/docs/WELCOME.txt");
    // or: builder.SetWelcome("[b]Thanks for installing My Plugin![/b]\n\nOpen [i]Project > Tools > My Plugin[/i] to start.");
}
```

A value that starts with `res://` is a path, anything else is the welcome text. `SetWelcome` replaces the `welcome_file` of `plugin.cfg` and the README.

## When it is shown

The welcome dialog opens once a plugin is installed and enabled: after an ePlugin's recipe was installed, or after a plain Godot plugin was enabled. It waits until no plugin is being enabled, disabled or updated, and until no [license](plugin-licenses.md) dialog is open. A plugin whose license has to be accepted therefore shows its license first and its welcome page afterwards.

When several plugins are installed together, for example a plugin with the dependencies it enables, their welcome pages are collected into one dialog. The list on the left shows each plugin; select one to read its page. Close the dialog when you are done: closing it counts every page in it as read. Pages do not have to be opened one by one.

At startup and after C# assembly reloads, the framework also checks the enabled plugins whose welcome page was not shown yet, for example plugins enabled while the editor was closed, or enabled before the framework supported welcome pages. Their pages are shown then.

A plugin's welcome page is shown once per project, not once per developer: disabling and enabling the plugin again does not show it again. An editor restart or assembly reload while the dialog is open closes it without counting the pages as read, so they are shown again afterwards. In a headless editor no dialog can be shown and nothing is recorded.

## Updates

Updates never show a welcome page, also not when the new version changes it or has one for the first time. Installing an update records the updated plugins' welcome pages as shown.

## Reading a welcome page again

The plugin details of the [ePlugin Manager](eplugin-manager.md) link each plugin's welcome page. Click it to open the page again. A configured welcome file that is missing or cannot be read is shown as an error there.

## Recorded welcome pages

Shown welcome pages are recorded in the `welcomes` list of `res://addons/eplugin-state.json`, the [plugin state file](plugin-state.md), next to the plugin states and accepted licenses. Each entry holds the plugin slug, the plugin version and the time it was shown. Commit the file with the addon files, so teammates are not shown pages that were already shown for the project.

The `welcomes` list is left out while no welcome page has been shown, so the file stays readable by framework versions without welcome page support. Once it holds entries, such older versions cannot read the file. If the file cannot be changed on disk in the meantime, pages shown in that state count as shown until the editor restarts and are shown again afterwards.
