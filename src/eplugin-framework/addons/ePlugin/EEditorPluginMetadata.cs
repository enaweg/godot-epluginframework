#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Strongly-typed view of a plugin's <c>plugin.cfg</c> file (the <c>[plugin]</c> section).
/// </summary>
/// <remarks>
/// Returned by <see cref="EditorPluginExtensions.ReadMetadata"/>. Each property mirrors the
/// corresponding key in <c>plugin.cfg</c>; values default to <see langword="null"/> when the
/// underlying key is missing or empty.
/// </remarks>
public class EEditorPluginMetadata
{
    /// <summary>Release page or git URL used to check for plugin updates.</summary>
    public string? UpdateUrl { get; set; }

    /// <summary>Display name of the plugin (the <c>name</c> key in <c>plugin.cfg</c>).</summary>
    public string Name { get; set; }

    /// <summary>Human-readable description of the plugin (the <c>description</c> key).</summary>
    public string Description { get; set; }

    /// <summary>
    /// Plugin version string (the <c>version</c> key), expected in <c>[major].[minor].[patch]</c>
    /// form when used with <see cref="IEEditorPluginBuilder.AddPluginDependency"/> version constraints.
    /// </summary>
    public string Version { get; set; }

    /// <summary>Author of the plugin (the <c>author</c> key).</summary>
    public string Author { get; set; }

    /// <summary>Where the plugin's documentation is (the <c>documentation_url</c> key).</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>Where the plugin's source code is (the <c>source_url</c> key).</summary>
    public string? SourceUrl { get; set; }
}
#endif