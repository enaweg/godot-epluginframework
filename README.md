# ePlugin Framework

Extended C# Godot Plugins.

![Godot 4.3](https://img.shields.io/badge/Godot-v4.3-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)
![Godot 4.4](https://img.shields.io/badge/Godot-v4.4-202020?logo=godot-engine&logoColor=blue&color=darkgreen&labelColor=202020)
![Godot 4.5](https://img.shields.io/badge/Godot-v4.5-202020?logo=godot-engine&logoColor=blue&color=darkorange&labelColor=202020)

![Dotnet 9](https://img.shields.io/badge/9-02020?logo=dotnet&logoSize=auto&logoColor=purple&color=darkgreen&labelColor=E0E0E0)

**NOTE**: This is currently in an experimental state and very much WIP!

## Features

+ Easy to use fluent API for plugins
+ on plugin activation/deactivation handling of
    + Project References
    + NuGet References
    + include/exclude Asset directories (for additional content)
+ Plugin Migration (version upgrades)
+ Plugin Dependencies (when a root plugin is disabled all dependant plugins will be disabled too)
+ Persistent plugin data

## Why?

Godot's current plugin system has a few major drawbacks (especially for C#)

+ Editor plugin code lives in same project as game code (every change to C# code will trigger a AssemblyContext reload
  loosing all state)
+ C# specific features like project references or nuget packages are not supported (for plugins)
+ Code that uses external references can not be compiled (installing C# plugins is complex)
+ for plugins in separate projects or nugets Godot does not find global classes, which makes it impossible to
  externalize components (see: [issue #95036](https://github.com/godotengine/godot/issues/95036))

As long as the state of Godot's plugin system and C# integration is as it is now, this extending framework tries to
provide some of the missing pieces for C# Plugins.

## Drawbacks

+ Activating/Deactivating a ePlugin will freeze Godot's UI for the duration of installation/deinstallation of that
  plugin
+ if an error happens during installation or deinstallation the projects might be left in a non-compilable state and
  needs manual intervention.

## What is not possible?

+ The current system does not allow for external Assets (Scenes, Models, Scripts, aso.) as Godot needs a unique Id for
  each Asset and this is not supported for external elements.

## Testing

This needs more external plugins and testing to move forward. Feel free to participate and provide feedback.

Tested on:

+ Godot 4.5 + .NET 9 (regression was introduced, does not work as expected, )
+ Godot 4.4.x + .NET 9

# Example Code (Basic Plugin)

```C#
#if TOOLS
using Godot;
using Enaweg.Plugin;

namespace Enaweg.Sample;

[Tool]
public partial class plugin : EEditorPlugin
{
    internal override void Bootstrap(IEEditorPluginBuilder builder)
    {
        builder
            // add multiple nugets at once (latest stable releases)
            .AddNuget("Sample.Nuget.Package1a", "Sample.Nuget.Package1b")
            
            // add specific nuget versions from a source URL
            .AddNuget("Sample.Nuget.Package2", ">2.0", "https://www.nuget.org/")
            
            // add specific nuget version from a local directory
            .AddNuget("Sample.Nuget.Package2", ">2.0", "res://path-to-nuget-directory")
            
            // add a dependency to any plugin (C# or normal GDScript Plugin)
            .AddPluginDependency("other-plugin", "2.0")
            
            // add autoload
            .AddAutoload("ResourceName", "res://path-to-resource")
            
            // add project reference to solution (and Godot's project if last parameter is true)
            // projects can be included in a hidden directory
            .AddProject("project path", "virtual Folder", true)
            
            // add a directory to show/hide depending on plugin state
            // plugins need to be provided in a deactivated state to users
            .AddDirectory($"{PluginDirectory}/.src");
    }
}

#endif
```

## Example Code (Persisted Plugin)

```C#
#if TOOLS
using Enaweg.Plugin;
using Godot;

namespace Enaweg.SamplePersisted;

[Tool]
public partial class PersistedPlugin : EEditorPluginPersisted<PersistedPluginData>
{
    internal override void Bootstrap(IEEditorPluginBuilder builder)
    {
       // ... setup using builder
    }
}
#endif
```

```C#
#if TOOLS
using Enaweg.Plugin;

namespace Enaweg.SamplePersisted;

public class PersistedPluginData :  PluginSettingsBase
{
    public string YourPluginDataProperty { get; set; }
}
#endif
```

## Contribute

Feel free to contribute with Documentation, Testing, or PRs.

## Roadmap

* stabilize current API
* improve documentation
* automated testing
* add simple UI API (show progress for plugins loading)
* provide more APIs for plugins to use (as needed)
    * Automatic plugin update system using source URL
    * Plugin specific UI templates (licenses, feedback, Welcome screen)

