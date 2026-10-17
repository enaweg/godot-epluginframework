#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// A C# plugin with a recipe. Its update keeps it enabled: the interim bridge (<see cref="InterimBridge"/>) keeps the old
/// visible resources until the new assembly is loaded, and its recipe is reconciled after the editor restarted into the
/// new code. The folder is moved as a whole, unless the plugin ships a GDExtension: then its files are replaced one by one
/// like a <see cref="GDExtensionPluginWorkflow"/>'s, leaving the editor's runtime copies of the libraries alone. Its update
/// always builds and restarts the editor, which loads the new libraries, so nothing is reloaded in place.
/// GDExtensions in recipe directories (<c>AddDirectory</c>) are not supported.
/// </summary>
internal sealed class EPluginWorkflow : PluginKindWorkflow
{
    public static readonly EPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.EPlugin;
    public override string Name => "ePlugin";
    public override bool DisabledDuringSwap => false;

    public override IAddonSwap Swap(UpdatePluginJournal plugin) => plugin.NativeExtension ? AddonFileSwap.Instance : AddonFolderSwap.Instance;

    public override IEnumerable<Finding> Validate(KindValidation validation)
    {
        if (!validation.StagedFiles.Concat(validation.InstalledFiles).Any(GDExtensionConfig.IsExtensionFile)) yield break;
        foreach (var finding in GDExtensionRules.Validate(validation)) yield return finding;
        yield return new("R21", FindingSeverity.Info, "The editor restarts into the new assembly and loads the new GDExtension libraries with it.");
    }

    public override void AfterSwap(SwappedPlugin swapped)
    {
        if (swapped.Plugin.NativeExtension)
            Trace(swapped.Host, swapped.Plugin, "replaced the files, keeping Godot's runtime copies of the GDExtension libraries; the restart into the new assembly loads the new ones.");
    }
}
#endif
