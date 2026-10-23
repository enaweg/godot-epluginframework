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

- `AddNuget(name, version?, source?)` adds a NuGet package. Omit the version to use the latest stable version. `AddNugets(names...)` adds several packages. External and local feeds are recorded in the project's root `nuget.config`.
- `AddProject(path, addReference?)` adds a project to the solution and, unless `addReference` is `false`, references it from the main Godot project. `AddProject(path, virtualFolderName, addReference?)` also places it under a solution folder (`null` for none). The path may be a `res://` path, relative to the project root, or absolute. Keep each project in its own folder whose name starts with `.`, such as `.support`: the main Godot project compiles every `.cs` file below it except in hidden folders, so it would otherwise compile the project's sources too, and a project in the plugin's own folder would also compile the plugin's script.
- `AddAutoload(name, path)` registers an autoload singleton while the plugin is enabled.
- `AddDirectory(path)` makes a plugin directory available while enabled and hides it while disabled, by renaming it with or without a leading `.` (and setting the hidden attribute on Windows). Ship managed source directories in hidden form, such as `.src`, so they are inert until activation. The path may name either form (`src` or `.src`). A directory already in the wanted state is left alone and a missing one is only reported as a warning; only both forms existing at once fails, and so does naming the plugin's own directory or one that holds it (such as `res://addons`), which would hide the plugin itself. A GDExtension (a `.gdextension` file and its libraries) in a managed directory must be reloadable: showing the directory loads it, and hiding it unloads it first, because Godot keeps its libraries open and the directory could otherwise not be renamed. A managed directory with a GDExtension that is not reloadable fails the activation (see [GDExtension plugins](gdextension-plugins.md#gdextensions-in-managed-directories)).
- `AddPluginDependency(slug, version?)` requires another C#, GDScript or GDExtension plugin, or a [GDExtension without `plugin.cfg`](gdextension-plugins.md#gdextensions-without-plugincfg), which is always loaded and never enabled for it. The framework enables it first and disables dependants if it is turned off. A version such as `">=2.0.0"` means that version or higher (`">2.0.0"` means the same, for older recipes); an exact version such as `"1.2.3"` requires that version. Versions are written like `plugin.cfg` versions, with an optional `v`, two to four numbers, a prerelease suffix and build metadata, and only their numbers are compared: `1.2` equals `1.2.0`, and `1.2.3-beta.1` satisfies `1.2.3`. Every recipe already depends on `ePlugin`; naming it adds a version constraint. A plugin that depends on itself, also through other plugins, fails to activate and the cycle is logged.
- `SetLicense(license)` sets the plugin's license, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin licenses](plugin-licenses.md#in-the-recipe).
- `SetWelcome(welcome)` sets the welcome page shown once after the plugin is installed, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin welcome pages](plugin-welcome.md#in-the-recipe).
- `AddOptionalPluginDependency(slug, version, recipe)` declares resources needed only when another plugin is already enabled and matches the constraint (`null` for any version). The other plugin is never enabled automatically and a missing or mismatched plugin does not fail activation.

Declarations are checked when the recipe is created: an empty name or path, an invalid NuGet package id or project path, a malformed plugin slug or version constraint, and declarations that contradict each other (one package with two versions, a plugin that is both a required and an optional dependency) throw an `ArgumentException`. The activation then fails with that message, shown in the ePlugin Manager. Declaring the same thing twice counts once, except an optional dependency, whose resources must be declared in one call.

Deactivation reverses the recipe. Removing a NuGet package, project reference, or solution entry that is already gone, for example because it was removed by hand, is reported as a warning and counts as done. A NuGet package, project reference, or solution entry that another installed recipe also declares, of another enabled plugin or another of the plugin's own optional recipes, stays until the last recipe declaring it is reversed.

Optional dependencies are re-evaluated when either plugin is enabled or disabled, also when the optional plugin is a GDScript plugin toggled in **Project Settings > Plugins**, so activation order does not matter. Disabling the optional plugin removes the nested recipe while leaving the declaring plugin active. Nested recipes receive `IEEditorPluginRecipeBuilder`, which supports resources but cannot declare dependencies of its own.

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

The `EditorPlugin` extensions `GetPluginSlug()`, `GetPluginDirectory()`, `ReadMetadata()`, and `Cli()` help recipes locate plugin files, read metadata, and access the .NET CLI abstraction. The [added-code sample](../src/eplugin-framework/addons/sample_addedcode_plugin) and the [optional-dependency sample](../src/eplugin-framework/addons/sample_optional_plugin) use `GetPluginDirectory()` for their managed directories.

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

An `EditorPlugin` that implements `IInitialize` itself is initialized when the framework first sees it, without registering. An initializer that throws is logged and does not keep the others from running. `IEPlugin.Logger` writes to ePlugin's log. The [added-code sample](../src/eplugin-framework/addons/sample_addedcode_plugin) demonstrates this pattern.

## Troubleshooting

Turn on **Debug logging** on the **General** tab of the [ePlugin Manager](eplugin-manager.md)'s **Settings**, or `eplugin/logging/debug` in **Project Settings** (with **Advanced Settings** shown), to log every dotnet command ePlugin runs and more detail about dependency resolution. It is read when the framework starts, so it applies after the next C# build or editor restart. If ePlugin itself fails to start, the error is logged and it tries again every 10 seconds.
