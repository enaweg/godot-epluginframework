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
    
    public string Name { get; init; } = pluginBase.ReadMetadata()?.Name ?? pluginBase.GetPluginSlug();

    public EEditorPluginMetadata? Metadata { get; init; } = pluginBase.ReadMetadata();

    public string? Directory { get; init; } = pluginBase.GetPluginDirectory();

    public ILogger? Logger { get; init; } = logger;

    public IEEditorPlugin? Plugin { get; init; } = plugin;

    public EditorPlugin PluginBase { get; init; } = pluginBase;

    public IDotnetCli? Cli { get; init; } = EGlobal.Instance.GetCli(logger);

    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
    public EEditorPluginBuilder Builder { get; init; } = EEditorPluginBuilder.Create();

    public bool IsRecipeCreated { get; set; } = false;

    /// <summary>
    /// The optional dependencies whose nested recipe is currently installed for this plugin. Uninstall
    /// reverses exactly these. <see langword="null"/> means no snapshot is available (the plugin was never
    /// activated in this session, e.g. after an assembly reload) and the optional dependencies have to be
    /// resolved against the current editor state instead.
    /// </summary>
    public List<EEditorPluginRecipe.OptionalPlugin>? AppliedOptionalDependencies { get; set; } = null;

    public Exception? ErrorDetail { get; set; } = null;
}

#endif