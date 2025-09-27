#if TOOLS
using System;

namespace Enaweg.Plugin;

[Serializable]
public abstract class PluginSettingsBase
{
    public string Version { get; set; }
}
#endif