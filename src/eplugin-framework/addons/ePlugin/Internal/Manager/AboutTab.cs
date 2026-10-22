#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The About tab of the settings: the ePlugin Framework's logo, version and description with links to its store page,
/// source code and documentation, and a request for a star on GitHub. The layout lives in SettingsDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class AboutTab : VBoxContainer
{
    private TextureRect _logo = null!;
    private Label _version = null!;
    private Label _description = null!;
    private LinkButton _store = null!;
    private LinkButton _source = null!;
    private LinkButton _documentation = null!;
    private PanelContainer _starNote = null!;
    private LinkButton _star = null!;

    public void Initialize()
    {
        _logo = GetNode<TextureRect>("%AboutLogo"); _version = GetNode<Label>("%AboutVersion"); _description = GetNode<Label>("%AboutDescription");
        _store = GetNode<LinkButton>("%StoreLink"); _source = GetNode<LinkButton>("%SourceLink"); _documentation = GetNode<LinkButton>("%DocumentationLink");
        _starNote = GetNode<PanelContainer>("%StarNote"); _star = GetNode<LinkButton>("%StarLink");
    }

    /// <summary>Shows <paramref name="framework"/>, the manager's entry for the framework, or the defaults without one.</summary>
    public void Open(PluginInfo? framework)
    {
        var about = AboutInfo.From(framework);
        _logo.Texture = EditorIcons.LargeLogo;
        _version.Text = about.VersionLine;
        _description.Text = about.Description;
        _description.Visible = about.Description.Length > 0;
        Show(_store, about.StoreUrl); Show(_source, about.SourceUrl); Show(_documentation, about.DocumentationUrl);
        Show(_star, about.StarUrl);
        _starNote.Visible = about.StarUrl is not null;
        _starNote.AddThemeStyleboxOverride("panel", StarNoteStyle());
    }

    // A LinkButton opens its Uri in the system browser when it is clicked.
    private static void Show(LinkButton link, string? url)
    {
        link.Uri = url ?? "";
        link.TooltipText = url ?? "";
        link.Visible = url is not null;
    }

    /// <summary>A box in the editor's accent color, read on every open so it follows theme changes.</summary>
    private static StyleBoxFlat StarNoteStyle()
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        var accent = theme is not null && theme.HasColor("accent_color", "Editor") ? theme.GetColor("accent_color", "Editor") : new Color(0.44f, 0.73f, 0.98f);
        var scale = EditorInterface.Singleton.GetEditorScale();
        var style = new StyleBoxFlat { BgColor = accent with { A = 0.15f }, BorderColor = accent with { A = 0.6f } };
        style.SetBorderWidthAll(Mathf.Max(1, (int)scale));
        style.SetCornerRadiusAll((int)(4 * scale));
        style.SetContentMarginAll(8 * scale);
        return style;
    }

}

/// <summary>What the About tab shows, from the framework's plugin.cfg; links that are not web URLs are left out.</summary>
internal sealed record AboutInfo(string VersionLine, string Description, string? StoreUrl, string? SourceUrl, string? DocumentationUrl,
    string? StarUrl)
{
    /// <summary>The framework's page in the Godot Asset Store, which plugin.cfg has no key for.</summary>
    internal const string StorePage = "https://store.godotengine.org/asset/enaweg/eplugin-framework/";
    internal const string DefaultSource = "https://github.com/enaweg/godot-epluginframework";
    /// <summary>
    /// The project's GitHub page, whose Star button is at the top right. GitHub has no link that stars a repository, and
    /// a source_url of a fork is not the project to star.
    /// </summary>
    internal const string StarPage = DefaultSource;
    internal const string DefaultDocumentation = "https://github.com/enaweg/godot-epluginframework#documentation";

    public static AboutInfo From(PluginInfo? framework)
    {
        var version = framework?.Version is { Length: > 0 } v ? $"Version {v}" : "Version unknown";
        if (framework?.Author is { Length: > 0 } author) version += $" · by {author}";
        return new(version, framework?.Description.Trim() ?? "", WebUrl(StorePage),
            WebUrl(framework?.SourceUrl ?? DefaultSource), WebUrl(framework?.DocumentationUrl ?? DefaultDocumentation),
            WebUrl(StarPage));
    }

    private static string? WebUrl(string url) => PluginManagerViewModel.IsWebUrl(url) ? url : null;
}
#endif
