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
        // The framework ships no GDExtension. UpdateRecovery restores its folder at startup, when Godot has loaded every
        // extension already, so one inside it could not be replaced.
        if (RefuseNativeExtension(validation) is { } native) yield return native;
    }
}
#endif
