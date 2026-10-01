#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal.Update;

internal static class UpdateSettings
{
    public static void Register()
    {
        Add("check_enabled", true, Variant.Type.Bool);
        Add("check_interval_hours", 20, Variant.Type.Int, PropertyHint.Range, "1,8760,1");
        Add("allow_prerelease", false, Variant.Type.Bool);
        Add("on_build_failure", "ask", Variant.Type.String, PropertyHint.Enum, "ask,rollback,keep");
        Add("restart_policy", "auto", Variant.Type.String, PropertyHint.Enum, "auto,always");
    }
    private static void Add(string name, Variant value, Variant.Type type, PropertyHint hint = PropertyHint.None, string hintString = "")
    {
        var key = "eplugin/updates/" + name;
        if (!ProjectSettings.HasSetting(key)) ProjectSettings.SetSetting(key, value);
        ProjectSettings.SetInitialValue(key, value);
        ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            { "name", key }, { "type", (int)type }, { "hint", (int)hint }, { "hint_string", hintString }
        });
    }
}
#endif
