#if TOOLS
using System.Collections.Generic;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// A C# plugin with a recipe. Its update keeps it enabled: the folder is moved as a whole, the interim bridge
/// (<see cref="InterimBridge"/>) keeps the old visible resources until the new assembly is loaded, and its recipe is
/// reconciled after the editor restarted into the new code.
/// </summary>
internal sealed class EPluginWorkflow : PluginKindWorkflow
{
    public static readonly EPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.EPlugin;
    public override string Name => "ePlugin";
    public override bool DisabledDuringSwap => false;

    public override IEnumerable<Finding> Validate(KindValidation validation)
    {
        // The plugin stays enabled during its update, and the bridge moves folders that a loaded library would hold.
        if (RefuseNativeExtension(validation) is { } native) yield return native;
    }
}
#endif
