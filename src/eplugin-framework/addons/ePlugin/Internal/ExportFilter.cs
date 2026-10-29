#if TOOLS
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Leaves the framework's editor-only files (<see cref="ExportExclusions"/>) out of every export, like an exclude filter
/// in each export preset. Godot keeps its presets in memory and offers no API to change them, so the framework does not
/// write the filter into <c>export_presets.cfg</c>: the editor would overwrite it with its own copy.
/// </summary>
[Tool]
internal sealed partial class ExportFilter : EditorExportPlugin
{
    public override string _GetName() => "ePlugin";

    public override void _ExportFile(string path, string type, string[] features)
    {
        if (ExportExclusions.Excludes(path)) Skip();
    }
}
#endif
