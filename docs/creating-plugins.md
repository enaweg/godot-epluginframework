# Create an ePlugin

An ePlugin is a Godot `EditorPlugin` that also implements `IEEditorPlugin`. Its `CreateRecipe` method declares project changes, and `EnableEPlugin` / `DisableEPlugin` connect those declarations to the plugin lifecycle.

Like every plugin ePlugin supports, an ePlugin needs a `plugin.cfg` in its folder below `res://addons`; add-ons without one are not supported, whatever their type, except GDExtensions in their own folder. GDScript plugins get updates, licenses, welcome pages and links through `plugin.cfg` alone; see [GDScript plugins](gdscript-plugins.md). Plugins that ship a GDExtension get the same; see [GDExtension plugins](gdextension-plugins.md).

## Starter plugin

```csharp
#if TOOLS
using Godot;
using Enaweg.Plugin;

namespace MyPlugin;

[Tool]
public partial class SamplePlugin : EditorPlugin, IEEditorPlugin
{
    public void CreateRecipe(IEEditorPluginBuilder builder)
    {
        builder
            .AddNuget("Serilog", "4.0.0")
            .AddProject("res://addons/my_plugin/.support/Support.csproj", "My Plugin", true)
            .AddAutoload("MyPluginService", "res://addons/my_plugin/service.tscn")
            .AddDirectory("res://addons/my_plugin/.src");
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

`CreateRecipe` should be deterministic and side-effect free. The framework calls it to determine what to install and remove, including when it reconciles an enabled plugin after an update. Keep the plugin's root code compilable with only Godot and ePlugin; put code that requires a new NuGet package or project reference in a managed directory or project declared by the recipe.

`EnableEPlugin()` and `DisableEPlugin()` report lifecycle progress automatically. For custom editor work, use `EPlugin.Progress.Begin(...)` and report through the returned disposable `IProgress<string>` scope. See [Progress dialog](progress-dialog.md#reporting-progress-from-a-c-editor-plugin) for examples and the public interfaces. Keep recipe creation free of progress side effects.

## Recipe resources

`IEEditorPluginBuilder` supports these declarations:

- `AddNuget(name, version?, source?)` adds a NuGet package. Omit the version to use the latest stable version. `AddNugets(names...)` adds several packages. Feeds at an http(s) URL or inside the project (`res://` or a project-relative path) are recorded in the project root's `nuget.config` (or in the project's existing `NuGet.config` or `NuGet.Config`, which NuGet would otherwise stop reading), so other checkouts restore the package too. A feed elsewhere (`user://`, or an absolute path outside the project) is only passed to `dotnet add package` and logged as a warning: it works on the machine that enabled the plugin, but a fresh checkout cannot restore the package. Recipe packages and project references are added to the game's main project, so they are also built into exported games.
- `AddProject(path, addReference?)` adds a project to the solution (the one Godot uses, in the folder of the `dotnet/project/solution_directory` project setting, by default the project root: `<assembly>.sln`, else `<assembly>.slnx`, else the only solution in that folder) and, unless `addReference` is `false`, references it from the main Godot project. `AddProject(path, virtualFolderName, addReference?)` also places it under a solution folder (`null` for none). The path may be a `res://` path, relative to the project root, or absolute. Keep each project in its own folder whose name starts with `.`, such as `.support`: the main Godot project compiles every `.cs` file below it except in hidden folders, so it would otherwise compile the project's sources too, and a project in the plugin's own folder would also compile the plugin's script. Godot exports a release in the `ExportRelease` configuration, which a referenced project inherits, but a plain `Microsoft.NET.Sdk` project optimizes only its `Release` configuration. Unless the project optimizes `ExportRelease` itself, as `Godot.NET.Sdk` projects do, the framework therefore makes the reference (`SetConfiguration`) and the solution build it as `Release` in `ExportRelease`, so exported games get the optimized library. If that cannot be written, a warning is logged and the game still builds, with the library unoptimized.
- `AddAutoload(name, path)` registers an autoload singleton while the plugin is enabled. Autoloads are part of the game, so its script must compile outside the editor: don't wrap it in `#if TOOLS`.
- `AddDirectory(path)` makes a plugin directory available while enabled and hides it while disabled, by renaming it with or without a leading `.` (and setting the hidden attribute on Windows). Ship managed source directories in hidden form, such as `.src`, so they are inert until activation. A shown directory is part of the game and is exported with it, so wrap code in it that only the editor uses in `#if TOOLS`. The path may name either form (`src` or `.src`); a relative path is relative to the project (`res://`), not to the editor's working directory. A directory already in the wanted state is left alone and a missing one is only reported as a warning; only both forms existing at once fails. The directory must be inside the plugin's directory: one outside it (another plugin's, `res://.godot`) or the plugin's own directory or one that holds it (such as `res://addons`, which would hide the plugin itself) fails the activation, and disabling the plugin leaves such a directory as it is, with a warning. A GDExtension (a `.gdextension` file and its libraries) in a managed directory must be reloadable: showing the directory loads it, and hiding it unloads it first, because Godot keeps its libraries open and the directory could otherwise not be renamed. A managed directory with a GDExtension that is not reloadable fails the activation (see [GDExtension plugins](gdextension-plugins.md#gdextensions-in-managed-directories)).
- `AddPluginDependency(slug, version?)` requires another C#, GDScript or GDExtension plugin, or a [GDExtension without `plugin.cfg`](gdextension-plugins.md#gdextensions-without-plugincfg), which is always loaded and never enabled for it. The framework enables it first and disables dependants if it is turned off. When it does not come up, because Godot does not enable it or an ePlugin dependency never activates (its `_EnablePlugin` throws or does not call `EnableEPlugin`), the dependant's activation fails with *Plugin dependency … did not activate!*. A version such as `">=2.0.0"` means that version or higher (`">2.0.0"` means the same, for older recipes); an exact version such as `"1.2.3"` requires that version. Versions are written like `plugin.cfg` versions, with an optional `v`, two to four numbers, a prerelease suffix and build metadata, and only their numbers are compared: `1.2` equals `1.2.0`, and `1.2.3-beta.1` satisfies `1.2.3`. Every recipe already depends on `ePlugin`; naming it adds a version constraint. A plugin that depends on itself, also through other plugins, fails to activate and the cycle is logged.
- `SetLicense(license)` sets the plugin's license, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin licenses](plugin-licenses.md#in-the-recipe).
- `SetWelcome(welcome)` sets the welcome page shown once after the plugin is installed, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin welcome pages](plugin-welcome.md#in-the-recipe).
- `AddOptionalPluginDependency(slug, version, recipe)` declares resources needed only when another plugin is already enabled and matches the constraint (`null` for any version). The other plugin is never enabled automatically and a missing or mismatched plugin does not fail activation.

Declarations are checked when the recipe is created: an empty name or path, an invalid NuGet package id or project path, a malformed plugin slug or version constraint, and declarations that contradict each other (one package with two versions, a plugin that is both a required and an optional dependency) throw an `ArgumentException`. The activation then fails with that message, shown in the ePlugin Manager. Declaring the same thing twice counts once, except an optional dependency, whose resources must be declared in one call.

Deactivation reverses the recipe. Removing a NuGet package, project reference, or solution entry that is already gone, for example because it was removed by hand, is reported as a warning and counts as done. A NuGet package, NuGet source, project reference, solution entry, autoload, or managed directory that another installed recipe also declares, of another enabled plugin (also one whose activation failed, which may have installed it) or another of the plugin's own optional recipes, stays until the last recipe declaring it is reversed. An autoload name that another installed recipe already uses with a different path fails the activation. Enabling an ePlugin while ePlugin Framework itself is disabled enables the framework first, and a plugin enabled while the framework is still starting is installed once it has started. A plugin disabled while ePlugin Framework itself is disabled or not started keeps its recipe installed, and a warning says so: enable the framework, then enable and disable the plugin again to remove it.

Optional dependencies are re-evaluated when either plugin is enabled or disabled, also when the optional plugin is a GDScript plugin toggled in **Project Settings > Plugins**, so activation order does not matter. Disabling the optional plugin removes the nested recipe while leaving the declaring plugin active. Nested recipes receive `IEEditorPluginRecipeBuilder`, which supports only resources: no dependencies of their own, and no `SetLicense` or `SetWelcome`.

```csharp
builder.AddOptionalPluginDependency("other-plugin", ">=1.0.0", optional => optional
    .AddNuget("MyPlugin.Integration")
    .AddProject("res://addons/my_plugin/.integration/Integration.csproj")
    .AddAutoload("IntegrationService", "res://addons/my_plugin/integration.tscn")
    .AddDirectory($"{this.GetPluginDirectory()}/.optional-src"));
```

## License

Ship a `LICENSE` file in the plugin's root directory; the ePlugin Manager shows it in the plugin details. Set `license_required=true` in `plugin.cfg` to have users accept the license before the plugin is enabled. `license_file` in `plugin.cfg` or `SetLicense` in the recipe provide a different license. See [Plugin licenses](plugin-licenses.md).

## Documentation and source links

Add `documentation_url` and `source_url` to the `[plugin]` section of `plugin.cfg` to link the plugin's documentation and source code in the ePlugin Manager's plugin details. They work for every plugin, not only ePlugins:

```ini
[plugin]

name="My Plugin"
version="1.0.0"
script="MyPlugin.cs"
documentation_url="https://example.org/my-plugin/docs"
source_url="https://github.com/owner/my-plugin"
```

Both are optional. Clicking an `http` or `https` link opens it in the system browser; any other value, such as an SSH Git address, is shown as text. `EditorPluginExtensions.ReadMetadata()` returns them as `DocumentationUrl` and `SourceUrl`.

## Welcome page

The framework shows a plugin's welcome page once per project after the plugin is installed. By default it is the plugin's README (`README.md`, `README.txt` or `README`); `welcome_file` in `plugin.cfg` or `SetWelcome` in the recipe provide a BBCode page instead. A plugin without any of these shows nothing. See [Plugin welcome pages](plugin-welcome.md).

## Plugin helpers and startup initializers

The `EditorPlugin` extensions `GetPluginSlug()`, `GetPluginDirectory()`, `ReadMetadata()`, and `Cli()` help plugin code locate plugin files, read metadata, and access the .NET CLI abstraction. `EditorPluginExtensions.ReadMetadata(path)` reads any `plugin.cfg`. `Cli()` returns `null` while the framework has not started yet; don't call it from `CreateRecipe`, which must stay free of side effects. `EPlugin.Instance` is the running framework as an `IEPlugin` (`null` while it is disabled), with its `Logger` and whether `EnableDebugLogging` is on. The plugin's directory is the one with its `plugin.cfg`, also when `plugin.cfg` names a script in a subfolder (`script="src/MyPlugin.cs"`). The [added-code sample](../src/eplugin-framework/addons/sample_addedcode_plugin) and the [optional-dependency sample](../src/eplugin-framework/addons/sample_optional_plugin) use `GetPluginDirectory()` for their managed directories.

Code in a managed directory can implement `IInitialize`. Register it with `EPlugin.RegisterInitializer` to receive its `Initialize(IEPlugin)` callback once the framework is ready, after every startup and C# assembly reload; one registered while the framework is already running is called right away. A module initializer is a convenient place to register it, because it runs whenever the assembly is loaded:

```csharp
#if TOOLS
using System.Runtime.CompilerServices;
using Enaweg.Plugin;

public class MyInitializer : IInitialize
{
    public void Initialize(IEPlugin ePlugin) => ePlugin.Logger.Log("My plugin code is ready.");
}

public static class MyInitializerSetup
{
    [ModuleInitializer]
    public static void Register() => EPlugin.RegisterInitializer(new MyInitializer());
}
#endif
```

Wrap code that uses ePlugin in `#if TOOLS`, like the plugin itself: the framework exists only in the editor, so an exported game does not compile it otherwise.

An `EditorPlugin` that implements `IInitialize` itself is initialized when the framework first sees it, without registering. An initializer that throws is logged and does not keep the others from running. `IEPlugin.Logger` writes to ePlugin's log. The [added-code sample](../src/eplugin-framework/addons/sample_addedcode_plugin) registers its initializer with a module initializer, as shown above.

## Troubleshooting

Turn on **Debug logging** on the **General** tab of the [ePlugin Manager](eplugin-manager.md)'s **Settings**, or `eplugin/logging/debug` in **Project Settings** (with **Advanced Settings** shown), to log every dotnet command ePlugin runs and more detail about dependency resolution. It is read when the framework starts, so it applies after the next C# build or editor restart. If ePlugin itself fails to start, the error is logged and it tries again every 10 seconds.
