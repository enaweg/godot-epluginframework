#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The colors of the status words in the manager's and its dialogs' BBCode texts. The pure models read them as text, so
/// they stay free of Godot; <see cref="Use"/> takes the editor theme's status colors, which Godot darkens for a light
/// theme. Without an editor, e.g. in tests, they are fixed colors for a dark theme.
/// </summary>
internal static class TextColors
{
    public static string Error { get; private set; } = "#ff7070";
    public static string Warning { get; private set; } = "#ffd070";
    public static string Success { get; private set; } = "#70e070";
    public static string Info { get; private set; } = "#a0c0ff";

    /// <summary>Takes the status colors of <paramref name="theme"/>, the editor's when its windows are prepared or it changes.</summary>
    public static void Use(Theme? theme)
    {
        Error = Of(theme, "error_color", "#ff7070");
        Warning = Of(theme, "warning_color", "#ffd070");
        Success = Of(theme, "success_color", "#70e070");
        Info = Of(theme, "accent_color", "#a0c0ff");
    }

    /// <summary>The theme's color <paramref name="name"/> of the editor as a BBCode color, else <paramref name="fallback"/>.</summary>
    internal static string Of(Theme? theme, string name, string fallback) =>
        theme is not null && theme.HasColor(name, "Editor") ? "#" + theme.GetColor(name, "Editor").ToHtml(false) : fallback;
}
#endif
