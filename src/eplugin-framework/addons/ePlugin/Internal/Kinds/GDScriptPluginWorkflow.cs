#if TOOLS
namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// A plugin whose entry script is GDScript. Its update disables it, moves its folder as a whole and enables it again;
/// without C# files it needs neither a build nor a restart.
/// </summary>
internal sealed class GDScriptPluginWorkflow : PluginKindWorkflow
{
    public static readonly GDScriptPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.GDScript;
    public override string Name => "GDScript plugin";
    public override bool DisabledDuringSwap => true;
}
#endif
