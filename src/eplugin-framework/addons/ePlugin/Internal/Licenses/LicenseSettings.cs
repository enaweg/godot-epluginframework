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

    public static void Register() => EPluginSettings.Register(AutoAcceptKey, false, Variant.Type.Bool);
}
#endif
