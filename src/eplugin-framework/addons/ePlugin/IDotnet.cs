#if TOOLS
namespace Enaweg.Plugin;

/// <summary>
/// Main entry point to the .NET Cli.
/// </summary>
public interface IDotnet
{
    /// <summary>
    /// The entry point for calls to .NET cli.
    /// </summary>
    IDotnetCli Call { get; }
}
#endif