#if TOOLS
using System.Text.Json.Serialization;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// The kinds of plugins below <c>res://addons</c> that ePlugin tells apart. Each has its own workflow
/// (<see cref="PluginKinds.Workflow(PluginKind)"/>), so what happens to a plugin of one kind can be followed in one class.
/// Persisted by name in update journals; add new kinds without renaming the old ones.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PluginKind
{
    /// <summary>ePlugin Framework itself.</summary>
    Framework,
    /// <summary>A C# plugin implementing <see cref="IEEditorPlugin"/>: its recipe is installed and reconciled.</summary>
    EPlugin,
    /// <summary>A C# plugin without a recipe.</summary>
    CSharp,
    /// <summary>A plugin whose entry script is GDScript.</summary>
    GDScript,
    /// <summary>
    /// A plugin that is not an ePlugin and ships a GDExtension (a <c>.gdextension</c> file and its native libraries)
    /// next to its <c>plugin.cfg</c>, whatever its entry script's language.
    /// </summary>
    GDExtension,
    /// <summary>A plugin whose entry script is missing or neither GDScript nor C#.</summary>
    Unknown
}
#endif
