#if TOOLS
namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// A C# plugin without a recipe. Its update disables it, moves its folder as a whole, builds the project once and
/// restarts the editor into the new assembly, then enables it again.
/// </summary>
internal sealed class CSharpPluginWorkflow : PluginKindWorkflow
{
    public static readonly CSharpPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.CSharp;
    public override string Name => "C# plugin";
    public override bool DisabledDuringSwap => true;
}
#endif
