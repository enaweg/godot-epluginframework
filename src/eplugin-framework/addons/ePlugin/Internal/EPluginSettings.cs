#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>Registers ePlugin's <c>eplugin/*</c> project settings, which the settings dialog's General tab shows.</summary>
internal static class EPluginSettings
{
    /// <summary>
    /// Registers <paramref name="key"/> with its default and editor hint. A value the project already set is kept; the
    /// default is not written to <c>project.godot</c>.
    /// </summary>
    public static void Register(string key, Variant defaultValue, Variant.Type type, PropertyHint hint = PropertyHint.None,
        string hintString = "")
    {
        if (!ProjectSettings.HasSetting(key)) ProjectSettings.SetSetting(key, defaultValue);
        ProjectSettings.SetInitialValue(key, defaultValue);
        ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            { "name", key }, { "type", (int)type }, { "hint", (int)hint }, { "hint_string", hintString }
        });
    }
}
#endif
