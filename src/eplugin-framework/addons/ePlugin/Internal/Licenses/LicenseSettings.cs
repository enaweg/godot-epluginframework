#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal.Licenses;

internal static class LicenseSettings
{
    /// <summary>
    /// Accepts every plugin license without showing it. Risky: the user agrees to terms nobody read, for the whole
    /// project. Only set in the project settings, mainly for headless editors such as CI exports.
    /// </summary>
    public const string AutoAcceptKey = "eplugin/licenses/auto_accept";

    public static bool AutoAccept => ProjectSettings.GetSetting(AutoAcceptKey, false).AsBool();

    public static void Register()
    {
        if (!ProjectSettings.HasSetting(AutoAcceptKey)) ProjectSettings.SetSetting(AutoAcceptKey, false);
        ProjectSettings.SetInitialValue(AutoAcceptKey, false);
        ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            { "name", AutoAcceptKey }, { "type", (int)Variant.Type.Bool }, { "hint", (int)PropertyHint.None }, { "hint_string", "" }
        });
    }
}
#endif
