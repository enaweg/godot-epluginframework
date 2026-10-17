#if TOOLS
namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// A plugin whose entry script is missing or neither GDScript nor C#, and every plugin of an update journal written
/// before journals recorded the kind (except ePlugins). Updated like a GDScript plugin: disabled, its folder moved as a
/// whole, enabled again.
/// </summary>
internal sealed class UnknownPluginWorkflow : PluginKindWorkflow
{
    public static readonly UnknownPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.Unknown;
    public override string Name => "Plugin";
    public override bool DisabledDuringSwap => true;
}
#endif
