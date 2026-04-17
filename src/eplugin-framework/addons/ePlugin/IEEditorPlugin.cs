using Godot;

namespace Enaweg.Plugin;

/// <summary>
/// Base interface to extend editor plugins with.
///
/// NOTES: This was originally an abstract base class derived from <see cref="EditorPlugin"/>. Due to C# bugs in Godot
/// (4.5.x, 4.6.x), it was converted to an interface to avoid build issues.
/// </summary>
public interface IEEditorPlugin
{
    public EditorPlugin GodotPlugin { get; }
    
    internal void Bootstrap(IEEditorPluginBuilder builder);
}