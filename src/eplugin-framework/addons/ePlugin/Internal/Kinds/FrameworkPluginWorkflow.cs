#if TOOLS
using System.Collections.Generic;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// ePlugin Framework itself. Its update is a self-update: it stays enabled, its folder is moved as a whole, and
/// <see cref="UpdateApplier"/> and <see cref="UpdateRecovery"/> always roll a failed one back (no keeping on build
/// failure, no downgrade, early recovery after two unhealthy starts).
/// </summary>
internal sealed class FrameworkPluginWorkflow : PluginKindWorkflow
{
    public static readonly FrameworkPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.Framework;
    public override string Name => "ePlugin Framework";
    public override bool DisabledDuringSwap => false;

    public override IEnumerable<Finding> Validate(KindValidation validation)
    {
        // UpdateRecovery restores the framework by moving folders before any C# code could unload a library.
        if (RefuseNativeExtension(validation) is { } native) yield return native;
    }
}
#endif
