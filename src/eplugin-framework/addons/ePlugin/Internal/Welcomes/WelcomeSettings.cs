#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal.Welcomes;

internal static class WelcomeSettings
{
    /// <summary>
    /// Never opens the welcome dialog. Pages are not recorded as shown, so turning it off again shows the pages of the
    /// enabled plugins that were not shown yet; the ePlugin Manager still links every page.
    /// </summary>
    public const string HideKey = "eplugin/welcomes/hide";

    public static bool Hide => ProjectSettings.GetSetting(HideKey, false).AsBool();

    public static void Register() => EPluginSettings.Register(HideKey, false, Variant.Type.Bool);
}
#endif
