#if TOOLS
using System;

namespace Enaweg.Plugin.Internal;

internal sealed class PluginContext(EEditorPlugin plugin)
{
    public string Slug { get; init; } = plugin.PluginSlug;
    public string Name { get; init; } = plugin.PluginSlug;
    
   
    public EEditorPlugin? Plugin { get; set; } = plugin;
    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
 

    public bool IsFirstActivation { get; set; }

    public uint FailedTries { get; set; } = 0;

    public Exception? ErrorDetail { get; set; }
}

#endif