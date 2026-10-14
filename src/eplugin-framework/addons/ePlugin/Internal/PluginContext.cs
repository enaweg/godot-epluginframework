#if TOOLS
using System;
using System.Collections.Generic;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

[Tool]
internal sealed class PluginContext(IEEditorPlugin? plugin, EditorPlugin pluginBase, ILogger? logger)
{
    public string Slug { get; init; } = pluginBase.GetPluginSlug();
    
    public string Name { get; private set; } = pluginBase.ReadMetadata()?.Name ?? pluginBase.GetPluginSlug();

    public EEditorPluginMetadata? Metadata { get; set; } = pluginBase.ReadMetadata();

    /// <summary>Takes the plugin.cfg read again, e.g. after it was repaired or updated.</summary>
    public void RefreshMetadata(EEditorPluginMetadata? metadata)
    {
        Metadata = metadata;
        Name = Metadata?.Name ?? Slug;
    }

    public string? Directory { get; init; } = pluginBase.GetPluginDirectory();

    public ILogger? Logger { get; set; } = logger;

    public IEEditorPlugin? Plugin { get; init; } = plugin;

    public EditorPlugin PluginBase { get; init; } = pluginBase;

    /// <summary>Set by <see cref="EGlobal"/>, which knows the installed dotnet CLI.</summary>
    public IDotnetCli? Cli { get; set; }

    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
    /// <summary>Replaced by an empty one when creating the recipe failed, so a retry does not add to a half recipe.</summary>
    public EEditorPluginBuilder Builder { get; set; } = EEditorPluginBuilder.Create();

    public bool IsRecipeCreated { get; set; }

    /// <summary>
    /// The optional dependencies whose nested recipe is currently installed for this plugin. Uninstall
    /// reverses exactly these. <see langword="null"/> means no snapshot is available (the plugin was never
    /// activated in this session, e.g. after an assembly reload) and the optional dependencies have to be
    /// resolved against the current editor state instead.
    /// </summary>
    public List<EEditorPluginRecipe.OptionalPlugin>? AppliedOptionalDependencies { get; set; }

    public Exception? ErrorDetail { get; set; }
}

#endif
