#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal;

internal static class LoggingSettings
{
    /// <summary>
    /// Logs the dotnet commands ePlugin runs and more detail about dependency resolution. Read when the framework
    /// starts, so a change applies after the next assembly reload or editor restart.
    /// </summary>
    public const string DebugKey = "eplugin/logging/debug";

    public static bool Debug => ProjectSettings.GetSetting(DebugKey, false).AsBool();

    public static void Register()
    {
        if (!ProjectSettings.HasSetting(DebugKey)) ProjectSettings.SetSetting(DebugKey, false);
        ProjectSettings.SetInitialValue(DebugKey, false);
        ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            { "name", DebugKey }, { "type", (int)Variant.Type.Bool }, { "hint", (int)PropertyHint.None }, { "hint_string", "" }
        });
    }
}
#endif
