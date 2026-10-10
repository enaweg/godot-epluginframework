# Create an ePlugin

An ePlugin is a Godot `EditorPlugin` that also implements `IEEditorPlugin`. Its `CreateRecipe` method declares project changes, and `EnableEPlugin` / `DisableEPlugin` connect those declarations to the plugin lifecycle.

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
            .AddProject("res://addons/my_plugin/Support.csproj", "My Plugin", true)
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

## Recipe resources

`IEEditorPluginBuilder` supports these declarations:

- `AddNuget(name, version?, source?)` adds a NuGet package. Omit the version to use the latest stable version. `AddNugets(names...)` adds several packages. External and local feeds are recorded in the project's root `nuget.config`.
- `AddProject(path, addReference?)` adds a project to the solution and optionally references it from the main Godot project. The overload `AddProject(path, virtualFolderName?, addReference?)` also places it under a solution folder.
- `AddAutoload(name, path)` registers an autoload singleton while the plugin is enabled.
- `AddDirectory(path)` makes a plugin directory available while enabled and hides it while disabled. Ship managed source directories in hidden form, such as `.src`, so they are inert until activation.
- `AddPluginDependency(slug, version?)` requires another C# or GDScript plugin. The framework enables it first and disables dependants if it is turned off. A version such as `">2.0.0"` sets a minimum; an exact version such as `"1.2.3"` requires that version.
- `SetLicense(license)` sets the plugin's license, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin licenses](plugin-licenses.md#in-the-recipe).
- `SetWelcome(welcome)` sets the welcome page shown once after the plugin is installed, either as text (BBCode allowed) or as a `res://` path to a file. See [Plugin welcome pages](plugin-welcome.md#in-the-recipe).
- `AddOptionalPluginDependency(slug, version?, recipe)` declares resources needed only when another plugin is already enabled and matches the constraint. The other plugin is never enabled automatically and a missing or mismatched plugin does not fail activation.

Optional dependencies are re-evaluated when either ePlugin-managed plugin is enabled or disabled, so activation order does not matter. Disabling the optional plugin removes the nested recipe while leaving the declaring plugin active. Nested recipes receive `IEEditorPluginRecipeBuilder`, which supports resources but cannot declare dependencies of its own.

```csharp
builder.AddOptionalPluginDependency("other-plugin", ">1.0.0", optional => optional
    .AddNuget("MyPlugin.Integration")
    .AddProject("res://addons/my_plugin/Integration.csproj")
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

The `EditorPlugin` extensions `GetPluginSlug()`, `GetPluginDirectory()`, `ReadMetadata()`, and `Cli()` help recipes locate plugin files, read metadata, and access the .NET CLI abstraction. See the [sample project](../src/eplugin-framework/addons/sample_plugin) for a complete example.

Code in a managed directory can implement `IInitialize`. Register it with `EPlugin.RegisterInitializer` to receive its startup callback once the framework is ready. The [added-code sample](../src/eplugin-framework/addons/sample_addedcode_plugin) demonstrates this pattern.
